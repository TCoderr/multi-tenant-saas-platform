using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Messaging;

namespace Kulvar.Platform.Tenancy;

/// <summary>İsteğin hangi satırları kilitleyerek başlayacağı (kilit sırası: şirket → aktör üyeliği → abonelik → sayaçlar).</summary>
public enum TenantLock
{
    /// <summary>Okuma: kilit yok.</summary>
    None,

    /// <summary>İçerik yazma: aktörün üyelik satırı FOR SHARE. Eşzamanlı rol/kaldırma işlemi bu isteğin bitmesini bekler.</summary>
    ActorShared,

    /// <summary>Üyelik/yaşam döngüsü değişikliği: şirket satırı FOR UPDATE. Şirket içindeki bu tür işlemler seri çalışır.</summary>
    Governance,
}

/// <summary>Üyeliği ve erişim modu doğrulanmış, şirket kapsamına girmiş iş birimi.</summary>
public sealed class TenantContext
{
    public required DbSession Session { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid UserId { get; init; }
    public required TenantRole Role { get; init; }
    public required TenantStatus TenantStatus { get; init; }
    public required string TenantName { get; init; }
    public required int TenantVersion { get; init; }
    public required Entitlements Entitlements { get; init; }
    public required AccessMode Mode { get; init; }

    public AppDbContext Db => Session.Db;
    public DateTimeOffset Now => Session.Now;

    public bool Can(Permission permission) => AccessRules.IsAllowed(permission, Role, Mode);

    public void Require(Permission permission)
    {
        if (!AccessRules.RoleAllows(permission, Role))
        {
            throw Problems.Forbidden(AccessRules.Code(permission));
        }
        if (!AccessRules.ModeAllows(permission, Mode))
        {
            throw new ProblemException(403, "access_mode_restricted", "Şirketin durumu bu işleme izin vermiyor.", new Dictionary<string, object?>
            {
                ["accessMode"] = Mode.ToString(),
                ["tenantStatus"] = TenantStatus.ToString(),
                ["subscriptionStatus"] = Entitlements.SubscriptionStatus.ToString(),
                ["requiredPermission"] = AccessRules.Code(permission),
            });
        }
    }

    /// <summary>Denetim kaydı ekler (aynı transaction). <paramref name="data"/> kişisel veri/sır içermemelidir.</summary>
    public void Audit(string action, string targetType, Guid? targetId = null, Guid? ticketId = null, object? data = null) =>
        Db.Add(AuditEvent.Create(TenantId, UserId, action, targetType, targetId, ticketId, data, Now));

    /// <summary>Outbox mesajı ekler (aynı transaction). Gövde yalnız kimlik taşır.</summary>
    public void Enqueue(string type, object payload) =>
        Db.Add(OutboxMessage.Create(TenantId, null, type, payload, Now));
}

public interface ITenantExecutor
{
    /// <summary>
    /// Güncel üyeliği doğrular, şirket kapsamına girer, izni ve erişim modunu denetler ve işi tek transaction'da çalıştırır.
    /// Üye değilse veya şirket yoksa 404 <c>tenant_not_found</c>.
    /// </summary>
    Task<T> RunAsync<T>(Guid tenantId, Permission permission, TenantLock tenantLock, Func<TenantContext, CancellationToken, Task<T>> work, CancellationToken ct);
}

/// <summary>Şirketin entitlement'ını veritabanından okur. Billing modülü uygular.</summary>
public interface IEntitlementReader
{
    Task<Entitlements> ReadAsync(DbSession session, Guid tenantId, CancellationToken ct);

    /// <summary>Gösterim amaçlı kullanım özeti. Kota kararı bununla verilmez; karar tüketim deyiminin kendisindedir.</summary>
    Task<UsageSnapshot> ReadUsageAsync(DbSession session, Guid tenantId, CancellationToken ct);
}

public sealed record UsageSnapshot(int TicketsThisPeriod, DateTimeOffset PeriodResetsAt);

public static class UsageMetrics
{
    public const string TicketsCreated = "tickets_created";
}

/// <summary>
/// Kota kapısı: tüketim, onu tetikleyen iş etkisiyle aynı transaction'da yapılır. Kota doluysa 403 <c>quota_exceeded</c> fırlatır.
/// Billing modülü uygular; kayıtlı kapı yoksa tüketim yapılmaz.
/// </summary>
public interface IUsageGate
{
    Task ConsumeAsync(TenantContext context, string metric, Guid sourceId, CancellationToken ct);
}

/// <summary>Şirket oluşturulurken aynı transaction'da çalışan modül adımı (ör. deneme aboneliği, talep sayacı).</summary>
public interface ITenantProvisioningStep
{
    Task OnTenantCreatedAsync(DbSession session, Guid tenantId, CancellationToken ct);
}

public sealed class LifecycleOptions
{
    /// <summary>Silme isteği ile kalıcı temizleme arasındaki iptal penceresi (gün). Demo varsayımıdır; hukuki saklama süresi iddiası değildir.</summary>
    public int DeletionGraceDays { get; set; } = 7;
}

/// <summary>Şirket silme isteğinde aynı transaction'da çalışan modül adımı (ör. bekleyen ödeme işlemlerini hükümsüz kılma).</summary>
public interface ITenantDeletionStep
{
    Task OnDeletionRequestedAsync(TenantContext context, CancellationToken ct);
}

/// <summary>Üyelik kaldırılırken aynı transaction'da çalışan modül adımı (ör. talep atamalarını boşaltma).</summary>
public interface IMemberRemovalStep
{
    Task OnMemberRemovedAsync(TenantContext context, Guid removedUserId, CancellationToken ct);
}
