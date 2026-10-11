using Kulvar.Platform.Security;

namespace Kulvar.Modules.Identity.Domain;

/// <summary>Global kimlik. Rol taşımaz; roller şirket üyeliğindedir.</summary>
public sealed class User
{
    private User() { }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = null!;
    public string NormalizedEmail { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public DateTimeOffset? EmailVerifiedAt { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockoutUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsEmailVerified => EmailVerifiedAt is not null;

    public static User Create(Guid id, string email, string displayName, DateTimeOffset now) => new()
    {
        Id = id,
        Email = email.Trim(),
        NormalizedEmail = EmailNormalizer.Normalize(email),
        DisplayName = displayName,
        PasswordHash = string.Empty,
        CreatedAt = now,
    };

    public void SetPasswordHash(string hash) => PasswordHash = hash;

    public void MarkEmailVerified(DateTimeOffset now) => EmailVerifiedAt ??= now;

    public bool IsLockedOut(DateTimeOffset now) => LockoutUntil is { } until && until > now;

    public void ResetLoginFailures()
    {
        FailedLoginCount = 0;
        LockoutUntil = null;
    }
}

/// <summary>Sunucu tarafı oturum. Cookie yalnız bu satırın kimliğini taşır; iptal satırda yapılır.</summary>
public sealed class Session
{
    private Session() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static Session Start(Guid userId, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Ids.New(),
        UserId = userId,
        CreatedAt = now,
        ExpiresAt = now + lifetime,
    };

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}

public static class UserTokenPurpose
{
    public const string EmailVerification = "email_verify";
}

/// <summary>Tek kullanımlık kullanıcı token'ı (e-posta doğrulama). Yalnız hash saklanır.</summary>
public sealed class UserToken
{
    private UserToken() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Purpose { get; private set; } = null!;
    public byte[] TokenHash { get; private set; } = null!;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static UserToken Create(Guid userId, string purpose, byte[] tokenHash, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Ids.New(),
        UserId = userId,
        Purpose = purpose,
        TokenHash = tokenHash,
        ExpiresAt = now + lifetime,
        CreatedAt = now,
    };

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && ExpiresAt > now;

    public void Consume(DateTimeOffset now) => ConsumedAt = now;
}

/// <summary>Platform operatörü işareti. Yalnız migration rolüyle (kurulum komutu) eklenir; uygulama rolü yalnız okuyabilir.</summary>
public sealed class PlatformOperator
{
    private PlatformOperator() { }

    public Guid UserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
