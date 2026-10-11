using Kulvar.Platform.Tenancy;

namespace Kulvar.Modules.Tenancy.Domain;

public enum MemberDecision
{
    Allowed,

    /// <summary>Aktörün rolü bu hedef/rol için yetkili değil.</summary>
    Forbidden,

    /// <summary>İşlem şirketi Owner'sız bırakırdı.</summary>
    LastOwner,

    /// <summary>Owner rolü rol değişikliğiyle verilemez; sahiplik teklifi ve hedefin kabulü gerekir.</summary>
    OwnerRequiresOffer,

    /// <summary>Hedefin rolü zaten istenen rol.</summary>
    NoChange,
}

/// <summary>Üyelik yönetimi kuralları (02 §3). Saf fonksiyonlar: veritabanı, HTTP veya saat bağımlılığı yok.</summary>
public static class RolePolicy
{
    /// <summary>Davetle verilebilecek roller. Owner hiçbir zaman davetle verilmez.</summary>
    public static bool CanInvite(TenantRole actor, TenantRole invitedRole) => actor switch
    {
        TenantRole.Owner => invitedRole is TenantRole.Admin or TenantRole.Agent or TenantRole.Viewer,
        TenantRole.Admin => invitedRole is TenantRole.Agent or TenantRole.Viewer,
        _ => false,
    };

    public static MemberDecision CanChangeRole(TenantRole actor, TenantRole targetCurrent, TenantRole newRole, int activeOwnerCount)
    {
        if (newRole == TenantRole.Owner)
        {
            return MemberDecision.OwnerRequiresOffer;
        }
        if (targetCurrent == newRole)
        {
            return MemberDecision.NoChange;
        }
        switch (actor)
        {
            case TenantRole.Owner:
                // Bir Owner'ı (kendisi dahil) düşürmek yalnız başka aktif Owner kalıyorsa mümkündür.
                return targetCurrent == TenantRole.Owner && activeOwnerCount <= 1 ? MemberDecision.LastOwner : MemberDecision.Allowed;
            case TenantRole.Admin:
                var targetManageable = targetCurrent is TenantRole.Agent or TenantRole.Viewer;
                var roleGrantable = newRole is TenantRole.Agent or TenantRole.Viewer;
                return targetManageable && roleGrantable ? MemberDecision.Allowed : MemberDecision.Forbidden;
            default:
                return MemberDecision.Forbidden;
        }
    }

    /// <summary>Başka bir üyeyi kaldırma. Kendini kaldırma "ayrılma"dır (<see cref="CanLeave"/>).</summary>
    public static MemberDecision CanRemove(TenantRole actor, TenantRole target, int activeOwnerCount) => actor switch
    {
        TenantRole.Owner => target == TenantRole.Owner && activeOwnerCount <= 1 ? MemberDecision.LastOwner : MemberDecision.Allowed,
        TenantRole.Admin => target is TenantRole.Agent or TenantRole.Viewer ? MemberDecision.Allowed : MemberDecision.Forbidden,
        _ => MemberDecision.Forbidden,
    };

    public static MemberDecision CanLeave(TenantRole actor, int activeOwnerCount) =>
        actor == TenantRole.Owner && activeOwnerCount <= 1 ? MemberDecision.LastOwner : MemberDecision.Allowed;
}
