using Kulvar.Modules.Identity.Domain;
using Kulvar.Modules.Tenancy.Domain;
using Kulvar.Platform.Http;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Kulvar.Modules.Tenancy.Features;

public sealed record ChangeRoleRequest(TenantRole? Role);

internal static class MemberEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/t/{tenantId:guid}");
        tenant.MapGet("/members", List);
        tenant.MapPatch("/members/{userId:guid}", ChangeRole);
        tenant.MapDelete("/members/{userId:guid}", Remove);
        tenant.MapPost("/members/leave", Leave);
    }

    private static async Task<IResult> List(Guid tenantId, ITenantExecutor executor, CancellationToken ct)
    {
        var items = await executor.RunAsync(tenantId, Permission.MembersRead, TenantLock.None, async (ctx, c) =>
        {
            var showEmail = ctx.Can(Permission.MembersPii);
            var rows = await (from m in ctx.Db.Set<Membership>()
                              join u in ctx.Db.Set<User>() on m.UserId equals u.Id
                              where m.Status == MembershipStatus.Active
                              orderby u.DisplayName, u.Id
                              select new { m.UserId, u.DisplayName, u.Email, m.Role, m.CreatedAt, m.Version })
                .AsNoTracking().ToListAsync(c);
            return rows.Select(r => new
            {
                userId = r.UserId,
                displayName = r.DisplayName,
                email = showEmail || r.UserId == ctx.UserId ? r.Email : null,
                role = r.Role,
                joinedAt = r.CreatedAt,
                version = r.Version,
                isSelf = r.UserId == ctx.UserId,
            }).ToList();
        }, ct);
        return Results.Ok(new { items });
    }

    private static async Task<IResult> ChangeRole(Guid tenantId, Guid userId, ChangeRoleRequest request, HttpContext http, ITenantExecutor executor, CancellationToken ct)
    {
        var expected = ETags.RequireIfMatch(http);
        if (request.Role is not { } newRole)
        {
            throw Problems.Validation("role", "Rol zorunludur.");
        }

        var version = await executor.RunAsync(tenantId, Permission.MembersManage, TenantLock.Governance, async (ctx, c) =>
        {
            var target = await FindActiveAsync(ctx, userId, c);
            ETags.EnsureMatch(expected, target.Version);
            var owners = await CountOwnersAsync(ctx, c);
            switch (RolePolicy.CanChangeRole(ctx.Role, target.Role, newRole, owners))
            {
                case MemberDecision.Forbidden:
                    throw Problems.Forbidden(AccessRules.Code(Permission.MembersManage));
                case MemberDecision.LastOwner:
                    throw LastOwner();
                case MemberDecision.OwnerRequiresOffer:
                    throw Problems.BadRequest("owner_requires_offer", "Owner rolü yalnız sahiplik teklifi ve hedefin kabulüyle verilir.");
                case MemberDecision.NoChange:
                    return target.Version;
            }

            var previous = target.Role;
            target.ChangeRole(newRole);
            ctx.Audit("member.role_changed", "membership", userId, data: new { from = previous, to = newRole });
            await ctx.Db.SaveChangesAsync(c);
            return target.Version;
        }, ct);
        ETags.Set(http, version);
        return Results.Ok(new { version });
    }

    private static async Task<IResult> Remove(Guid tenantId, Guid userId, ITenantExecutor executor, IEnumerable<IMemberRemovalStep> steps, CancellationToken ct)
    {
        await executor.RunAsync(tenantId, Permission.MembersManage, TenantLock.Governance, async (ctx, c) =>
        {
            if (userId == ctx.UserId)
            {
                throw Problems.BadRequest("use_leave", "Kendinizi kaldırmak için şirketten ayrılın.");
            }
            var target = await FindActiveAsync(ctx, userId, c);
            var owners = await CountOwnersAsync(ctx, c);
            switch (RolePolicy.CanRemove(ctx.Role, target.Role, owners))
            {
                case MemberDecision.Forbidden:
                    throw Problems.Forbidden(AccessRules.Code(Permission.MembersManage));
                case MemberDecision.LastOwner:
                    throw LastOwner();
            }
            await RemoveCoreAsync(ctx, target, steps, "member.removed", c);
            return true;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Leave(Guid tenantId, ITenantExecutor executor, IEnumerable<IMemberRemovalStep> steps, CancellationToken ct)
    {
        await executor.RunAsync(tenantId, Permission.MembersLeave, TenantLock.Governance, async (ctx, c) =>
        {
            var self = await FindActiveAsync(ctx, ctx.UserId, c);
            var owners = await CountOwnersAsync(ctx, c);
            if (RolePolicy.CanLeave(self.Role, owners) == MemberDecision.LastOwner)
            {
                throw LastOwner();
            }
            await RemoveCoreAsync(ctx, self, steps, "member.left", c);
            return true;
        }, ct);
        return Results.NoContent();
    }

    private static async Task RemoveCoreAsync(TenantContext ctx, Membership target, IEnumerable<IMemberRemovalStep> steps, string action, CancellationToken ct)
    {
        // Üyelik satırını güncellemek, o üyenin devam eden yazma isteklerinin (FOR SHARE) bitmesini bekler.
        target.Remove(ctx.Now);
        // Ayrılan üyenin taraf olduğu açık sahiplik teklifi hükümsüz kalır.
        await ctx.Db.Set<OwnershipOffer>()
            .Where(o => o.Status == OwnershipOfferStatus.Pending && (o.FromUserId == target.UserId || o.ToUserId == target.UserId))
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.Status, OwnershipOfferStatus.Cancelled).SetProperty(o => o.ResolvedAt, ctx.Now), ct);
        foreach (var step in steps)
        {
            await step.OnMemberRemovedAsync(ctx, target.UserId, ct);
        }
        ctx.Audit(action, "membership", target.UserId, data: new { role = target.Role });
        await ctx.Db.SaveChangesAsync(ct);
    }

    internal static async Task<Membership> FindActiveAsync(TenantContext ctx, Guid userId, CancellationToken ct) =>
        await ctx.Db.Set<Membership>().SingleOrDefaultAsync(m => m.UserId == userId && m.Status == MembershipStatus.Active, ct)
        ?? throw Problems.NotFound();

    /// <summary>Şirket satırı kilitliyken (Governance) çağrılır; sayım o yüzden yarışa kapalıdır.</summary>
    internal static Task<int> CountOwnersAsync(TenantContext ctx, CancellationToken ct) =>
        ctx.Db.Set<Membership>().CountAsync(m => m.Role == TenantRole.Owner && m.Status == MembershipStatus.Active, ct);

    internal static ProblemException LastOwner() =>
        Problems.Conflict("last_owner", "Şirkette en az bir Owner kalmalıdır. Önce sahipliği başka bir üyeye devredin.");
}
