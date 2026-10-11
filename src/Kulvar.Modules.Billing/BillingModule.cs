using Kulvar.Modules.Billing.Domain;
using Kulvar.Modules.Billing.Provider;
using Kulvar.Platform.Data;
using Kulvar.Platform.Messaging;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kulvar.Modules.Billing;

public static class BillingModule
{
    public const string WebhookRateLimitPolicy = "webhook";

    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BillingOptions>(configuration.GetSection("Billing"));
        services.AddSingleton<IEntitlementReader, EntitlementReader>();
        services.AddSingleton<IUsageGate, UsageGate>();
        services.AddSingleton<ITenantProvisioningStep, TrialProvisioning>();
        services.AddSingleton<ITenantDeletionStep, InvalidateOperationsOnDeletion>();
        services.AddSingleton<IOutboxHandler, SubscriptionChangedMailHandler>();
        services.AddSingleton<IOutboxHandler, OperationRequestedHandler>();
        services.AddSingleton<SubscriptionTimers>();
        // Gerçek ödeme sağlayıcısı yoktur: tek uygulama simüle sağlayıcıdır (durumu fakepsp şemasında, yalnız worker rolü erişir).
        services.AddSingleton<IBillingProvider, FakeBillingProvider>();
        services.AddSingleton<BillingProcessor>();
        services.AddSingleton<BillingReconciler>();
        services.AddSingleton<ProviderInboxProcessor>();
        services.AddSingleton<ProviderSimulator>();
        return services;
    }

    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/plans", BillingEndpoints.Plans).AllowAnonymous();
        endpoints.MapPost("/api/webhooks/billing", WebhookEndpoint.Receive).AllowAnonymous().RequireRateLimiting(WebhookEndpoint.RateLimitPolicy);
        var tenant = endpoints.MapGroup("/api/t/{tenantId:guid}/billing");
        tenant.MapGet("/", BillingEndpoints.Overview);
        tenant.MapPost("/plan-change", PlanChangeEndpoints.Request);
        tenant.MapGet("/operations/{id:guid}", PlanChangeEndpoints.GetOperation);
        tenant.MapPost("/cancel", PlanChangeEndpoints.Cancel);
        return endpoints;
    }
}

internal static class BillingEndpoints
{
    /// <summary>Public fiyat listesi: yalnız güncel plan sürümleri. Şirket verisi içermez.</summary>
    public static async Task<IResult> Plans(AppDb db, CancellationToken ct)
    {
        var items = await db.RunAsync(DbScope.None, async (session, c) => await CurrentPlansAsync(session.Db, c), ct);
        return Results.Ok(new { currency = PlanCatalog.Currency, items });
    }

    public static async Task<IResult> Overview(Guid tenantId, ITenantExecutor executor, IEntitlementReader entitlements, IOptions<BillingOptions> options, CancellationToken ct)
    {
        var response = await executor.RunAsync(tenantId, Permission.BillingRead, TenantLock.None, async (ctx, c) =>
        {
            var subscription = await ctx.Db.Set<Subscription>().AsNoTracking().SingleAsync(c);
            var usage = await entitlements.ReadUsageAsync(ctx.Session, tenantId, c);
            var e = ctx.Entitlements;
            return new
            {
                subscription = new
                {
                    status = subscription.Status,
                    planCode = e.PlanCode,
                    planName = e.PlanName,
                    trialEndsAt = subscription.TrialEndsAt,
                    graceEndsAt = subscription.GraceEndsAt,
                    currentPeriodEnd = subscription.CurrentPeriodEnd,
                },
                entitlements = new { seatLimit = e.SeatLimit, monthlyTicketLimit = e.MonthlyTicketLimit, exportEnabled = e.ExportEnabled },
                usage = new { ticketsThisPeriod = usage.TicketsThisPeriod, periodResetsAt = usage.PeriodResetsAt },
                canManage = ctx.Can(Permission.BillingManage),
                plans = await CurrentPlansAsync(ctx.Db, c),
                // Son işlemler: "kabul edildi" ile "uygulandı" ayrımı burada görünür (istemci açık işlemi yoklayarak izler).
                operations = (await (from o in ctx.Db.Set<BillingOperation>()
                                     join v in ctx.Db.Set<PlanVersion>() on o.TargetPlanVersionId equals v.Id
                                     orderby o.CreatedAt descending, o.Id descending
                                     select new { Operation = o, v.PlanCode }).AsNoTracking().Take(10).ToListAsync(c))
                    .Select(r => PlanChangeEndpoints.ToDto(r.Operation, r.PlanCode)).ToList(),
                simulation = new { outcomeSelection = options.Value.Simulation.AllowOutcomeSelection },
            };
        }, ct);
        return Results.Ok(response);
    }

    internal static async Task<List<PlanDto>> CurrentPlansAsync(AppDbContext db, CancellationToken ct) =>
        await (from v in db.Set<PlanVersion>()
               join p in db.Set<Plan>() on v.PlanCode equals p.Code
               where v.IsCurrent
               orderby p.SortOrder
               select new PlanDto(p.Code, p.Name, v.PriceMinor, v.Currency, v.SeatLimit, v.MonthlyTicketLimit, v.ExportEnabled, v.TrialDays))
            .AsNoTracking().ToListAsync(ct);

    public sealed record PlanDto(string Code, string Name, long PriceMinor, string Currency, int SeatLimit, int MonthlyTicketLimit, bool ExportEnabled, int? TrialDays);
}

/// <summary>Abonelik durumu değişince şirketin Owner'larına bilgi iletisi (demo posta kutusu).</summary>
internal sealed class SubscriptionChangedMailHandler : IOutboxHandler
{
    public string Type => BillingOutbox.SubscriptionChanged;

    public async Task HandleAsync(DbSession session, OutboxMessageEnvelope message, CancellationToken ct)
    {
        var payload = message.Read<BillingOutbox.SubscriptionChangedPayload>();
        // Şirket kapsamı kurulu: üyelik ve kullanıcı satırları yalnız bu şirket için görünür.
        var owners = await session.Db.QueryAsync(
            """
            SELECT u.normalized_email, t.name, s.status
            FROM app.memberships m
            JOIN app.users u ON u.id = m.user_id
            JOIN app.tenants t ON t.id = m.tenant_id
            JOIN app.subscriptions s ON s.tenant_id = m.tenant_id
            WHERE m.tenant_id = $1 AND m.role = 'Owner' AND m.status = 'Active'
            """,
            r => (Email: r.GetString(0), Tenant: r.GetString(1), Status: r.GetString(2)), ct, session.TenantId);

        foreach (var owner in owners)
        {
            var dedupeKey = $"subscription_changed:{message.Id:N}:{owner.Email}";
            if (await MailSink.AlreadyDeliveredAsync(session.Db, dedupeKey, ct))
            {
                continue;
            }
            var (subject, body) = payload.Reason switch
            {
                nameof(SubscriptionChange.TrialExpired) => ("deneme süreniz bitti", "Deneme süresi doldu. Ödemesiz dönem boyunca çalışmaya devam edebilirsiniz; kesinti olmaması için bir plan seçin."),
                nameof(SubscriptionChange.GraceExpired) => ("çalışma alanı salt-okunur oldu", "Ödemesiz dönem bitti. Verileriniz duruyor ve okunabilir; yazmaya devam etmek için bir plan seçin."),
                "PlanApplied" => ("planınız güncellendi", "Plan değişikliği uygulandı. Yeni limitler hemen geçerlidir."),
                "PaymentDeclined" => ("ödeme reddedildi", "Plan değişikliği için ödeme alınamadı. Planınız ve limitleriniz değişmedi."),
                "RenewalFailed" => ("yenileme ödemesi alınamadı", "Yenileme ödemesi alınamadı. Ödemesiz dönem boyunca çalışmaya devam edebilirsiniz."),
                "Renewed" => ("aboneliğiniz yenilendi", "Ödeme alındı; aboneliğiniz etkin."),
                "Cancelled" => ("aboneliğiniz iptal edildi", "Abonelik iptal edildi. Verileriniz duruyor ve okunabilir."),
                "Refunded" => ("ödemeniz iade edildi", "Uygulanamayan bir plan değişikliğinin ödemesi iade edildi."),
                _ => ("abonelik durumunuz değişti", "Abonelik durumunuz güncellendi."),
            };
            session.Db.Add(SinkMessage.Create(
                session.TenantId, owner.Email, "subscription_changed",
                $"Kulvar: {owner.Tenant} — {subject}", $"{body} (Durum: {owner.Status})",
                actionPath: $"/t/{session.TenantId}/plan", dedupeKey, session.Now));
        }
    }
}
