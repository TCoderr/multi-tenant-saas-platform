using Kulvar.Platform.Data;
using Kulvar.Platform.Tenancy;

namespace Kulvar.Modules.Tenancy.Domain;

/// <summary>Şirket kaydı. İzolasyon birimi; içerik tabloları buna <c>tenant_id</c> ile bağlanır.</summary>
public sealed class Tenant
{
    private Tenant() { }

    public const string DeletedName = "(silindi)";

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public TenantStatus Status { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string? SuspendedReason { get; private set; }
    public DateTimeOffset? DeletionRequestedAt { get; private set; }
    public DateTimeOffset? PurgeAfter { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public int Version { get; private set; }

    public static Tenant Create(Guid id, string name, Guid createdByUserId, DateTimeOffset now) => new()
    {
        Id = id,
        Name = name,
        Status = TenantStatus.Active,
        CreatedByUserId = createdByUserId,
        CreatedAt = now,
        Version = 1,
    };

    public void Rename(string name)
    {
        Name = name;
        Version++;
    }

    /// <summary>Platform operatörü askıya alır: içerik kilitlenir (Locked). Gerekçe zorunludur.</summary>
    public void Suspend(string reason)
    {
        if (Status != TenantStatus.Active)
        {
            throw new InvalidOperationException("Yalnız aktif şirket askıya alınabilir.");
        }
        Status = TenantStatus.Suspended;
        SuspendedReason = reason;
        Version++;
    }

    public void Reactivate()
    {
        if (Status != TenantStatus.Suspended)
        {
            throw new InvalidOperationException("Şirket askıda değil.");
        }
        Status = TenantStatus.Active;
        SuspendedReason = null;
        Version++;
    }

    public void RequestDeletion(DateTimeOffset now, TimeSpan grace)
    {
        if (Status != TenantStatus.Active)
        {
            throw new InvalidOperationException("Yalnız aktif şirket için silme istenebilir.");
        }
        Status = TenantStatus.DeletionPending;
        DeletionRequestedAt = now;
        PurgeAfter = now + grace;
        Version++;
    }

    public void CancelDeletion()
    {
        if (Status != TenantStatus.DeletionPending)
        {
            throw new InvalidOperationException("Bekleyen silme isteği yok.");
        }
        Status = TenantStatus.Active;
        DeletionRequestedAt = null;
        PurgeAfter = null;
        Version++;
    }
}

public enum MembershipStatus { Active, Removed }

/// <summary>
/// (şirket, kullanıcı, rol). Satır fiziksel olarak silinmez: yorum/atama/yazar ilişkileri bileşik FK ile buna bağlıdır.
/// Fiziksel silme yalnız şirket temizlemesinde yapılır.
/// </summary>
public sealed class Membership : ITenantOwned
{
    private Membership() { }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public TenantRole Role { get; private set; }
    public MembershipStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RemovedAt { get; private set; }
    public int Version { get; private set; }

    public bool IsActive => Status == MembershipStatus.Active;

    public static Membership Create(Guid tenantId, Guid userId, TenantRole role, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        Role = role,
        Status = MembershipStatus.Active,
        CreatedAt = now,
        Version = 1,
    };

    public void ChangeRole(TenantRole role)
    {
        Role = role;
        Version++;
    }

    public void Remove(DateTimeOffset now)
    {
        Status = MembershipStatus.Removed;
        RemovedAt = now;
        Version++;
    }

    /// <summary>Daha önce kaldırılmış üyenin yeniden katılması (yeni davet kabulü).</summary>
    public void Reactivate(TenantRole role, DateTimeOffset now)
    {
        Status = MembershipStatus.Active;
        Role = role;
        RemovedAt = null;
        CreatedAt = now;
        Version++;
    }
}
