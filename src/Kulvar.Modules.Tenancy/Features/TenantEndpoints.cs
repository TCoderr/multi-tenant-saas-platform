using Kulvar.Modules.Identity.Domain;
using Kulvar.Modules.Tenancy.Domain;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Security;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kulvar.Modules.Tenancy.Features;

public sealed record CreateTenantRequest(string? Name);
public sealed record UpdateTenantSettingsRequest(string? Name);
public sealed record DeletionRequest(string? ConfirmName, string? Password);

internal static class TenantEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/tenants", Create);
        endpoints.MapGet("/api/me/memberships", MyMemberships);

        var tenant = endpoints.MapGroup("/api/t/{tenantId:guid}");
        tenant.MapGet("/", Workspace);
        tenant.MapPatch("/settings", UpdateSettings);
        tenant.MapPost("/deletion", RequestDeletion);
        tenant.MapDelete("/deletion", CancelDeletion);
    }

    /// <summary>
    /// Şirket silme isteği: yalnız Owner, şirket adını ve kendi parolasını doğrulayarak. Şirket "DeletionPending" olur (yazma durur);
    /// bekleme süresi içinde iptal edilebilir, süre dolunca arka planda kalıcı olarak temizlenir.
    /// </summary>
    private static async Task<IResult> RequestDeletion(Guid tenantId, DeletionRequest request, ITenantExecutor executor, IPasswordHasher<User> hasher,
        IEnumerable<ITenantDeletionStep> steps, IOptions<LifecycleOptions> options, CancellationToken ct)
    {
        var response = await executor.RunAsync(tenantId, Permission.TenantDelete, TenantLock.Governance, async (ctx, c) =>
        {
            // Yaşam döngüsü kilidi: devam eden içerik yazmaları biter; bundan sonra başlayanlar yeni durumu (salt-okunur) görür.
            await TenantRegistry.LockForLifecycleAsync(ctx.Db, tenantId, c);
            var tenant = await ctx.Db.Set<Tenant>().SingleAsync(t => t.Id == tenantId, c);
            var user = await ctx.Db.Set<User>().AsNoTracking().SingleAsync(u => u.Id == ctx.UserId, c);
            var nameMatches = string.Equals(request.ConfirmName?.Trim(), tenant.Name, StringComparison.Ordinal);
            var passwordMatches = !string.IsNullOrEmpty(request.Password) && request.Password.Length <= 200
                                  && hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) != PasswordVerificationResult.Failed;
            if (!nameMatches || !passwordMatches)
            {
                throw new ProblemException(403, "confirmation_failed", "Onay başarısız: şirket adını aynen yazın ve parolanızı girin.");
            }
            if (tenant.Status != TenantStatus.Active)
            {
                throw Problems.Conflict("invalid_tenant_state", "Şirket silme isteği için uygun durumda değil.");
            }

            tenant.RequestDeletion(ctx.Now, TimeSpan.FromDays(options.Value.DeletionGraceDays));
            foreach (var step in steps)
            {
                await step.OnDeletionRequestedAsync(ctx, c);
            }
            ctx.Audit("tenant.deletion_requested", "tenant", tenantId, data: new { purgeAfter = tenant.PurgeAfter });
            await ctx.Db.SaveChangesAsync(c);
            return new { status = tenant.Status, purgeAfter = tenant.PurgeAfter };
        }, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> CancelDeletion(Guid tenantId, ITenantExecutor executor, CancellationToken ct)
    {
        var response = await executor.RunAsync(tenantId, Permission.TenantDeleteCancel, TenantLock.Governance, async (ctx, c) =>
        {
            // Temizleme aynı satırı FOR UPDATE alır: iptal ya temizlemeden önce commit olur ya da şirket artık görünmezdir (404).
            if (await TenantRegistry.LockForLifecycleAsync(ctx.Db, tenantId, c) != TenantStatus.DeletionPending)
            {
                throw Problems.Conflict("invalid_tenant_state", "Bekleyen silme isteği yok.");
            }
            var tenant = await ctx.Db.Set<Tenant>().SingleAsync(t => t.Id == tenantId, c);
            tenant.CancelDeletion();
            ctx.Audit("tenant.deletion_cancelled", "tenant", tenantId);
            await ctx.Db.SaveChangesAsync(c);
            return new { status = tenant.Status };
        }, ct);
        return Results.Ok(response);
    }

    /// <summary>
    /// Şirket oluşturma: şirket kapsamının "üyelikten sonra" kuralının belgeli istisnası. Kimlik sunucuda üretilir,
    /// kapsam o kimliğe ayarlanır; şirket + Owner üyeliği + modül adımları (deneme aboneliği vb.) tek transaction'dadır.
    /// </summary>
    private static async Task<IResult> Create(CreateTenantRequest request, ICurrentUser current, AppDb db, IEnumerable<ITenantProvisioningStep> steps, CancellationToken ct)
    {
        var userId = current.UserId;
        var validator = new Validator();
        var name = validator.Required("name", request.Name, 120, minLength: 2);
        validator.ThrowIfInvalid();

        var tenantId = await db.RunAsync(DbScope.ForUser(userId), async (session, c) =>
        {
            var verified = await session.Db.Set<User>().Where(u => u.Id == userId).Select(u => u.EmailVerifiedAt != null).SingleAsync(c);
            if (!verified)
            {
                throw new ProblemException(403, "email_not_verified", "Şirket oluşturmak için önce e-posta adresinizi doğrulayın.");
            }

            var id = Ids.New();
            await session.EnterTenantAsync(id, c);
            session.Db.Add(Tenant.Create(id, name, userId, session.Now));
            session.Db.Add(Membership.Create(id, userId, TenantRole.Owner, session.Now));
            await session.Db.SaveChangesAsync(c);
            foreach (var step in steps)
            {
                await step.OnTenantCreatedAsync(session, id, c);
            }
            session.Db.Add(AuditEvent.Create(id, userId, "tenant.created", "tenant", id, null, null, session.Now));
            await session.Db.SaveChangesAsync(c);
            return id;
        }, ct);

        return Results.Created($"/api/t/{tenantId}", new { tenantId });
    }

    /// <summary>Kullanıcı kapsamlı: yalnız kendi üyeliklerinin metadata'sı (ad, rol, durum). Şirket içeriği döndürmez.</summary>
    private static async Task<IResult> MyMemberships(ICurrentUser current, AppDb db, CancellationToken ct)
    {
        var userId = current.UserId;
        var items = await db.RunAsync(DbScope.ForUser(userId), async (session, c) =>
            // EF filtresi bilinçli kapalı (şirket kapsamı yok); görünürlüğü RLS'in kullanıcı kapsamlı politikası belirler.
            await (from m in session.Db.Set<Membership>().IgnoreQueryFilters()
                   join t in session.Db.Set<Tenant>() on m.TenantId equals t.Id
                   where m.UserId == userId && m.Status == MembershipStatus.Active
                         && t.Status != TenantStatus.Purging && t.Status != TenantStatus.Deleted
                   orderby t.Name, t.Id
                   select new { tenantId = t.Id, tenantName = t.Name, role = m.Role, tenantStatus = t.Status })
                .AsNoTracking().ToListAsync(c), ct);
        return Results.Ok(new { items });
    }

    private static async Task<IResult> Workspace(Guid tenantId, HttpContext http, ITenantExecutor executor, IEntitlementReader entitlements, CancellationToken ct)
    {
        var response = await executor.RunAsync(tenantId, Permission.WorkspaceRead, TenantLock.None, async (ctx, c) =>
        {
            var seatsUsed = await ctx.Db.Set<Membership>().CountAsync(m => m.Status == MembershipStatus.Active, c);
            var pendingInvites = await ctx.Db.Set<Invite>().CountAsync(i => i.Status == InviteStatus.Pending && i.ExpiresAt > ctx.Now, c);
            var usage = await entitlements.ReadUsageAsync(ctx.Session, tenantId, c);
            var lifecycle = await ctx.Db.Set<Tenant>().AsNoTracking().Where(t => t.Id == tenantId).Select(t => new { t.PurgeAfter, t.SuspendedReason }).SingleAsync(c);
            var e = ctx.Entitlements;
            return new
            {
                tenant = new
                {
                    id = ctx.TenantId, name = ctx.TenantName, status = ctx.TenantStatus, version = ctx.TenantVersion,
                    purgeAfter = lifecycle.PurgeAfter, suspendedReason = lifecycle.SuspendedReason,
                },
                userId = ctx.UserId,
                role = ctx.Role,
                permissions = AccessRules.GrantedCodes(ctx.Role, ctx.Mode),
                accessMode = ctx.Mode,
                subscription = new { status = e.SubscriptionStatus, planCode = e.PlanCode, planName = e.PlanName, trialEndsAt = e.TrialEndsAt, graceEndsAt = e.GraceEndsAt },
                entitlements = new { seatLimit = e.SeatLimit, monthlyTicketLimit = e.MonthlyTicketLimit, exportEnabled = e.ExportEnabled },
                usage = new { seatsUsed, pendingInvites, ticketsThisPeriod = usage.TicketsThisPeriod, periodResetsAt = usage.PeriodResetsAt },
            };
        }, ct);
        ETags.Set(http, response.tenant.version);
        return Results.Ok(response);
    }

    private static async Task<IResult> UpdateSettings(Guid tenantId, UpdateTenantSettingsRequest request, HttpContext http, ITenantExecutor executor, CancellationToken ct)
    {
        var expected = ETags.RequireIfMatch(http);
        var validator = new Validator();
        var name = validator.Required("name", request.Name, 120, minLength: 2);
        validator.ThrowIfInvalid();

        var version = await executor.RunAsync(tenantId, Permission.TenantSettings, TenantLock.Governance, async (ctx, c) =>
        {
            var tenant = await ctx.Db.Set<Tenant>().SingleAsync(t => t.Id == tenantId, c);
            ETags.EnsureMatch(expected, tenant.Version);
            tenant.Rename(name);
            ctx.Audit("tenant.renamed", "tenant", tenantId);
            await ctx.Db.SaveChangesAsync(c);
            return tenant.Version;
        }, ct);
        ETags.Set(http, version);
        return Results.Ok(new { version });
    }
}
