using System.IO.Compression;
using System.Security.Cryptography;
using Kulvar.Modules.Customers;
using Kulvar.Modules.Support.Domain;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Observability;
using Kulvar.Platform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kulvar.Modules.Operations;

/// <summary>
/// Export worker'ı. İşi kiralar (lease + fencing), şirket kapsamında okur, dosyayı yazar ve işi "Ready" yapar.
/// İçerik sorguları yalnız kurulu şirket kapsamında çalışır: worker'ın BYPASSRLS'i yoktur, başka şirketin satırı dosyaya giremez.
/// Üretim boyunca şirket satırı FOR SHARE kilitlidir: temizleme (FOR UPDATE) export bitmeden başlayamaz, başladıktan sonra export başlayamaz.
/// </summary>
public sealed class ExportRunner(WorkerDb db, IOptions<ExportOptions> options, ILogger<ExportRunner> log)
{
    private readonly ExportOptions _options = options.Value;

    public string WorkerId { get; init; } = $"export-{Environment.ProcessId}-{Guid.NewGuid():N}"[..36];

    /// <summary>Test kancası: dosya yazıldıktan sonra, iş "Ready" yapılmadan hemen önce. Üretimde boştur.</summary>
    internal Func<Guid, Task>? BeforeFinalizeHook { get; set; }

    private sealed record Claimed(Guid Id, Guid TenantId, long LeaseToken, int Attempts);

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var processed = 0;
        while (!ct.IsCancellationRequested && await ClaimAsync(ct) is { } job)
        {
            await ProcessAsync(job, ct);
            processed++;
        }
        return processed;
    }

    private async Task<Claimed?> ClaimAsync(CancellationToken ct)
    {
        var rows = await db.RunAsync(DbScope.None, (session, c) => session.Db.QueryAsync(
            """
            UPDATE app.export_jobs j
            SET status = 'Running', lease_owner = $2, lease_until = $3, lease_token = j.lease_token + 1, attempts = j.attempts + 1, started_at = coalesce(j.started_at, $1)
            WHERE j.id = (
                SELECT id FROM app.export_jobs
                WHERE status = 'Queued' OR (status = 'Running' AND lease_until <= $1)
                ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING j.id, j.tenant_id, j.lease_token, j.attempts
            """,
            r => new Claimed(r.GetGuid(0), r.GetGuid(1), r.GetInt64(2), r.GetInt32(3)), c, session.Now, WorkerId, session.Now.AddSeconds(_options.LeaseSeconds)), ct);
        return rows.SingleOrDefault();
    }

    private async Task ProcessAsync(Claimed job, CancellationToken ct)
    {
        using var activity = Telemetry.Activities.StartActivity("export.run");
        var path = _options.ArtifactPath(job.TenantId, job.Id);
        // Geçici dosya kiralama token'ını taşır: aynı işi devralan başka bir worker bu dosyanın üzerine yazamaz.
        var temp = $"{path}.{job.LeaseToken}.tmp";
        var outcome = "ready";
        try
        {
            outcome = await db.RunAsync(DbScope.None, async (session, c) =>
            {
                var status = await TenantRegistry.LockSharedAsync(session.Db, job.TenantId, c);
                if (status is null or TenantStatus.Purging or TenantStatus.Deleted)
                {
                    await FinishAsync(session, job, "Cancelled", "tenant_gone", c);
                    return "cancelled";
                }
                await session.EnterTenantAsync(job.TenantId, c);

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var counts = await WriteArchiveAsync(session, job, temp, c);
                var size = new FileInfo(temp).Length;
                string sha;
                await using (var stream = File.OpenRead(temp))
                {
                    sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, c));
                }
                if (BeforeFinalizeHook is { } hook)
                {
                    await hook(job.Id);
                }

                // Fencing: kiralama bu worker'da değilse (süresi dolup başkası aldıysa) satır güncellenmez ve dosya yayımlanmaz.
                var updated = await session.Db.ExecuteAsync(
                    """
                    UPDATE app.export_jobs
                    SET status = 'Ready', completed_at = $3, expires_at = $4, size_bytes = $5, sha256 = $6, lease_owner = NULL, lease_until = NULL, failure_code = NULL
                    WHERE id = $1 AND lease_token = $2 AND status = 'Running'
                    """,
                    c, job.Id, job.LeaseToken, session.Now, session.Now.AddHours(_options.RetentionHours), size, sha);
                if (updated != 1)
                {
                    return "lease_lost";
                }
                session.Db.Add(AuditEvent.Create(job.TenantId, null, "export.completed", "export", job.Id, null, counts, session.Now));
                await session.Db.SaveChangesAsync(c);
                File.Move(temp, path, overwrite: true);
                return "ready";
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            outcome = "failed";
            log.LogWarning("Export işi başarısız: {JobId} deneme={Attempts} hata={ExceptionType}", job.Id, job.Attempts, ex.GetType().Name);
            await db.RunAsync(DbScope.None, (session, c) => job.Attempts >= _options.MaxAttempts
                ? FinishAsync(session, job, "Failed", "export_failed", c)
                // Yeniden deneme: iş "Running" kalır, kiralaması deneme sayısıyla artan bir süre sonra dolar ve yeniden alınır (hemen tekrar denenmez).
                : session.Db.ExecuteAsync("UPDATE app.export_jobs SET lease_owner = NULL, lease_until = $3 WHERE id = $1 AND lease_token = $2", c, job.Id, job.LeaseToken, session.Now.AddSeconds(30 * job.Attempts)), ct);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp); // Yayımlanmamış dosya geride kalmaz.
            }
        }
        Telemetry.ExportsCompleted.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    }

    private static Task<int> FinishAsync(DbSession session, Claimed job, string status, string failureCode, CancellationToken ct) =>
        session.Db.ExecuteAsync(
            "UPDATE app.export_jobs SET status = $3, failure_code = $4, completed_at = $5, lease_owner = NULL, lease_until = NULL WHERE id = $1 AND lease_token = $2",
            ct, job.Id, job.LeaseToken, status, failureCode, session.Now);

    private static async Task<object> WriteArchiveAsync(DbSession session, Claimed job, string tempPath, CancellationToken ct)
    {
        int customers, tickets, comments;
        await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            customers = await WriteEntryAsync(zip, "customers.csv", async csv =>
            {
                await csv.WriteRowAsync("id", "name", "company_title", "email", "phone", "notes", "created_at", "updated_at");
                await foreach (var c in session.Db.Set<Customer>().AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).AsAsyncEnumerable().WithCancellation(ct))
                {
                    await csv.WriteRowAsync(c.Id, c.Name, c.CompanyTitle, c.Email, c.Phone, c.Notes, c.CreatedAt, c.UpdatedAt);
                }
            });
            tickets = await WriteEntryAsync(zip, "tickets.csv", async csv =>
            {
                await csv.WriteRowAsync("number", "id", "title", "description", "priority", "status", "customer_id", "assignee_user_id", "created_at", "updated_at", "resolved_at", "closed_at");
                await foreach (var t in session.Db.Set<Ticket>().AsNoTracking().OrderBy(x => x.Number).AsAsyncEnumerable().WithCancellation(ct))
                {
                    await csv.WriteRowAsync(t.Number, t.Id, t.Title, t.Description, t.Priority.ToString(), t.Status.ToString(), t.CustomerId, t.AssigneeUserId, t.CreatedAt, t.UpdatedAt, t.ResolvedAt, t.ClosedAt);
                }
            });
            comments = await WriteEntryAsync(zip, "comments.csv", async csv =>
            {
                await csv.WriteRowAsync("ticket_id", "comment_id", "author_user_id", "body", "created_at");
                await foreach (var c in session.Db.Set<TicketComment>().AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).AsAsyncEnumerable().WithCancellation(ct))
                {
                    await csv.WriteRowAsync(c.TicketId, c.Id, c.AuthorUserId, c.Body, c.CreatedAt);
                }
            });
            var manifest = zip.CreateEntry("manifest.txt");
            await using var writer = new StreamWriter(manifest.Open(), CsvWriter.Encoding);
            await writer.WriteAsync(
                $"Kulvar export\r\ntenant_id: {job.TenantId:D}\r\njob_id: {job.Id:D}\r\ngenerated_at_utc: {session.Now.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}\r\n" +
                $"customers: {customers}\r\ntickets: {tickets}\r\ncomments: {comments}\r\n" +
                "format: CSV, UTF-8 with BOM, comma separated, CRLF line endings, RFC 4180 quoting\r\n" +
                "note: cells starting with = + - @ are prefixed with a single quote (spreadsheet formula injection protection)\r\n");
        }
        return new { customers, tickets, comments };
    }

    private static async Task<int> WriteEntryAsync(ZipArchive zip, string name, Func<CsvWriter, Task> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(entry.Open(), CsvWriter.Encoding);
        var csv = new CsvWriter(writer);
        await write(csv);
        return csv.RowCount - 1; // başlık hariç
    }

    /// <summary>Saklama süresi dolan artefaktların dosyasını siler ve işi "Expired" yapar; yarım kalmış geçici dosyaları temizler.</summary>
    public async Task<int> SweepExpiredAsync(CancellationToken ct)
    {
        var expired = await db.RunAsync(DbScope.None, (session, c) => session.Db.QueryAsync(
            """
            UPDATE app.export_jobs SET status = 'Expired', download_token_hash = NULL, download_token_expires_at = NULL
            WHERE status = 'Ready' AND expires_at <= $1
            RETURNING id, tenant_id
            """,
            r => (Id: r.GetGuid(0), TenantId: r.GetGuid(1)), c, session.Now), ct);
        foreach (var (id, tenantId) in expired)
        {
            var path = _options.ArtifactPath(tenantId, id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        return expired.Count;
    }
}
