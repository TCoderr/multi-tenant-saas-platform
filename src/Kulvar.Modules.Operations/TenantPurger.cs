using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kulvar.Modules.Operations;

/// <summary>
/// Şirket temizleme. Silme bekleme süresi dolan şirket önce "Purging" yapılır (bu andan sonra hiçbir iş o şirket için etki üretmez),
/// sonra satırları belgeli sırayla, şirket kapsamında silinir ve şirket mezar taşına ("Deleted") dönüşür.
/// Her adım kendi transaction'ıdır ve yinelenebilir: yarıda kesilen temizleme sonraki turda kaldığı yerden tamamlanır.
/// Global kimlik (users/sessions) ve başka şirketlerin satırları kapsam dışındadır; worker'ın BYPASSRLS'i olmadığı için zaten görünmezler.
/// </summary>
public sealed class TenantPurger(WorkerDb db, IOptions<ExportOptions> exportOptions, ILogger<TenantPurger> log)
{
    /// <summary>Silme sırası: çocuk tablolar önce (FK'ler RESTRICT). Mezar taşında kalanlar: tenants, subscriptions, billing_operations.</summary>
    public static readonly IReadOnlyList<string> PurgeOrder =
    [
        "ticket_comments", "audit_events", "tickets", "customers", "usage_events", "usage_counters", "tenant_counters", "idempotency_keys",
        "export_jobs", "ownership_offers", "invites", "sink_messages", "outbox_messages", "memberships",
    ];

    /// <summary>Test kancası: bir tablo silindikten sonra çağrılır (yarıda kesilme senaryosu). Üretimde boştur.</summary>
    internal Func<string, Task>? AfterStepHook { get; set; }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var due = await db.RunAsync(DbScope.None, (session, c) => session.Db.QueryAsync(
            "SELECT id FROM app.tenants WHERE status = 'Purging' OR (status = 'DeletionPending' AND purge_after <= $1) ORDER BY purge_after NULLS FIRST, id LIMIT 5",
            r => r.GetGuid(0), c, session.Now), ct);

        var purged = 0;
        foreach (var tenantId in due)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            if (await PurgeAsync(tenantId, ct))
            {
                purged++;
            }
        }
        return purged;
    }

    public async Task<bool> PurgeAsync(Guid tenantId, CancellationToken ct)
    {
        // 1) Yazma kapısı: FOR UPDATE, şirkete satır ekleyen devam eden tüm transaction'ların (FOR KEY SHARE / FOR SHARE) bitmesini bekler.
        var started = await db.RunAsync(DbScope.None, async (session, c) =>
        {
            var status = await TenantRegistry.LockForLifecycleAsync(session.Db, tenantId, c);
            if (status == TenantStatus.Purging)
            {
                return true; // Önceki tur yarıda kalmış: devam.
            }
            // Bekleme süresi içinde iptal edilmiş olabilir; koşul kilit altında yeniden sınanır.
            var updated = await session.Db.ExecuteAsync(
                "UPDATE app.tenants SET status = 'Purging', version = version + 1 WHERE id = $1 AND status = 'DeletionPending' AND purge_after <= $2",
                c, tenantId, session.Now);
            if (updated == 1)
            {
                session.Db.Add(PlatformAuditEvent.Create(null, "tenant.purge_started", tenantId, null, session.Now));
                await session.Db.SaveChangesAsync(c);
            }
            return updated == 1;
        }, ct);
        if (!started)
        {
            return false;
        }

        // 2) Satırlar: her tablo ayrı transaction, şirket kapsamında. RLS gereği yalnız bu şirketin satırları silinebilir.
        var counts = new Dictionary<string, int>();
        foreach (var table in PurgeOrder)
        {
            counts[table] = await db.RunAsync(DbScope.ForTenant(tenantId), (session, c) =>
                // Tablo adı sabit listeden gelir (kullanıcı girdisi değil); şirket kimliği parametredir.
                session.Db.ExecuteAsync($"DELETE FROM app.{table} WHERE tenant_id = $1", c, tenantId), ct);
            if (AfterStepHook is { } hook)
            {
                await hook(table);
            }
        }

        // 3) Export dosyaları.
        var directory = exportOptions.Value.Root.Length == 0 ? null : exportOptions.Value.TenantDirectory(tenantId);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        // 4) Mezar taşı: ad temizlenir; abonelik kapatılır (epoch ilerler: geç gelen ödeme başarısı uygulanamaz, iade edilir).
        await db.RunAsync(DbScope.None, async (session, c) =>
        {
            await TenantRegistry.LockForLifecycleAsync(session.Db, tenantId, c);
            await session.Db.ExecuteAsync(
                "UPDATE app.tenants SET status = 'Deleted', name = '(silindi)', suspended_reason = NULL, deleted_at = $2, version = version + 1 WHERE id = $1 AND status = 'Purging'",
                c, tenantId, session.Now);
            await session.Db.ExecuteAsync(
                "UPDATE app.subscriptions SET status = 'Cancelled', epoch = epoch + 1, updated_at = $2, version = version + 1 WHERE tenant_id = $1",
                c, tenantId, session.Now);
            session.Db.Add(PlatformAuditEvent.Create(null, "tenant.purged", tenantId, new { rows = counts.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value) }, session.Now));
            await session.Db.SaveChangesAsync(c);
        }, ct);
        log.LogInformation("Şirket temizlendi: {TenantId} satır={Rows}", tenantId, counts.Values.Sum());
        return true;
    }
}
