using Kulvar.Platform.Data;
using Kulvar.Platform.Security;
using Kulvar.Platform.Tenancy;

namespace Kulvar.Modules.Tenancy.Domain;

public enum InviteStatus { Pending, Accepted, Revoked }

/// <summary>Arayüze dönen türetilmiş durum. "Expired" ve "Sending" saklanmaz; süre ve token varlığından hesaplanır.</summary>
public enum InviteState { Sending, Pending, Expired, Accepted, Revoked }

/// <summary>
/// Şirket daveti. Ham token veritabanında tutulmaz: worker posta üretirken token'ı oluşturur ve buraya yalnız SHA-256 hash'ini yazar.
/// Rol davet satırındadır; kabul isteği rol taşımaz.
/// </summary>
public sealed class Invite : ITenantOwned
{
    private Invite() { }

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Email { get; private set; } = null!;
    public string NormalizedEmail { get; private set; } = null!;
    public TenantRole Role { get; private set; }
    public InviteStatus Status { get; private set; }
    public byte[]? TokenHash { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public Guid InvitedByUserId { get; private set; }
    public Guid? AcceptedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static Invite Create(Guid tenantId, string email, TenantRole role, Guid invitedByUserId, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        TenantId = tenantId,
        Email = email.Trim(),
        NormalizedEmail = EmailNormalizer.Normalize(email),
        Role = role,
        Status = InviteStatus.Pending,
        InvitedByUserId = invitedByUserId,
        CreatedAt = now,
        ExpiresAt = now + Lifetime,
    };

    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;

    /// <summary>Koltuk ayıran davet: bekleyen ve süresi dolmamış.</summary>
    public bool HoldsSeat(DateTimeOffset now) => Status == InviteStatus.Pending && !IsExpired(now);

    public InviteState StateAt(DateTimeOffset now) => Status switch
    {
        InviteStatus.Accepted => InviteState.Accepted,
        InviteStatus.Revoked => InviteState.Revoked,
        _ when IsExpired(now) => InviteState.Expired,
        _ when TokenHash is null => InviteState.Sending,
        _ => InviteState.Pending,
    };

    public void MarkSent(byte[] tokenHash, DateTimeOffset now)
    {
        TokenHash = tokenHash;
        SentAt = now;
    }

    public void Accept(Guid userId, DateTimeOffset now)
    {
        Status = InviteStatus.Accepted;
        AcceptedByUserId = userId;
        AcceptedAt = now;
    }

    public void Revoke(DateTimeOffset now)
    {
        Status = InviteStatus.Revoked;
        RevokedAt = now;
    }
}

public enum OwnershipOfferStatus { Pending, Accepted, Cancelled, Declined }

/// <summary>
/// Sahiplik teklifi: Owner rolü yalnız hedef üyenin açık kabulüyle verilir. <see cref="DemoteSelf"/> ise ("devret") kabul anında
/// teklif eden aynı transaction'da Admin olur; değilse iki Owner olur.
/// </summary>
public sealed class OwnershipOffer : ITenantOwned
{
    private OwnershipOffer() { }

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid FromUserId { get; private set; }
    public Guid ToUserId { get; private set; }
    public bool DemoteSelf { get; private set; }
    public OwnershipOfferStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    public static OwnershipOffer Create(Guid tenantId, Guid fromUserId, Guid toUserId, bool demoteSelf, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        TenantId = tenantId,
        FromUserId = fromUserId,
        ToUserId = toUserId,
        DemoteSelf = demoteSelf,
        Status = OwnershipOfferStatus.Pending,
        CreatedAt = now,
        ExpiresAt = now + Lifetime,
    };

    public bool IsOpen(DateTimeOffset now) => Status == OwnershipOfferStatus.Pending && ExpiresAt > now;

    public void Resolve(OwnershipOfferStatus status, DateTimeOffset now)
    {
        Status = status;
        ResolvedAt = now;
    }
}
