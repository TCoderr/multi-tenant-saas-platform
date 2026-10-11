using Kulvar.Modules.Identity.Domain;
using Kulvar.Modules.Tenancy.Domain;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Messaging;
using Kulvar.Platform.Security;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Kulvar.Modules.Tenancy.Features;

public sealed record CreateInviteRequest(string? Email, TenantRole? Role);
public sealed record InviteTokenRequest(string? Token);

public static class TenancyOutbox
{
    public const string InviteCreated = "tenancy.invite_created";
    public const string InviteAccepted = "tenancy.invite_accepted";
    public const string OwnershipOffered = "tenancy.ownership_offered";

    public sealed record InvitePayload(Guid InviteId);
    public sealed record OfferPayload(Guid OfferId);
}

internal static class InviteEndpoints
{
    private const string PendingEmailIndex = "ix_invites_tenant_id_normalized_email";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/t/{tenantId:guid}");
        tenant.MapGet("/invites", List);
        tenant.MapPost("/invites", Create);
        tenant.MapDelete("/invites/{id:guid}", Revoke);
        endpoints.MapPost("/api/invites/lookup", Lookup);
        endpoints.MapPost("/api/invites/accept", Accept);
    }

    private static async Task<IResult> List(Guid tenantId, ITenantExecutor executor, CancellationToken ct)
    {
        var items = await executor.RunAsync(tenantId, Permission.MembersManage, TenantLock.None, async (ctx, c) =>
        {
            var rows = await (from i in ctx.Db.Set<Invite>()
                              join u in ctx.Db.Set<User>() on i.InvitedByUserId equals u.Id
                              orderby i.CreatedAt descending, i.Id descending
                              select new { Invite = i, InvitedByName = u.DisplayName })
                .AsNoTracking().Take(200).ToListAsync(c);
            // Token (ve hash'i) hiçbir yanıtta yer almaz: yöneticiler başkasının davet bağlantısını elde edemez.
            return rows.Select(r => new
            {
                id = r.Invite.Id,
                email = r.Invite.Email,
                role = r.Invite.Role,
                state = r.Invite.StateAt(ctx.Now),
                invitedByName = r.InvitedByName,
                createdAt = r.Invite.CreatedAt,
                expiresAt = r.Invite.ExpiresAt,
            }).ToList();
        }, ct);
        return Results.Ok(new { items });
    }

    private static async Task<IResult> Create(Guid tenantId, CreateInviteRequest request, ITenantExecutor executor, CancellationToken ct)
    {
        var validator = new Validator();
        if (!EmailNormalizer.IsPlausible(request.Email))
        {
            validator.Add("email", "Geçerli bir e-posta adresi girin.");
        }
        if (request.Role is null)
        {
            validator.Add("role", "Rol zorunludur.");
        }
        validator.ThrowIfInvalid();
        var role = request.Role!.Value;
        var normalized = EmailNormalizer.Normalize(request.Email!);

        var response = await executor.RunAsync(tenantId, Permission.MembersInvite, TenantLock.Governance, async (ctx, c) =>
        {
            if (!RolePolicy.CanInvite(ctx.Role, role))
            {
                // Owner rolü davetle verilemez; Admin yalnız Agent/Viewer davet edebilir.
                throw role == TenantRole.Owner
                    ? Problems.BadRequest("owner_requires_offer", "Owner rolü yalnız sahiplik teklifi ve hedefin kabulüyle verilir.")
                    : Problems.Forbidden(AccessRules.Code(Permission.MembersInvite));
            }

            var alreadyMember = await (from m in ctx.Db.Set<Membership>()
                                       join u in ctx.Db.Set<User>() on m.UserId equals u.Id
                                       where m.Status == MembershipStatus.Active && u.NormalizedEmail == normalized
                                       select m.UserId).AnyAsync(c);
            if (alreadyMember)
            {
                throw Problems.Conflict("already_member", "Bu kişi zaten şirketin üyesi.");
            }

            var pending = await ctx.Db.Set<Invite>().Where(i => i.Status == InviteStatus.Pending).ToListAsync(c);
            var sameEmail = pending.SingleOrDefault(i => i.NormalizedEmail == normalized);
            if (sameEmail is not null)
            {
                if (!sameEmail.IsExpired(ctx.Now))
                {
                    throw Problems.Conflict("invite_already_pending", "Bu adrese bekleyen bir davet var.");
                }
                sameEmail.Revoke(ctx.Now); // Süresi dolmuş davetin yerini yenisi alır.
                await ctx.Db.SaveChangesAsync(c);
            }

            // Koltuk: aktif üyeler + koltuk ayıran (bekleyen, süresi dolmamış) davetler. Şirket satırı kilitli olduğundan sayım yarışa kapalıdır.
            var seatsUsed = await ctx.Db.Set<Membership>().CountAsync(m => m.Status == MembershipStatus.Active, c);
            var seatsReserved = pending.Count(i => i.HoldsSeat(ctx.Now));
            SeatRules.EnsureAvailable(seatsUsed + seatsReserved, ctx.Entitlements.SeatLimit);

            var invite = Invite.Create(ctx.TenantId, request.Email!, role, ctx.UserId, ctx.Now);
            ctx.Db.Add(invite);
            ctx.Audit("invite.created", "invite", invite.Id, data: new { role });
            ctx.Enqueue(TenancyOutbox.InviteCreated, new TenancyOutbox.InvitePayload(invite.Id));
            try
            {
                await ctx.Db.SaveChangesAsync(c);
            }
            catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, PendingEmailIndex))
            {
                throw Problems.Conflict("invite_already_pending", "Bu adrese bekleyen bir davet var.");
            }
            return new { id = invite.Id, email = invite.Email, role = invite.Role, state = invite.StateAt(ctx.Now), expiresAt = invite.ExpiresAt };
        }, ct);
        return Results.Created($"/api/t/{tenantId}/invites/{response.id}", response);
    }

    private static async Task<IResult> Revoke(Guid tenantId, Guid id, ITenantExecutor executor, CancellationToken ct)
    {
        await executor.RunAsync(tenantId, Permission.MembersManage, TenantLock.Governance, async (ctx, c) =>
        {
            var invite = await ctx.Db.Set<Invite>().SingleOrDefaultAsync(i => i.Id == id, c) ?? throw Problems.NotFound();
            if (!RolePolicy.CanInvite(ctx.Role, invite.Role))
            {
                throw Problems.Forbidden(AccessRules.Code(Permission.MembersManage));
            }
            if (invite.Status != InviteStatus.Pending)
            {
                throw Problems.Conflict("invite_not_pending", "Davet artık beklemede değil.");
            }
            invite.Revoke(ctx.Now);
            ctx.Audit("invite.revoked", "invite", invite.Id);
            await ctx.Db.SaveChangesAsync(c);
            return true;
        }, ct);
        return Results.NoContent();
    }

    /// <summary>Davet bağlantısının önizlemesi: kabul etmeden önce hangi şirkete, hangi rolle davet edildiğini gösterir.</summary>
    private static async Task<IResult> Lookup(InviteTokenRequest request, ICurrentUser current, AppDb db, CancellationToken ct)
    {
        var userId = current.UserId;
        var hashHex = RequireToken(request.Token);
        var response = await db.RunAsync(new DbScope(UserId: userId, InviteTokenHashHex: hashHex), async (session, c) =>
        {
            var invite = await FindByTokenAsync(session, hashHex, c);
            var user = await session.Db.Set<User>().AsNoTracking().SingleAsync(u => u.Id == userId, c);
            // Token ile doğrulanan davetin şirketine girilir (belgeli istisna): şirket adı ancak bu kapsamda görünür.
            await session.EnterTenantAsync(invite.TenantId, c);
            var tenant = await session.Db.Set<Tenant>().AsNoTracking().Where(t => t.Id == invite.TenantId).Select(t => new { t.Name, t.Status }).SingleAsync(c);
            return new
            {
                tenantName = tenant.Name,
                role = invite.Role,
                state = tenant.Status == TenantStatus.Active ? invite.StateAt(session.Now) : InviteState.Revoked,
                expiresAt = invite.ExpiresAt,
                invitedEmail = EmailNormalizer.Mask(invite.Email),
                emailMatches = invite.NormalizedEmail == user.NormalizedEmail,
                emailVerified = user.IsEmailVerified,
            };
        }, ct);
        return Results.Ok(response);
    }

    /// <summary>
    /// Davet kabulü: şirket kapsamının "üyelikten sonra" kuralının ikinci belgeli istisnası. Davet satırı yalnız token hash kapsamıyla
    /// okunur ve doğrulanır; kapsam davetin kendi şirketine ayarlanır. Rol davet satırından gelir.
    /// </summary>
    private static async Task<IResult> Accept(InviteTokenRequest request, ICurrentUser current, AppDb db, IEntitlementReader entitlements, CancellationToken ct)
    {
        var userId = current.UserId;
        var hashHex = RequireToken(request.Token);
        var tenantId = await db.RunAsync(new DbScope(UserId: userId, InviteTokenHashHex: hashHex), async (session, c) =>
        {
            var found = await FindByTokenAsync(session, hashHex, c);
            var user = await session.Db.Set<User>().AsNoTracking().SingleAsync(u => u.Id == userId, c);
            if (!user.IsEmailVerified)
            {
                throw new ProblemException(403, "email_not_verified", "Daveti kabul etmek için önce e-posta adresinizi doğrulayın.");
            }
            if (found.NormalizedEmail != user.NormalizedEmail)
            {
                throw new ProblemException(403, "invite_email_mismatch", "Bu davet başka bir e-posta adresine gönderilmiş.");
            }

            await session.EnterTenantAsync(found.TenantId, c);
            // Şirket satırı kilidi: kabul, diğer üyelik işlemleriyle (davet, kaldırma, koltuk sayımı) seri çalışır.
            var tenantStatus = await TenantRegistry.LockAsync(session.Db, found.TenantId, c);
            var entitlement = await entitlements.ReadAsync(session, found.TenantId, c);
            if (tenantStatus != TenantStatus.Active || AccessRules.ComputeMode(tenantStatus.Value, entitlement.SubscriptionStatus) != AccessMode.Full)
            {
                throw Problems.Conflict("tenant_not_accepting_members", "Şirket şu anda yeni üye kabul etmiyor.");
            }

            // Kilit altında yeniden okunur: eşzamanlı ikinci kabul veya iptal burada görülür.
            var invite = await session.Db.Set<Invite>().SingleAsync(i => i.Id == found.Id, c);
            if (invite.Status != InviteStatus.Pending)
            {
                throw Problems.Conflict("invite_not_pending", "Davet artık geçerli değil.");
            }
            if (invite.IsExpired(session.Now))
            {
                throw new ProblemException(410, "invite_expired", "Davetin süresi dolmuş. Yöneticinizden yeni davet isteyin.");
            }

            var membership = await session.Db.Set<Membership>().SingleOrDefaultAsync(m => m.UserId == userId, c);
            if (membership is { IsActive: true })
            {
                throw Problems.Conflict("already_member", "Zaten bu şirketin üyesisiniz.");
            }
            // Kabul anında koltuk yeniden doğrulanır (arada plan düşürülmüş olabilir). Bu davetin ayırdığı koltuk kullanıcıya geçer.
            var seatsUsed = await session.Db.Set<Membership>().CountAsync(m => m.Status == MembershipStatus.Active, c);
            SeatRules.EnsureAvailable(seatsUsed, entitlement.SeatLimit);

            if (membership is null)
            {
                session.Db.Add(Membership.Create(invite.TenantId, userId, invite.Role, session.Now));
            }
            else
            {
                membership.Reactivate(invite.Role, session.Now);
            }
            invite.Accept(userId, session.Now);
            session.Db.Add(AuditEvent.Create(invite.TenantId, userId, "invite.accepted", "invite", invite.Id, null, new { role = invite.Role }, session.Now));
            session.Db.Add(OutboxMessage.Create(invite.TenantId, null, TenancyOutbox.InviteAccepted, new TenancyOutbox.InvitePayload(invite.Id), session.Now));
            await session.Db.SaveChangesAsync(c);
            return invite.TenantId;
        }, ct);
        return Results.Ok(new { tenantId });
    }

    private static string RequireToken(string? token) =>
        Tokens.LooksValid(token) ? Tokens.HashHex(token!) : throw InviteNotFound();

    private static async Task<Invite> FindByTokenAsync(DbSession session, string hashHex, CancellationToken ct)
    {
        var hash = Convert.FromHexString(hashHex);
        // Şirket kapsamı henüz yok: EF filtresi bilinçli kapalı. Görünürlüğü RLS'in token kapsamlı politikası belirler (yalnız bu hash'in satırı).
        return await session.Db.Set<Invite>().IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(i => i.TokenHash == hash, ct) ?? throw InviteNotFound();
    }

    private static ProblemException InviteNotFound() => new(404, "invite_not_found", "Davet bulunamadı veya bağlantı geçersiz.");
}

public static class SeatRules
{
    /// <summary>Koltuk limiti dolmuşsa 403 <c>quota_exceeded</c> (metric=seats). Bu bir abonelik kotasıdır; hız sınırı (429) değildir.</summary>
    public static void EnsureAvailable(int used, int limit)
    {
        if (used >= limit)
        {
            throw new ProblemException(403, "quota_exceeded", "Planınızın koltuk limiti dolu.", new Dictionary<string, object?>
            {
                ["metric"] = "seats",
                ["limit"] = limit,
                ["used"] = used,
            });
        }
    }
}
