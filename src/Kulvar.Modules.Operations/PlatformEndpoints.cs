using Kulvar.Modules.Billing;
using Kulvar.Modules.Billing.Domain;
using Kulvar.Modules.Billing.Provider;
using Kulvar.Modules.Identity.Domain;
using Kulvar.Modules.Tenancy.Domain;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Messaging;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kulvar.Modules.Operations;

public sealed record ReasonRequest(string? Reason);
public sealed record ResolveOperationRequest(string? Action, string? Note);
public sealed record SimulateProviderEventRequest(Guid? TenantId, string? Kind);

/// <summary>
/// Platform operatörü yürütücüsü. Operatör kaydı her istekte doğrulanır (uygulama rolüyle, yalnız kendi satırı); iş
/// <c>kulvar_platform</c> rolüyle çalışır. Bu rolün müşteri/talep/yorum/üyelik/davet/kullanıcı tablolarında hiçbir yetkisi yoktur:
/// "operatör yalnız metadata görür" kuralı kod incelemesine değil veritabanı yetkilerine dayanır. Impersonation yoktur.
/// </summary>
public sealed class PlatformExecutor(AppDb appDb, PlatformDb platformDb, ICurrentUser current)
{
    public async Task<T> RunAsync<T>(Func<DbSession, Guid, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var userId = current.UserId;
        var isOperator = await appDb.RunAsync(DbScope.ForUser(userId), (session, c) =>
            session.Db.Set<PlatformOperator>().AnyAsync(o => o.UserId == userId, c), ct);
        if (!isOperator)
        {
            throw Problems.NotFound(); // Operatör olmayan için bu uçlar "yok".
        }
        return await platformDb.RunAsync(DbScope.None, (session, c) => work(session, userId, c), ct);
    }
}

internal static class PlatformEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var platform = endpoints.MapGroup("/api/platform");
        platform.MapGet("/tenants", Tenants);
        platform.MapPost("/tenants/{id:guid}/suspend", Suspend);
        platform.MapPost("/tenants/{id:guid}/reactivate", Reactivate);
        platform.MapGet("/billing/operations", Operations);
        platform.MapPost("/billing/operations/{id:guid}/resolve", Resolve);
        platform.MapGet("/jobs/dead", DeadJobs);
        platform.MapPost("/jobs/{id:guid}/replay", ReplayJob);
        platform.MapGet("/audit", Audit);
        platform.MapPost("/simulation/provider-event", SimulateProviderEvent);
    }

    /// <summary>Şirket METADATA'sı: ad, durum, plan, abonelik durumu, tarihler. Üye, müşteri, talep veya kullanım içeriği yoktur.</summary>
    private static async Task<IResult> Tenants(HttpContext http, PlatformExecutor executor, CancellationToken ct)
    {
        QueryGuard.AllowOnly(http, "status", "limit");
        var limit = QueryGuard.Limit(http);
        var status = QueryGuard.Enum<TenantStatus>(http, "status");
        var items = await executor.RunAsync((session, _, c) => session.Db.QueryAsync(
            """
            SELECT t.id, t.name, t.status, t.created_at, t.suspended_reason, t.deletion_requested_at, t.purge_after,
                   s.status, p.code, p.name, s.trial_ends_at, s.grace_ends_at
            FROM app.tenants t
            LEFT JOIN app.subscriptions s ON s.tenant_id = t.id
            LEFT JOIN app.plan_versions v ON v.id = s.plan_version_id
            LEFT JOIN app.plans p ON p.code = v.plan_code
            WHERE ($1::text IS NULL OR t.status = $1)
            ORDER BY t.created_at DESC, t.id DESC
            LIMIT $2
            """,
            r => new
            {
                id = r.GetGuid(0),
                name = r.GetString(1),
                status = r.GetString(2),
                createdAt = r.GetFieldValue<DateTimeOffset>(3),
                suspendedReason = r.IsDBNull(4) ? null : r.GetString(4),
                deletionRequestedAt = r.IsDBNull(5) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(5),
                purgeAfter = r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6),
                subscriptionStatus = r.IsDBNull(7) ? null : r.GetString(7),
                planCode = r.IsDBNull(8) ? null : r.GetString(8),
                planName = r.IsDBNull(9) ? null : r.GetString(9),
                trialEndsAt = r.IsDBNull(10) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(10),
                graceEndsAt = r.IsDBNull(11) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(11),
            }, c, status?.ToString(), limit), ct);
        return Results.Ok(new { items });
    }

    private static Task<IResult> Suspend(Guid id, ReasonRequest request, PlatformExecutor executor, CancellationToken ct) =>
        ChangeTenantAsync(id, request, executor, suspend: true, ct);

    private static Task<IResult> Reactivate(Guid id, ReasonRequest request, PlatformExecutor executor, CancellationToken ct) =>
        ChangeTenantAsync(id, request, executor, suspend: false, ct);

    private static async Task<IResult> ChangeTenantAsync(Guid id, ReasonRequest request, PlatformExecutor executor, bool suspend, CancellationToken ct)
    {
        var validator = new Validator();
        var reason = validator.Required("reason", request.Reason, 300, minLength: 5);
        validator.ThrowIfInvalid();
        var status = await executor.RunAsync(async (session, operatorId, c) =>
        {
            if (await TenantRegistry.LockForLifecycleAsync(session.Db, id, c) is null)
            {
                throw Problems.NotFound();
            }
            var tenant = await session.Db.Set<Tenant>().SingleAsync(t => t.Id == id, c);
            var allowed = suspend ? tenant.Status == TenantStatus.Active : tenant.Status == TenantStatus.Suspended;
            if (!allowed)
            {
                throw Problems.Conflict("invalid_tenant_state", suspend ? "Yalnız aktif şirket askıya alınabilir." : "Şirket askıda değil.",
                    new Dictionary<string, object?> { ["tenantStatus"] = tenant.Status.ToString() });
            }
            if (suspend)
            {
                tenant.Suspend(reason);
            }
            else
            {
                tenant.Reactivate();
            }
            session.Db.Add(PlatformAuditEvent.Create(operatorId, suspend ? "tenant.suspended" : "tenant.reactivated", id, new { reason }, session.Now));
            await session.Db.SaveChangesAsync(c);
            return tenant.Status;
        }, ct);
        return Results.Ok(new { status });
    }

    private static async Task<IResult> Operations(HttpContext http, PlatformExecutor executor, CancellationToken ct)
    {
        QueryGuard.AllowOnly(http, "status", "limit");
        var limit = QueryGuard.Limit(http);
        var status = QueryGuard.Enum<BillingOperationStatus>(http, "status") ?? BillingOperationStatus.NeedsResolution;
        var items = await executor.RunAsync(async (session, _, c) =>
            // Platform yolu: şirket kapsamı yok, EF filtresi bilinçli kapalı. Okunan tablo yalnız ödeme işlemi metadata'sıdır;
            // bu veritabanı rolü içerik tablolarına zaten erişemez.
            (await session.Db.Set<BillingOperation>().IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.Status == status).OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).Take(limit).ToListAsync(c))
            .Select(ToDto).ToList(), ct);
        return Results.Ok(new { items });
    }

    /// <summary>
    /// Çözüm bekleyen ödeme sonucunun operatör tarafından terminal yola sokulması. Gerekçe zorunludur ve platform denetim kaydına yazılır.
    /// refund → iade kuyruğu (worker iadeyi yapar → Refunded); void → Voided; close → ManuallyClosed.
    /// </summary>
    private static async Task<IResult> Resolve(Guid id, ResolveOperationRequest request, PlatformExecutor executor, CancellationToken ct)
    {
        var validator = new Validator();
        var note = validator.Required("note", request.Note, 500, minLength: 10);
        BillingOperationStatus? target = request.Action switch
        {
            "refund" => BillingOperationStatus.RefundPending,
            "void" => BillingOperationStatus.Voided,
            "close" => BillingOperationStatus.ManuallyClosed,
            _ => null,
        };
        if (target is null)
        {
            validator.Add("action", "İzin verilen değerler: refund, void, close.");
        }
        validator.ThrowIfInvalid();

        var dto = await executor.RunAsync(async (session, operatorId, c) =>
        {
            await session.Db.ExecuteAsync("SELECT 1 FROM app.billing_operations WHERE id = $1 FOR UPDATE", c, id);
            var operation = await session.Db.Set<BillingOperation>().IgnoreQueryFilters().SingleOrDefaultAsync(o => o.Id == id, c) ?? throw Problems.NotFound();
            if (operation.Status != BillingOperationStatus.NeedsResolution)
            {
                throw Problems.Conflict("operation_not_awaiting_resolution", "İşlem çözüm beklemiyor.", new Dictionary<string, object?> { ["operationStatus"] = operation.Status.ToString() });
            }
            operation.Resolve(target!.Value, operatorId, note, session.Now);
            session.Db.Add(PlatformAuditEvent.Create(operatorId, "billing.operation_resolved", operation.TenantId, new { operationId = id, action = request.Action, note }, session.Now));
            await session.Db.SaveChangesAsync(c);
            return ToDto(operation);
        }, ct);
        return Results.Ok(dto);
    }

    private static object ToDto(BillingOperation o) => new
    {
        operationId = o.Id,
        tenantId = o.TenantId,
        status = o.Status,
        amountMinor = o.AmountMinor,
        currency = o.Currency,
        failureCode = o.FailureCode,
        attempts = o.Attempts,
        createdAt = o.CreatedAt,
        updatedAt = o.UpdatedAt,
        resolutionNote = o.ResolutionNote,
    };

    /// <summary>Ölü mesajlar: tür, deneme, hata kodu. Gövde gösterilmez.</summary>
    private static async Task<IResult> DeadJobs(PlatformExecutor executor, CancellationToken ct)
    {
        var items = await executor.RunAsync(async (session, _, c) =>
            await session.Db.Set<OutboxMessage>().AsNoTracking().Where(m => m.Status == OutboxStatus.Dead)
                .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Take(100)
                .Select(m => new { id = m.Id, tenantId = m.TenantId, type = m.Type, attempts = m.Attempts, lastError = m.LastError, createdAt = m.CreatedAt })
                .ToListAsync(c), ct);
        return Results.Ok(new { items });
    }

    private static async Task<IResult> ReplayJob(Guid id, PlatformExecutor executor, CancellationToken ct)
    {
        await executor.RunAsync(async (session, operatorId, c) =>
        {
            var updated = await session.Db.ExecuteAsync(
                "UPDATE app.outbox_messages SET status = 'Pending', attempts = 0, available_at = $2, lease_owner = NULL, lease_until = NULL, last_error = NULL WHERE id = $1 AND status = 'Dead'",
                c, id, session.Now);
            if (updated != 1)
            {
                throw Problems.NotFound();
            }
            session.Db.Add(PlatformAuditEvent.Create(operatorId, "job.replayed", null, new { messageId = id }, session.Now));
            await session.Db.SaveChangesAsync(c);
            return true;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Audit(HttpContext http, PlatformExecutor executor, CancellationToken ct)
    {
        QueryGuard.AllowOnly(http, "limit");
        var limit = QueryGuard.Limit(http);
        var items = await executor.RunAsync(async (session, _, c) =>
            (await session.Db.Set<PlatformAuditEvent>().AsNoTracking().OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(limit).ToListAsync(c))
            .Select(e => new { id = e.Id, action = e.Action, tenantId = e.TenantId, actorUserId = e.ActorUserId, data = System.Text.Json.Nodes.JsonNode.Parse(e.Data), occurredAt = e.OccurredAt })
            .ToList(), ct);
        return Results.Ok(new { items });
    }

    /// <summary>Yalnız simülasyon açıkken: simüle sağlayıcıdan bir abonelik olayı ister. Olay, gerçek webhook ucundan (imzalı) geri gelir.</summary>
    private static async Task<IResult> SimulateProviderEvent(SimulateProviderEventRequest request, PlatformExecutor executor, IOptions<BillingOptions> options, CancellationToken ct)
    {
        if (!options.Value.Simulation.Enabled)
        {
            throw Problems.NotFound();
        }
        if (request.TenantId is not { } tenantId || request.Kind is not ("renewal_failed" or "renewed"))
        {
            throw Problems.Validation("kind", "tenantId ve kind (renewal_failed | renewed) zorunludur.");
        }
        await executor.RunAsync(async (session, operatorId, c) =>
        {
            await session.Db.ExecuteAsync(ProviderSimulator.NewRequestSql, c, ProviderSimulator.NewRequestId(), tenantId.ToString("N"), request.Kind, session.Now);
            session.Db.Add(PlatformAuditEvent.Create(operatorId, "simulation.provider_event_requested", tenantId, new { kind = request.Kind }, session.Now));
            await session.Db.SaveChangesAsync(c);
            return true;
        }, ct);
        return Results.Accepted();
    }
}
