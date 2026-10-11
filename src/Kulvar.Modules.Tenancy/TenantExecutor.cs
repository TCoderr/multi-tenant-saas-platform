using Kulvar.Modules.Tenancy.Domain;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Observability;
using Kulvar.Platform.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Kulvar.Modules.Tenancy;

/// <summary>
/// Şirket kapsamlı her isteğin tek giriş noktası. URL'deki şirket kimliği bir istektir, yetki değildir:
/// güncel üyelik veritabanından okunur ve ancak ondan sonra şirket kapsamına (RLS + EF filtresi) girilir.
/// </summary>
internal sealed class TenantExecutor(AppDb db, ICurrentUser current, IEntitlementReader entitlements) : ITenantExecutor
{
    public Task<T> RunAsync<T>(Guid tenantId, Permission permission, TenantLock tenantLock, Func<TenantContext, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var userId = current.UserId;
        return db.RunAsync(DbScope.ForUser(userId), async (session, c) =>
        {
            // 1) Kullanıcı kapsamıyla üyelik çözümleme. Şirket kapsamı henüz yok; EF filtresi bu yüzden bilinçli olarak kapatılır
            //    ve satır görünürlüğünü RLS'in kullanıcı kapsamlı politikası belirler (yalnız kendi üyeliklerim).
            var isMember = await session.Db.Set<Membership>().IgnoreQueryFilters()
                .AnyAsync(m => m.TenantId == tenantId && m.UserId == userId && m.Status == MembershipStatus.Active, c);
            if (!isMember)
            {
                throw Denied("not_member");
            }

            // 2) Üyelik doğrulandı: şirket kapsamına gir.
            await session.EnterTenantAsync(tenantId, c);

            // 3) Kilit (bekleme sırasında rol/durum değişmiş olabilir; kilitten sonra yeniden okunur).
            TenantRole? role;
            if (tenantLock == TenantLock.Governance)
            {
                await TenantRegistry.LockAsync(session.Db, tenantId, c);
                role = await ReadRoleAsync(session.Db, tenantId, userId, forShare: false, c);
            }
            else
            {
                role = await ReadRoleAsync(session.Db, tenantId, userId, forShare: tenantLock == TenantLock.ActorShared, c);
            }
            if (role is null)
            {
                throw Denied("membership_changed");
            }

            var tenant = await session.Db.Set<Tenant>().AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => new { t.Name, t.Status, t.Version })
                .SingleOrDefaultAsync(c);
            if (tenant is null)
            {
                throw Denied("tenant_missing");
            }

            var entitlement = await entitlements.ReadAsync(session, tenantId, c);
            var mode = AccessRules.ComputeMode(tenant.Status, entitlement.SubscriptionStatus) ?? throw Denied("tenant_gone");

            var context = new TenantContext
            {
                Session = session,
                TenantId = tenantId,
                UserId = userId,
                Role = role.Value,
                TenantStatus = tenant.Status,
                TenantName = tenant.Name,
                TenantVersion = tenant.Version,
                Entitlements = entitlement,
                Mode = mode,
            };
            context.Require(permission);
            return await work(context, c);
        }, ct);
    }

    private static async Task<TenantRole?> ReadRoleAsync(AppDbContext db, Guid tenantId, Guid userId, bool forShare, CancellationToken ct)
    {
        var sql = "SELECT role FROM app.memberships WHERE tenant_id = $1 AND user_id = $2 AND status = 'Active'" + (forShare ? " FOR SHARE" : string.Empty);
        var value = await db.ScalarAsync(sql, ct, tenantId, userId);
        return value is string s ? Enum.Parse<TenantRole>(s) : null;
    }

    // Üye olmayan, bilinmeyen ve silinmiş şirket aynı yanıtı alır: varlık sızdırılmaz.
    private static ProblemException Denied(string reason)
    {
        Telemetry.TenantAccessDenied.Add(1, new KeyValuePair<string, object?>("reason", reason));
        return Problems.TenantNotFound();
    }
}
