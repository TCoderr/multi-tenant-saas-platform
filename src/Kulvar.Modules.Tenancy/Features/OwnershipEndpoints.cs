using Kulvar.Modules.Identity.Domain;
using Kulvar.Modules.Tenancy.Domain;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Kulvar.Modules.Tenancy.Features;

public sealed record CreateOwnershipOfferRequest(Guid? ToUserId, bool? DemoteSelf);

internal static class OwnershipEndpoints
{
    private const string PendingOfferIndex = "ix_ownership_offers_tenant_id";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/t/{tenantId:guid}/ownership-offers");
        tenant.MapGet("/", List);
        tenant.MapPost("/", Create);
        tenant.MapPost("/{id:guid}/accept", Accept);
        tenant.MapDelete("/{id:guid}", Withdraw);
    }

    private static async Task<IResult> List(Guid tenantId, ITenantExecutor executor, CancellationToken ct)
    {
        var items = await executor.RunAsync(tenantId, Permission.MembersRead, TenantLock.None, async (ctx, c) =>
        {
            // Yalnız teklifin tarafları görür.
            var rows = await (from o in ctx.Db.Set<OwnershipOffer>()
                              join f in ctx.Db.Set<User>() on o.FromUserId equals f.Id
                              join t in ctx.Db.Set<User>() on o.ToUserId equals t.Id
                              where o.Status == OwnershipOfferStatus.Pending && o.ExpiresAt > ctx.Now && (o.FromUserId == ctx.UserId || o.ToUserId == ctx.UserId)
                              select new { o.Id, o.FromUserId, FromName = f.DisplayName, o.ToUserId, ToName = t.DisplayName, o.DemoteSelf, o.ExpiresAt })
                .AsNoTracking().ToListAsync(c);
            return rows.Select(r => new
            {
                id = r.Id,
                fromUserId = r.FromUserId,
                fromName = r.FromName,
                toUserId = r.ToUserId,
                toName = r.ToName,
                demoteSelf = r.DemoteSelf,
                expiresAt = r.ExpiresAt,
                canAccept = r.ToUserId == ctx.UserId && ctx.Can(Permission.OwnershipAccept),
            }).ToList();
        }, ct);
        return Results.Ok(new { items });
    }

    private static async Task<IResult> Create(Guid tenantId, CreateOwnershipOfferRequest request, ITenantExecutor executor, CancellationToken ct)
    {
        if (request.ToUserId is not { } toUserId)
        {
            throw Problems.Validation("toUserId", "Hedef üye zorunludur.");
        }
        var response = await executor.RunAsync(tenantId, Permission.OwnershipOffer, TenantLock.Governance, async (ctx, c) =>
        {
            if (toUserId == ctx.UserId)
            {
                throw Problems.Validation("toUserId", "Kendinize sahiplik teklif edemezsiniz.");
            }
            var target = await ctx.Db.Set<Membership>().SingleOrDefaultAsync(m => m.UserId == toUserId && m.Status == MembershipStatus.Active, c)
                ?? throw Problems.Validation("toUserId", "Hedef bu şirketin aktif üyesi olmalıdır.");
            if (target.Role == TenantRole.Owner)
            {
                throw Problems.Conflict("already_owner", "Bu üye zaten Owner.");
            }

            var open = await ctx.Db.Set<OwnershipOffer>().SingleOrDefaultAsync(o => o.Status == OwnershipOfferStatus.Pending, c);
            if (open is not null)
            {
                if (open.IsOpen(ctx.Now))
                {
                    throw Problems.Conflict("offer_already_pending", "Bekleyen bir sahiplik teklifi var.");
                }
                open.Resolve(OwnershipOfferStatus.Cancelled, ctx.Now); // Süresi dolmuş teklif kapatılır.
                await ctx.Db.SaveChangesAsync(c);
            }

            var offer = OwnershipOffer.Create(ctx.TenantId, ctx.UserId, toUserId, request.DemoteSelf ?? true, ctx.Now);
            ctx.Db.Add(offer);
            ctx.Audit("ownership.offered", "membership", toUserId, data: new { offer.DemoteSelf });
            ctx.Enqueue(TenancyOutbox.OwnershipOffered, new TenancyOutbox.OfferPayload(offer.Id));
            try
            {
                await ctx.Db.SaveChangesAsync(c);
            }
            catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, PendingOfferIndex))
            {
                throw Problems.Conflict("offer_already_pending", "Bekleyen bir sahiplik teklifi var.");
            }
            return new { id = offer.Id, toUserId, demoteSelf = offer.DemoteSelf, expiresAt = offer.ExpiresAt };
        }, ct);
        return Results.Created($"/api/t/{tenantId}/ownership-offers/{response.id}", response);
    }

    /// <summary>
    /// Kabul: hedef Owner olur; teklif "devret" ise teklif eden aynı transaction'da Admin olur. Şirket satırı kilitlidir;
    /// eşzamanlı kaldırma/ayrılma/rol değişikliği bu işlemle seri çalışır ve şirket hiçbir anda Owner'sız commit edilemez.
    /// </summary>
    private static async Task<IResult> Accept(Guid tenantId, Guid id, ITenantExecutor executor, CancellationToken ct)
    {
        var result = await executor.RunAsync(tenantId, Permission.OwnershipAccept, TenantLock.Governance, async (ctx, c) =>
        {
            var offer = await ctx.Db.Set<OwnershipOffer>().SingleOrDefaultAsync(o => o.Id == id && o.ToUserId == ctx.UserId, c) ?? throw Problems.NotFound();
            if (!offer.IsOpen(ctx.Now))
            {
                throw Problems.Conflict("offer_not_pending", "Teklif artık geçerli değil.");
            }
            var offerer = await ctx.Db.Set<Membership>().SingleOrDefaultAsync(m => m.UserId == offer.FromUserId && m.Status == MembershipStatus.Active, c);
            if (offerer is null || offerer.Role != TenantRole.Owner)
            {
                // Teklif eden artık Owner değil: teklif hükümsüzdür.
                offer.Resolve(OwnershipOfferStatus.Cancelled, ctx.Now);
                await ctx.Db.SaveChangesAsync(c);
                return (Accepted: false, DemotedSelf: false);
            }

            var me = await MemberEndpoints.FindActiveAsync(ctx, ctx.UserId, c);
            me.ChangeRole(TenantRole.Owner);
            if (offer.DemoteSelf)
            {
                offerer.ChangeRole(TenantRole.Admin);
            }
            offer.Resolve(OwnershipOfferStatus.Accepted, ctx.Now);
            ctx.Audit(offer.DemoteSelf ? "ownership.transferred" : "ownership.shared", "membership", ctx.UserId, data: new { from = offer.FromUserId, to = offer.ToUserId });
            await ctx.Db.SaveChangesAsync(c);
            return (Accepted: true, DemotedSelf: offer.DemoteSelf);
        }, ct);

        if (!result.Accepted)
        {
            throw Problems.Conflict("offer_not_pending", "Teklif artık geçerli değil.");
        }
        return Results.Ok(new { role = TenantRole.Owner, previousOwnerDemoted = result.DemotedSelf });
    }

    /// <summary>Teklif eden iptal eder veya hedef reddeder.</summary>
    private static async Task<IResult> Withdraw(Guid tenantId, Guid id, ITenantExecutor executor, CancellationToken ct)
    {
        await executor.RunAsync(tenantId, Permission.MembersRead, TenantLock.Governance, async (ctx, c) =>
        {
            var offer = await ctx.Db.Set<OwnershipOffer>().SingleOrDefaultAsync(o => o.Id == id && (o.FromUserId == ctx.UserId || o.ToUserId == ctx.UserId), c)
                ?? throw Problems.NotFound();
            if (offer.Status != OwnershipOfferStatus.Pending)
            {
                throw Problems.Conflict("offer_not_pending", "Teklif artık geçerli değil.");
            }
            var declined = offer.ToUserId == ctx.UserId;
            offer.Resolve(declined ? OwnershipOfferStatus.Declined : OwnershipOfferStatus.Cancelled, ctx.Now);
            ctx.Audit(declined ? "ownership.declined" : "ownership.cancelled", "membership", offer.ToUserId);
            await ctx.Db.SaveChangesAsync(c);
            return true;
        }, ct);
        return Results.NoContent();
    }
}
