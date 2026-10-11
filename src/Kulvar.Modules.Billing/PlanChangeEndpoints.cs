using Kulvar.Modules.Billing.Domain;
using Kulvar.Modules.Billing.Provider;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kulvar.Modules.Billing;

public sealed record PlanChangeRequest(string? PlanCode, string? SimulatedOutcome);

internal static class PlanChangeEndpoints
{
    public const string Operation = "billing.plan_change";
    private const string InFlightIndex = "ix_billing_operations_tenant_id";

    /// <summary>
    /// Plan değişikliği isteği. Yanıt 202'dir: "kabul edildi", "uygulandı" değil. Entitlement ancak sağlayıcı başarısı uygulanınca değişir.
    /// İşlem kaydı ve outbox mesajı sağlayıcıya gidilmeden önce, tek transaction'da yazılır.
    /// </summary>
    public static async Task<IResult> Request(Guid tenantId, PlanChangeRequest request, HttpContext http, ITenantExecutor executor, IOptions<BillingOptions> options, CancellationToken ct)
    {
        var key = Idempotency.RequireKey(http);
        var planCode = (request.PlanCode ?? string.Empty).Trim().ToLowerInvariant();
        if (planCode is not (PlanCatalog.Starter or PlanCatalog.Team))
        {
            throw Problems.Validation("planCode", "Seçilebilir planlar: starter, team.");
        }
        if (request.SimulatedOutcome is not null)
        {
            // Sonuç seçimi yalnız demo/test içindir; açık opt-in olmadan kabul edilmez (üretimde bu anahtar açılamaz).
            if (!options.Value.Simulation.AllowOutcomeSelection)
            {
                throw Problems.Validation("simulatedOutcome", "Simülasyon sonucu seçimi bu ortamda kapalı.");
            }
            if (!SimulatedOutcomes.All.Contains(request.SimulatedOutcome))
            {
                throw Problems.Validation("simulatedOutcome", "Bilinmeyen simülasyon sonucu.");
            }
        }
        var hash = Idempotency.HashRequest(request);

        var result = await executor.RunAsync(tenantId, Permission.BillingManage, TenantLock.ActorShared, (ctx, c) =>
            Idempotency.ExecuteAsync(ctx, Operation, key, hash, async () =>
            {
                await ctx.Db.ExecuteAsync("SELECT 1 FROM app.subscriptions WHERE tenant_id = $1 FOR UPDATE", c, ctx.TenantId);
                var subscription = await ctx.Db.Set<Subscription>().SingleAsync(c);
                var target = await ctx.Db.Set<PlanVersion>().AsNoTracking().SingleAsync(v => v.PlanCode == planCode && v.IsCurrent, c);
                if (subscription.Status == SubscriptionStatus.Active && subscription.PlanVersionId == target.Id)
                {
                    throw Problems.Conflict("already_on_plan", "Şirket zaten bu planda.");
                }
                var inFlight = await ctx.Db.Set<BillingOperation>()
                    .AnyAsync(o => o.Status == BillingOperationStatus.Pending || o.Status == BillingOperationStatus.Unknown, c);
                if (inFlight)
                {
                    throw InProgress();
                }

                // Yeni işlem aboneliğin epoch'unu ilerletir: daha önce istenmiş (çözüm bekleyen dahil) işlemlerin geç başarısı artık uygulanamaz.
                subscription.InvalidatePendingOperations(ctx.Now);
                var operation = BillingOperation.Request(ctx.TenantId, ctx.UserId, target.Id, target.PriceMinor, subscription.Epoch, request.SimulatedOutcome, ctx.Now);
                ctx.Db.Add(operation);
                ctx.Audit("billing.plan_change_requested", "subscription", ctx.TenantId, data: new { operationId = operation.Id, planCode });
                ctx.Enqueue(BillingOutbox.OperationRequested, new BillingOutbox.OperationPayload(operation.Id));
                try
                {
                    await ctx.Db.SaveChangesAsync(c);
                }
                catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, InFlightIndex))
                {
                    throw InProgress();
                }
                return (StatusCodes.Status202Accepted, ToDto(operation, planCode));
            }, c), ct);
        return result.ToHttpResult(http);
    }

    public static async Task<IResult> GetOperation(Guid tenantId, Guid id, ITenantExecutor executor, CancellationToken ct)
    {
        var response = await executor.RunAsync(tenantId, Permission.BillingRead, TenantLock.None, async (ctx, c) =>
        {
            var row = await (from o in ctx.Db.Set<BillingOperation>()
                             join v in ctx.Db.Set<PlanVersion>() on o.TargetPlanVersionId equals v.Id
                             where o.Id == id
                             select new { Operation = o, v.PlanCode }).AsNoTracking().SingleOrDefaultAsync(c) ?? throw Problems.NotFound();
            return ToDto(row.Operation, row.PlanCode);
        }, ct);
        return Results.Ok(response);
    }

    /// <summary>Abonelik iptali. Epoch ilerler: yoldaki bir ödemenin geç başarısı aboneliği yeniden etkinleştiremez, iade edilir.</summary>
    public static async Task<IResult> Cancel(Guid tenantId, ITenantExecutor executor, CancellationToken ct)
    {
        await executor.RunAsync(tenantId, Permission.BillingManage, TenantLock.ActorShared, async (ctx, c) =>
        {
            await ctx.Db.ExecuteAsync("SELECT 1 FROM app.subscriptions WHERE tenant_id = $1 FOR UPDATE", c, ctx.TenantId);
            var subscription = await ctx.Db.Set<Subscription>().SingleAsync(c);
            if (subscription.Status == SubscriptionStatus.Cancelled)
            {
                throw Problems.Conflict("already_cancelled", "Abonelik zaten iptal edilmiş.");
            }
            subscription.Cancel(ctx.Now);
            ctx.Audit("billing.subscription_cancelled", "subscription", ctx.TenantId);
            ctx.Enqueue(BillingOutbox.SubscriptionChanged, new BillingOutbox.SubscriptionChangedPayload("Cancelled"));
            await ctx.Db.SaveChangesAsync(c);
            return true;
        }, ct);
        return Results.NoContent();
    }

    internal static OperationDto ToDto(BillingOperation o, string planCode) =>
        new(o.Id, planCode, o.AmountMinor, o.Currency, o.Status, BillingOperationRules.IsTerminal(o.Status), o.FailureCode, o.CreatedAt, o.CompletedAt);

    public sealed record OperationDto(Guid OperationId, string PlanCode, long AmountMinor, string Currency, BillingOperationStatus Status, bool Terminal, string? FailureCode, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

    private static ProblemException InProgress() =>
        Problems.Conflict("operation_in_progress", "Sonucu bekleyen bir plan değişikliği var. Tamamlanınca yeniden deneyin.");
}
