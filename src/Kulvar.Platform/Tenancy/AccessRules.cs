namespace Kulvar.Platform.Tenancy;

public enum TenantRole { Owner, Admin, Agent, Viewer }

public enum TenantStatus { Active, Suspended, DeletionPending, Purging, Deleted }

public enum SubscriptionStatus { Trialing, Active, PastDue, Suspended, Cancelled }

/// <summary>Şirket durumu × abonelik durumu sonucu (02 §5).</summary>
public enum AccessMode { Full, Recovery, ReadOnly, Locked }

public enum Permission
{
    WorkspaceRead,
    MembersLeave,
    TenantSettings,
    TenantDelete,
    TenantDeleteCancel,
    CustomersRead,
    CustomersPii,
    CustomersWrite,
    CustomersDelete,
    TicketsRead,
    TicketsWrite,
    MembersRead,
    MembersPii,
    MembersInvite,
    MembersManage,
    OwnershipOffer,
    OwnershipAccept,
    BillingRead,
    BillingManage,
    AuditRead,
    ExportManage,
}

/// <summary>Bir şirketin o anki limitleri ve özellikleri. Kaynağı her istekte veritabanıdır; önbellek yok.</summary>
public sealed record Entitlements(
    SubscriptionStatus SubscriptionStatus,
    string PlanCode,
    string PlanName,
    int SeatLimit,
    int MonthlyTicketLimit,
    bool ExportEnabled,
    DateTimeOffset? TrialEndsAt,
    DateTimeOffset? GraceEndsAt);

public static class AccessRules
{
    private static readonly TenantRole[] All = [TenantRole.Owner, TenantRole.Admin, TenantRole.Agent, TenantRole.Viewer];
    private static readonly TenantRole[] OwnerOnly = [TenantRole.Owner];
    private static readonly TenantRole[] OwnerAdmin = [TenantRole.Owner, TenantRole.Admin];
    private static readonly TenantRole[] Staff = [TenantRole.Owner, TenantRole.Admin, TenantRole.Agent];

    private static readonly AccessMode[] AnyMode = [AccessMode.Full, AccessMode.Recovery, AccessMode.ReadOnly, AccessMode.Locked];
    private static readonly AccessMode[] Readable = [AccessMode.Full, AccessMode.Recovery, AccessMode.ReadOnly];
    private static readonly AccessMode[] FullOnly = [AccessMode.Full];
    private static readonly AccessMode[] FullOrRecovery = [AccessMode.Full, AccessMode.Recovery];

    private sealed record Rule(TenantRole[] Roles, AccessMode[] Modes, string Code);

    private static readonly Dictionary<Permission, Rule> Rules = new()
    {
        [Permission.WorkspaceRead] = new(All, AnyMode, "workspace.read"),
        [Permission.MembersLeave] = new(All, AnyMode, "members.leave"),
        [Permission.TenantSettings] = new(OwnerOnly, FullOnly, "tenant.settings"),
        [Permission.TenantDelete] = new(OwnerOnly, FullOrRecovery, "tenant.delete"),
        [Permission.TenantDeleteCancel] = new(OwnerOnly, [AccessMode.ReadOnly], "tenant.delete.cancel"),
        [Permission.CustomersRead] = new(All, Readable, "customers.read"),
        [Permission.CustomersPii] = new(Staff, Readable, "customers.pii"),
        [Permission.CustomersWrite] = new(Staff, FullOnly, "customers.write"),
        [Permission.CustomersDelete] = new(OwnerAdmin, FullOnly, "customers.delete"),
        [Permission.TicketsRead] = new(All, Readable, "tickets.read"),
        [Permission.TicketsWrite] = new(Staff, FullOnly, "tickets.write"),
        [Permission.MembersRead] = new(All, Readable, "members.read"),
        [Permission.MembersPii] = new(OwnerAdmin, Readable, "members.pii"),
        [Permission.MembersInvite] = new(OwnerAdmin, FullOnly, "members.invite"),
        [Permission.MembersManage] = new(OwnerAdmin, FullOrRecovery, "members.manage"),
        [Permission.OwnershipOffer] = new(OwnerOnly, FullOnly, "ownership.offer"),
        // Teklifin hedefi herhangi bir roldeki üye olabilir; kabul, hedefin kendisi olduğu ayrıca doğrulanarak yapılır.
        [Permission.OwnershipAccept] = new(All, FullOnly, "ownership.accept"),
        [Permission.BillingRead] = new(OwnerAdmin, Readable, "billing.read"),
        [Permission.BillingManage] = new(OwnerOnly, FullOrRecovery, "billing.manage"),
        [Permission.AuditRead] = new(OwnerAdmin, Readable, "audit.read"),
        [Permission.ExportManage] = new(OwnerOnly, Readable, "export.manage"),
    };

    public static string Code(Permission permission) => Rules[permission].Code;

    public static bool RoleAllows(Permission permission, TenantRole role) => Rules[permission].Roles.Contains(role);

    public static bool ModeAllows(Permission permission, AccessMode mode) => Rules[permission].Modes.Contains(mode);

    public static bool IsAllowed(Permission permission, TenantRole role, AccessMode mode) =>
        RoleAllows(permission, role) && ModeAllows(permission, mode);

    /// <summary>Şirket görünür değilse (temizleniyor/silinmiş) null döner: çağıran 404 üretir.</summary>
    public static AccessMode? ComputeMode(TenantStatus tenant, SubscriptionStatus subscription) => tenant switch
    {
        TenantStatus.Purging or TenantStatus.Deleted => null,
        TenantStatus.Suspended => AccessMode.Locked,
        TenantStatus.DeletionPending => AccessMode.ReadOnly,
        _ => subscription is SubscriptionStatus.Suspended or SubscriptionStatus.Cancelled ? AccessMode.Recovery : AccessMode.Full,
    };

    public static IReadOnlyList<string> GrantedCodes(TenantRole role, AccessMode mode) =>
        Rules.Where(r => IsAllowed(r.Key, role, mode)).Select(r => r.Value.Code).Order(StringComparer.Ordinal).ToList();
}
