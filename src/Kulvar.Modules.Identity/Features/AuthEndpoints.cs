using System.Security.Claims;
using Kulvar.Modules.Identity.Domain;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Messaging;
using Kulvar.Platform.Observability;
using Kulvar.Platform.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kulvar.Modules.Identity.Features;

public sealed record SignupRequest(string? Email, string? DisplayName, string? Password);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record VerifyEmailRequest(string? Token);
public sealed record UserResponse(Guid Id, string Email, string DisplayName, bool EmailVerified, bool IsPlatformOperator);

public static class IdentityOutbox
{
    public const string EmailVerificationRequested = "identity.email_verification_requested";

    public sealed record EmailVerificationPayload(Guid UserId);
}

internal static class AuthEndpoints
{
    // Bilinmeyen e-posta için de parola hash'i doğrulanır: yanıt süresi hesabın varlığını belli etmez.
    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<User>().HashPassword(null!, "kulvar-dummy-password"));

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth");
        auth.MapGet("/csrf", GetCsrf).AllowAnonymous();
        auth.MapPost("/signup", Signup).AllowAnonymous().RequireRateLimiting(IdentityModule.RateLimitPolicySignup);
        auth.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(IdentityModule.RateLimitPolicyLogin);
        auth.MapPost("/logout", Logout);
        auth.MapPost("/verify-email", VerifyEmail);
        auth.MapPost("/verify-email/resend", ResendVerification);
        endpoints.MapGet("/api/me", Me);
    }

    private static IResult GetCsrf(HttpContext http, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { token = tokens.RequestToken, headerName = tokens.HeaderName });
    }

    private static async Task<IResult> Signup(SignupRequest request, AppDb db, IPasswordHasher<User> hasher, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var validator = new Validator();
        var displayName = validator.Required("displayName", request.DisplayName, 100);
        if (!EmailNormalizer.IsPlausible(request.Email))
        {
            validator.Add("email", "Geçerli bir e-posta adresi girin.");
        }
        var password = request.Password ?? string.Empty;
        if (password.Length < options.Value.MinPasswordLength || password.Length > 200)
        {
            validator.Add("password", $"Parola en az {options.Value.MinPasswordLength} karakter olmalıdır.");
        }
        validator.ThrowIfInvalid();

        var email = request.Email!.Trim();
        var normalized = EmailNormalizer.Normalize(email);
        var passwordHash = hasher.HashPassword(null!, password); // Her durumda hesaplanır (zamanlama eşitliği).

        try
        {
            await db.RunAsync(new DbScope(LoginEmail: normalized), async (session, c) =>
            {
                if (await session.Db.Set<User>().AnyAsync(u => u.NormalizedEmail == normalized, c))
                {
                    return; // Yanıt aynıdır; mevcut hesap değişmez.
                }
                var user = User.Create(Ids.New(), email, displayName, session.Now);
                user.SetPasswordHash(passwordHash);
                await session.EnterUserAsync(user.Id, c);
                session.Db.Add(user);
                session.Db.Add(OutboxMessage.Create(null, user.Id, IdentityOutbox.EmailVerificationRequested, new IdentityOutbox.EmailVerificationPayload(user.Id), session.Now));
                await session.Db.SaveChangesAsync(c);
            }, ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            // Eşzamanlı aynı e-posta: biri kazandı. Yanıt yine aynıdır.
        }

        return Results.Json(new { status = "accepted", message = "Kayıt alındıysa giriş yapabilirsiniz. Doğrulama bağlantısı demo posta kutunuza gönderildi." }, statusCode: 202);
    }

    private sealed record LoginOutcome(User User, Session Session, bool IsOperator);

    private static async Task<IResult> Login(LoginRequest request, HttpContext http, AppDb db, IPasswordHasher<User> hasher, IOptions<AuthOptions> options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password) || request.Email.Length > 254 || request.Password.Length > 200)
        {
            throw Problems.InvalidCredentials();
        }
        var normalized = EmailNormalizer.Normalize(request.Email);
        var auth = options.Value;

        // Başarısız deneme sayacı da commit edilmelidir; bu yüzden ret istisnayla değil dönüş değeriyle taşınır.
        var outcome = await db.RunAsync(new DbScope(LoginEmail: normalized), async (session, c) =>
        {
            var user = await session.Db.Set<User>().SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, c);
            if (user is null)
            {
                hasher.VerifyHashedPassword(null!, DummyHash.Value, request.Password);
                return null;
            }
            if (user.IsLockedOut(session.Now))
            {
                hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
                return null; // Kilitliyken doğru parola da reddedilir; yanıt aynıdır.
            }
            if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
            {
                var lockUntil = session.Now.AddMinutes(auth.LockoutMinutes);
                await session.Db.Set<User>().Where(u => u.Id == user.Id).ExecuteUpdateAsync(set => set
                    .SetProperty(u => u.LockoutUntil, u => u.FailedLoginCount + 1 >= auth.MaxFailedLogins ? lockUntil : u.LockoutUntil)
                    .SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount + 1 >= auth.MaxFailedLogins ? 0 : u.FailedLoginCount + 1), c);
                return null;
            }

            await session.EnterUserAsync(user.Id, c);
            user.ResetLoginFailures();
            var newSession = Session.Start(user.Id, session.Now, TimeSpan.FromHours(auth.SessionHours));
            session.Db.Add(newSession);
            var isOperator = await session.Db.Set<PlatformOperator>().AnyAsync(o => o.UserId == user.Id, c);
            await session.Db.SaveChangesAsync(c);
            return new LoginOutcome(user, newSession, isOperator);
        }, ct);

        Telemetry.LoginAttempts.Add(1, new KeyValuePair<string, object?>("outcome", outcome is null ? "rejected" : "success"));
        if (outcome is null)
        {
            throw Problems.InvalidCredentials();
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, outcome.User.Id.ToString("D")),
            new Claim(HttpCurrentUser.SessionClaim, outcome.Session.Id.ToString("D")),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = false,
            IssuedUtc = outcome.Session.CreatedAt,
            ExpiresUtc = outcome.Session.ExpiresAt,
            AllowRefresh = false,
        });

        return Results.Ok(new { user = ToResponse(outcome.User, outcome.IsOperator) });
    }

    private static async Task<IResult> Logout(HttpContext http, ICurrentUser current, AppDb db, CancellationToken ct)
    {
        var userId = current.UserId;
        var sessionId = current.SessionIdOrNull;
        await db.RunAsync(DbScope.ForUser(userId), async (session, c) =>
        {
            await session.Db.Set<Session>()
                .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, session.Now), c);
        }, ct);
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> Me(ICurrentUser current, AppDb db, CancellationToken ct)
    {
        var userId = current.UserId;
        var response = await db.RunAsync(DbScope.ForUser(userId), async (session, c) =>
        {
            var user = await session.Db.Set<User>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, c) ?? throw Problems.Unauthenticated();
            var isOperator = await session.Db.Set<PlatformOperator>().AnyAsync(o => o.UserId == userId, c);
            return ToResponse(user, isOperator);
        }, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> VerifyEmail(VerifyEmailRequest request, ICurrentUser current, AppDb db, CancellationToken ct)
    {
        var userId = current.UserId;
        if (!Tokens.LooksValid(request.Token))
        {
            throw InvalidToken();
        }
        var hash = Tokens.Hash(request.Token!);
        await db.RunAsync(DbScope.ForUser(userId), async (session, c) =>
        {
            var token = await session.Db.Set<UserToken>().SingleOrDefaultAsync(
                t => t.TokenHash == hash && t.UserId == userId && t.Purpose == UserTokenPurpose.EmailVerification, c);
            if (token is null || !token.IsUsable(session.Now))
            {
                throw InvalidToken();
            }
            var user = await session.Db.Set<User>().SingleAsync(u => u.Id == userId, c);
            token.Consume(session.Now);
            user.MarkEmailVerified(session.Now);
            await session.Db.SaveChangesAsync(c);
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ResendVerification(ICurrentUser current, AppDb db, CancellationToken ct)
    {
        var userId = current.UserId;
        await db.RunAsync(DbScope.ForUser(userId), async (session, c) =>
        {
            var user = await session.Db.Set<User>().AsNoTracking().SingleAsync(u => u.Id == userId, c);
            var recentCutoff = session.Now.AddSeconds(-60);
            var recentlySent = await session.Db.Set<UserToken>().AnyAsync(t => t.UserId == userId && t.CreatedAt > recentCutoff, c);
            if (user.IsEmailVerified || recentlySent)
            {
                return; // Yanıt aynıdır; ileti seli üretilmez.
            }
            session.Db.Add(OutboxMessage.Create(null, userId, IdentityOutbox.EmailVerificationRequested, new IdentityOutbox.EmailVerificationPayload(userId), session.Now));
            await session.Db.SaveChangesAsync(c);
        }, ct);
        return Results.Json(new { status = "accepted" }, statusCode: 202);
    }

    private static ProblemException InvalidToken() => Problems.BadRequest("invalid_or_expired_token", "Bağlantı geçersiz veya süresi dolmuş.");

    private static UserResponse ToResponse(User user, bool isOperator) =>
        new(user.Id, user.Email, user.DisplayName, user.IsEmailVerified, isOperator);
}
