using Kulvar.Modules.Billing.Domain;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Messaging;
using Kulvar.Platform.Observability;
using Kulvar.Platform.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kulvar.Modules.Billing;

/// <summary>
/// Entitlement kaynağı sunucudaki abonelik + plan sürümüdür ve her istekte veritabanından okunur. Önbellek yoktur;
/// dolayısıyla geçersiz kılma, TTL veya eski değerle karar verme sorunu da yoktur.
/// </summary>
internal sealed class EntitlementReader : IEntitlementReader
{
    public async Task<Entitlements> ReadAsync(DbSession session, Guid tenantId, CancellationToken ct)
    {
        var row = await (from s in session.Db.Set<Subscription>()
                         join v in session.Db.Set<PlanVersion>() on s.PlanVersionId equals v.Id
                         join p in session.Db.Set<Plan>() on v.PlanCode equals p.Code
                         where s.TenantId == tenantId
                         select new { s.Status, s.TrialEndsAt, s.GraceEndsAt, p.Code, p.Name, v.SeatLimit, v.MonthlyTicketLimit, v.ExportEnabled })
            .AsNoTracking().SingleOrDefaultAsync(ct);

        // Abonelik satırı yoksa (olmamalı) kapalı tarafa düşülür: salt-okunur, limit yok.
        return row is null
            ? new Entitlements(SubscriptionStatus.Suspended, "none", "Plan yok", 0, 0, false, null, null)
            : new Entitlements(row.Status, row.Code, row.Name, row.SeatLimit, row.MonthlyTicketLimit, row.ExportEnabled, row.TrialEndsAt, row.GraceEndsAt);
    }

    public async Task<UsageSnapshot> ReadUsageAsync(DbSession session, Guid tenantId, CancellationToken ct)
    {
        var period = UsagePeriod.Of(session.Now);
        var used = await session.Db.Set<UsageCounter>()
            .Where(c => c.Metric == UsageMetrics.TicketsCreated && c.PeriodStart == period)
            .Select(c => (int?)c.Used).SingleOrDefaultAsync(ct);
        return new UsageSnapshot(used ?? 0, UsagePeriod.ResetsAt(session.Now));
    }
}

/// <summary>
/// Aylık kota kapısı. Tüketim tek deyimdir (önce-oku-sonra-yaz yok) ve onu tetikleyen iş etkisiyle aynı transaction'dadır.
/// Limit, abonelik satırı FOR SHARE kilitlenerek okunur: eşzamanlı plan değişikliği (FOR UPDATE) bu transaction'ı bekler;
/// bir oluşturma ya değişiklikten önce commit olur ya da sonra başlar.
/// </summary>
internal sealed class UsageGate : IUsageGate
{
    public async Task ConsumeAsync(TenantContext context, string metric, Guid sourceId, CancellationToken ct)
    {
        // İki ayrı deyim, bilinçli: kilit beklerken abonelik güncellenirse PostgreSQL (READ COMMITTED) kilitli satırın YENİ sürümünü döndürür,
        // ama aynı deyimde join edilmiş plan satırını yeniden aramaz; join'li tek deyim bu durumda satır döndürmez. Önce abonelik
        // kilitlenip güncel plan sürümü okunur, limit ondan sonra alınır.
        var planVersionId = await context.Db.ScalarAsync("SELECT plan_version_id FROM app.subscriptions WHERE tenant_id = $1 FOR SHARE", ct, context.TenantId);
        var limit = planVersionId is Guid id
            ? (int?)await context.Db.ScalarAsync("SELECT monthly_ticket_limit FROM app.plan_versions WHERE id = $1", ct, id) ?? 0
            : 0;
        var period = UsagePeriod.Of(context.Now);

        object? used = null;
        if (limit > 0)
        {
            used = await context.Db.ScalarAsync(
                """
                INSERT INTO app.usage_counters AS c (tenant_id, metric, period_start, used)
                VALUES ($1, $2, $3, 1)
                ON CONFLICT (tenant_id, metric, period_start)
                DO UPDATE SET used = c.used + 1 WHERE c.used < $4
                RETURNING used
                """,
                ct, context.TenantId, metric, period, limit);
        }
        if (used is null)
        {
            Telemetry.QuotaRejections.Add(1, new KeyValuePair<string, object?>("metric", metric));
            throw QuotaExceeded(metric, limit, limit, UsagePeriod.ResetsAt(context.Now));
        }

        await context.Db.ExecuteAsync(
            "INSERT INTO app.usage_events (tenant_id, metric, source_id, period_start, occurred_at) VALUES ($1, $2, $3, $4, $5)",
            ct, context.TenantId, metric, sourceId, period, context.Now);
    }

    /// <summary>Abonelik kotası yanıtı: 403. Taşıma hız sınırı (429) değildir ve Retry-After taşımaz; sıfırlanma zamanı gövdededir.</summary>
    public static ProblemException QuotaExceeded(string metric, int limit, int used, DateTimeOffset resetsAt) =>
        new(403, "quota_exceeded", "Planınızın bu dönemdeki kotası doldu.", new Dictionary<string, object?>
        {
            ["metric"] = metric,
            ["limit"] = limit,
            ["used"] = used,
            ["resetsAt"] = resetsAt,
        });
}

/// <summary>Şirket oluşturulurken (aynı transaction) 14 günlük deneme aboneliği açılır.</summary>
internal sealed class TrialProvisioning : ITenantProvisioningStep
{
    public async Task OnTenantCreatedAsync(DbSession session, Guid tenantId, CancellationToken ct)
    {
        session.Db.Add(Subscription.StartTrial(tenantId, session.Now));
        await session.Db.SaveChangesAsync(ct);
    }
}

/// <summary>Şirket silme isteğinde (aynı transaction) aboneliğin epoch'u ilerler: yoldaki bir ödemenin geç başarısı artık uygulanamaz, iade edilir.</summary>
internal sealed class InvalidateOperationsOnDeletion : ITenantDeletionStep
{
    public async Task OnDeletionRequestedAsync(TenantContext context, CancellationToken ct)
    {
        await context.Db.ExecuteAsync("SELECT 1 FROM app.subscriptions WHERE tenant_id = $1 FOR UPDATE", ct, context.TenantId);
        var subscription = await context.Db.Set<Subscription>().SingleAsync(ct);
        subscription.InvalidatePendingOperations(context.Now);
    }
}

public static class BillingOutbox
{
    public const string SubscriptionChanged = "billing.subscription_changed";
    public const string OperationRequested = "billing.operation_requested";

    public sealed record SubscriptionChangedPayload(string Reason);
    public sealed record OperationPayload(Guid OperationId);
}

/// <summary>
/// Zamana bağlı abonelik geçişleri (trial bitişi, ödemesiz dönem bitişi). Worker metadata'yı şirketler arası görür;
/// her şirketin geçişi kendi transaction'ında ve o şirketin kapsamında uygulanır. Yinelenebilir: geçiş koşulu satır kilidi altında yeniden sınanır.
/// </summary>
public sealed class SubscriptionTimers(WorkerDb db, ILogger<SubscriptionTimers> log)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var due = await db.RunAsync(DbScope.None, (session, c) => session.Db.QueryAsync(
            """
            SELECT s.tenant_id
            FROM app.subscriptions s JOIN app.tenants t ON t.id = s.tenant_id
            WHERE ((s.status = 'Trialing' AND s.trial_ends_at <= $1) OR (s.status = 'PastDue' AND s.grace_ends_at <= $1))
              AND t.status NOT IN ('Purging', 'Deleted')   -- silinen şirketler partiyi doldurup diğerlerini aç bırakmasın
            ORDER BY least(coalesce(s.trial_ends_at, s.grace_ends_at), coalesce(s.grace_ends_at, s.trial_ends_at)), s.tenant_id
            LIMIT 100
            """,
            r => r.GetGuid(0), c, session.Now), ct);

        var changed = 0;
        foreach (var tenantId in due)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            changed += await db.RunAsync(DbScope.None, async (session, c) =>
            {
                var tenantStatus = await TenantRegistry.LockSharedAsync(session.Db, tenantId, c);
                if (tenantStatus is null or TenantStatus.Purging or TenantStatus.Deleted)
                {
                    return 0;
                }
                await session.EnterTenantAsync(tenantId, c);
                await session.Db.ExecuteAsync("SELECT 1 FROM app.subscriptions WHERE tenant_id = $1 FOR UPDATE", c, tenantId);
                var subscription = await session.Db.Set<Subscription>().SingleAsync(c);
                var change = subscription.AdvanceTime(session.Now);
                if (change == SubscriptionChange.None)
                {
                    return 0; // Başka bir worker veya bir ödeme bu arada durumu değiştirmiş.
                }
                session.Db.Add(AuditEvent.Create(tenantId, null, "subscription.status_changed", "subscription", tenantId, null, new { reason = change, to = subscription.Status }, session.Now));
                session.Db.Add(OutboxMessage.Create(tenantId, null, BillingOutbox.SubscriptionChanged, new BillingOutbox.SubscriptionChangedPayload(change.ToString()), session.Now));
                await session.Db.SaveChangesAsync(c);
                return 1;
            }, ct);
        }
        if (changed > 0)
        {
            log.LogInformation("Abonelik zamanlayıcısı: {Changed} geçiş uygulandı", changed);
        }
        return changed;
    }
}
