using System.Diagnostics;
using System.Diagnostics.Metrics;
using Kulvar.Platform.Data;
using Kulvar.Platform.Observability;
using Kulvar.Platform.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kulvar.Platform.Messaging;

/// <summary>Bir mesaj türünün etkisini, worker'ın transaction'ı içinde uygular. Etki idempotent olmalıdır.</summary>
public interface IOutboxHandler
{
    string Type { get; }

    /// <summary>
    /// Kapsam (şirket veya kullanıcı) çağrıdan önce kurulmuştur. Konu bulunamazsa (silinmiş/iptal edilmiş) sessizce döner;
    /// mesaj gövdesindeki kimlikler yetki üretmez, yalnız kurulu kapsamda arama yapılır.
    /// </summary>
    Task HandleAsync(DbSession session, OutboxMessageEnvelope message, CancellationToken ct);
}

public sealed record OutboxMessageEnvelope(Guid Id, Guid? TenantId, Guid? UserId, string Type, string Payload, long LeaseToken, int Attempts)
{
    public T Read<T>() => System.Text.Json.JsonSerializer.Deserialize<T>(Payload, Audit.JsonDefaults.Storage)
        ?? throw new InvalidOperationException($"Outbox gövdesi okunamadı: {Type}");
}

public sealed class OutboxOptions
{
    public int BatchSize { get; set; } = 20;
    public int PerTenantBatch { get; set; } = 5;
    public int MaxAttempts { get; set; } = 8;
    public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromMinutes(10);
}

public enum OutboxOutcome { Done, SkippedTenantGone, Retried, Dead, LeaseLost }

/// <summary>Kiralamasını kaybeden worker'ın etkisi geri alınır (fencing).</summary>
public sealed class LeaseLostException() : Exception("Mesaj kiralaması başka bir worker'a geçti.");

/// <summary>
/// Kiralamalı outbox işleyici. Etki ve "tamamlandı" işareti aynı transaction'dadır; tamamlama lease_token eşleşmesi ister.
/// </summary>
public sealed class OutboxProcessor(WorkerDb db, IEnumerable<IOutboxHandler> handlers, IOptions<OutboxOptions> options, ILogger<OutboxProcessor> log)
{
    private readonly Dictionary<string, IOutboxHandler> _handlers = handlers.ToDictionary(h => h.Type, StringComparer.Ordinal);
    private readonly OutboxOptions _options = options.Value;

    /// <summary>Test kancası: etki uygulandıktan sonra, tamamlamadan hemen önce çağrılır. Üretimde boştur.</summary>
    internal Func<OutboxMessageEnvelope, Task>? BeforeCompleteHook { get; set; }

    public string WorkerId { get; init; } = $"worker-{Environment.ProcessId}-{Guid.NewGuid():N}"[..40];

    /// <summary>
    /// Bir parti kiralar ve işler. Kapatma isteği mesajlar ARASINDA gözlenir: başlanmış mesaj tamamlanır (süresi veritabanı ve sağlayıcı
    /// zaman aşımlarıyla sınırlıdır), başlanmamış olanlar kiralama süresi dolunca yeniden alınır. Yarım etki bırakılmaz.
    /// </summary>
    public async Task<IReadOnlyList<OutboxOutcome>> RunOnceAsync(CancellationToken ct)
    {
        var claimed = await ClaimAsync(ct);
        var outcomes = new List<OutboxOutcome>(claimed.Count);
        foreach (var message in claimed)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            outcomes.Add(await ProcessAsync(message, CancellationToken.None));
        }
        return outcomes;
    }

    public Task<List<OutboxMessageEnvelope>> ClaimAsync(CancellationToken ct) =>
        db.RunAsync(DbScope.None, (session, c) => session.Db.QueryAsync(
            """
            WITH candidates AS (
                SELECT id, created_at,
                       row_number() OVER (PARTITION BY coalesce(tenant_id, user_id, id) ORDER BY created_at, id) AS rn
                FROM app.outbox_messages
                WHERE status = 'Pending' AND available_at <= $1 AND (lease_until IS NULL OR lease_until <= $1)
            ), picked AS (
                SELECT o.id
                FROM app.outbox_messages o
                JOIN candidates c ON c.id = o.id
                WHERE c.rn <= $2 AND o.status = 'Pending' AND o.available_at <= $1 AND (o.lease_until IS NULL OR o.lease_until <= $1)
                ORDER BY c.rn, c.created_at, o.id
                LIMIT $3
                FOR UPDATE OF o SKIP LOCKED
            )
            UPDATE app.outbox_messages m
            SET lease_owner = $4, lease_until = $5, lease_token = m.lease_token + 1, attempts = m.attempts + 1
            FROM picked
            WHERE m.id = picked.id
            RETURNING m.id, m.tenant_id, m.user_id, m.type, m.payload::text, m.lease_token, m.attempts
            """,
            r => new OutboxMessageEnvelope(r.GetGuid(0), r.GetGuidOrNull(1), r.GetGuidOrNull(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt32(6)),
            c, session.Now, _options.PerTenantBatch, _options.BatchSize, WorkerId, session.Now + _options.Lease), ct);

    public async Task<OutboxOutcome> ProcessAsync(OutboxMessageEnvelope message, CancellationToken ct)
    {
        using var activity = Telemetry.Activities.StartActivity("outbox.process");
        activity?.SetTag("kulvar.message_type", message.Type);
        var outcome = OutboxOutcome.Done;
        try
        {
            outcome = await db.RunAsync(DbScope.None, async (session, c) =>
            {
                var result = OutboxOutcome.Done;
                if (message.TenantId is { } tenantId)
                {
                    // FOR SHARE: iş bitene kadar şirket Purging'e geçemez; temizleme ya bu etkiyi de siler ya da iş hiç başlamaz.
                    var status = await TenantRegistry.LockSharedAsync(session.Db, tenantId, c);
                    if (status is null or TenantStatus.Purging or TenantStatus.Deleted)
                    {
                        result = OutboxOutcome.SkippedTenantGone; // Silinmiş şirket için hiçbir etki üretilmez.
                    }
                    else
                    {
                        await session.EnterTenantAsync(tenantId, c);
                    }
                }
                if (message.UserId is { } userId)
                {
                    await session.EnterUserAsync(userId, c);
                }

                if (result == OutboxOutcome.Done)
                {
                    if (!_handlers.TryGetValue(message.Type, out var handler))
                    {
                        throw new InvalidOperationException($"Bilinmeyen outbox türü: {message.Type}");
                    }
                    await handler.HandleAsync(session, message, c);
                    await session.Db.SaveChangesAsync(c);
                }

                if (BeforeCompleteHook is { } hook)
                {
                    await hook(message);
                }

                var completed = await session.Db.ExecuteAsync(
                    """
                    UPDATE app.outbox_messages
                    SET status = 'Done', processed_at = $3, lease_owner = NULL, lease_until = NULL, last_error = $4
                    WHERE id = $1 AND lease_token = $2 AND status = 'Pending'
                    """,
                    c, message.Id, message.LeaseToken, session.Now, result == OutboxOutcome.SkippedTenantGone ? "tenant_gone" : null);
                if (completed != 1)
                {
                    throw new LeaseLostException();
                }
                return result;
            }, ct);
        }
        catch (LeaseLostException)
        {
            outcome = OutboxOutcome.LeaseLost;
            log.LogWarning("Outbox kiralaması kaybedildi: {MessageId} {MessageType}", message.Id, message.Type);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            outcome = await FailAsync(message, ex, ct);
        }

        Telemetry.OutboxProcessed.Add(1, new KeyValuePair<string, object?>("type", message.Type), new KeyValuePair<string, object?>("outcome", outcome.ToString()));
        activity?.SetTag("kulvar.outcome", outcome.ToString());
        return outcome;
    }

    private async Task<OutboxOutcome> FailAsync(OutboxMessageEnvelope message, Exception ex, CancellationToken ct)
    {
        var dead = message.Attempts >= _options.MaxAttempts;
        var errorCode = PostgresErrors.SqlState(ex) is { } sqlState ? $"db:{sqlState}" : ex.GetType().Name;
        // İstisna mesajı kişisel veri içerebilir; yalnız tür/sqlstate saklanır ve loglanır.
        log.LogWarning("Outbox mesajı işlenemedi: {MessageId} {MessageType} deneme={Attempts} hata={ErrorCode} dead={Dead}",
            message.Id, message.Type, message.Attempts, errorCode, dead);

        var updated = await db.RunAsync(DbScope.None, (session, c) => session.Db.ExecuteAsync(
            """
            UPDATE app.outbox_messages
            SET status = $3, available_at = $4, last_error = $5, lease_owner = NULL, lease_until = NULL
            WHERE id = $1 AND lease_token = $2 AND status = 'Pending'
            """,
            c, message.Id, message.LeaseToken, dead ? "Dead" : "Pending", session.Now + Backoff(message.Attempts), errorCode), ct);
        if (updated != 1)
        {
            return OutboxOutcome.LeaseLost;
        }
        return dead ? OutboxOutcome.Dead : OutboxOutcome.Retried;
    }

    /// <summary>Üstel bekleme + %20 jitter, tavanlı.</summary>
    internal TimeSpan Backoff(int attempts)
    {
        var exponent = Math.Min(Math.Max(attempts - 1, 0), 16);
        var seconds = Math.Min(_options.BackoffBase.TotalSeconds * Math.Pow(2, exponent), _options.BackoffCap.TotalSeconds);
        var jitter = 1 + (Random.Shared.NextDouble() * 0.4 - 0.2);
        return TimeSpan.FromSeconds(seconds * jitter);
    }
}
