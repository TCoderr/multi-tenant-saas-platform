using System.Security.Claims;
using Kulvar.Modules.Identity.Domain;
using Kulvar.Modules.Identity.Features;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Messaging;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kulvar.Modules.Identity;

public sealed class AuthOptions
{
    public int MinPasswordLength { get; set; } = 12;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int SessionHours { get; set; } = 12;
    public int VerificationTokenHours { get; set; } = 24;
}

public static class IdentityModule
{
    public const string SessionCookieName = "__Host-kulvar-session";
    public const string RateLimitPolicyLogin = "auth-login";
    public const string RateLimitPolicySignup = "auth-signup";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuthOptions>(configuration.GetSection("Auth"));
        // Yalnız test hızını ayarlamak için; verilmezse çerçeve varsayılanı (PBKDF2-HMAC-SHA512, 100.000 tur) geçerlidir.
        services.AddOptions<PasswordHasherOptions>().Configure<IConfiguration>((options, config) =>
        {
            if (config.GetValue<int?>("Auth:PasswordIterations") is { } iterations)
            {
                options.IterationCount = iterations;
            }
        });
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton<IOutboxHandler, EmailVerificationHandler>();
        return services;
    }

    /// <summary>Yalnız API süreci. Cookie kimlik doğrulaması: yönlendirme yok (401/403 ProblemDetails), her istekte oturum satırı doğrulanır.</summary>
    public static IServiceCollection AddIdentityCookieAuthentication(this IServiceCollection services)
    {
        services.AddScoped<SessionValidator>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = SessionCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.Path = "/";
                options.Cookie.IsEssential = true;
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context =>
                    Problems.WriteAsync(context.HttpContext, 401, "unauthenticated", "Oturum gerekli.");
                options.Events.OnRedirectToAccessDenied = context =>
                    Problems.WriteAsync(context.HttpContext, 403, "forbidden", "Bu işlem için yetkiniz yok.");
                options.Events.OnValidatePrincipal = context =>
                    context.HttpContext.RequestServices.GetRequiredService<SessionValidator>().ValidateAsync(context);
            });
        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        AuthEndpoints.Map(endpoints);
        MailboxEndpoints.Map(endpoints);
        return endpoints;
    }
}

/// <summary>
/// Cookie bütünlük korumalıdır ama yetki kaynağı değildir: her istekte oturum satırı okunur.
/// İptal edilmiş veya süresi dolmuş oturumun cookie'si (kopyalanmış olsa da) reddedilir.
/// </summary>
public sealed class SessionValidator(AppDb db)
{
    public async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (!Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            !Guid.TryParse(principal?.FindFirstValue(HttpCurrentUser.SessionClaim), out var sessionId))
        {
            context.RejectPrincipal();
            return;
        }

        var active = await db.RunAsync(DbScope.ForUser(userId), (session, ct) =>
            session.Db.Set<Session>().AnyAsync(
                s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > session.Now, ct),
            context.HttpContext.RequestAborted);

        if (!active)
        {
            context.RejectPrincipal();
        }
    }
}
