using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Kulvar.Modules.Billing;
using Kulvar.Modules.Customers;
using Kulvar.Modules.Identity;
using Kulvar.Modules.Operations;
using Kulvar.Modules.Support;
using Kulvar.Modules.Tenancy;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Kulvar.Api.Hosting;

public sealed class RateLimitOptions
{
    public int LoginPerMinute { get; set; } = 20;
    public int SignupPerMinute { get; set; } = 5;
    public int WebhookPerMinute { get; set; } = 120;
    public int ApiPerMinute { get; set; } = 600;
}

public static class ApiHost
{
    public const string CsrfHeader = "X-CSRF-TOKEN";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddSecretFiles();
        StartupGuards.Enforce(builder.Configuration, builder.Environment);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 1 * 1024 * 1024;
        });

        var services = builder.Services;
        services.AddKulvarCore(builder.Configuration);
        services.AddTenancyApi();
        services.AddOperationsApi();
        services.AddSingleton<CursorCodec>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        services.AddProblemDetails();
        services.AddExceptionHandler<ProblemExceptionHandler>();
        services.ConfigureHttpJsonOptions(json =>
        {
            // Bilinmeyen alan (ör. gövdede tenantId veya role) sessizce yok sayılmaz: 400.
            json.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            // Varsayılan (gevşetilmemiş) kodlayıcı: < > & ' karakterleri JSON içinde \uXXXX olarak kaçırılır. Yanıtlar zaten
            // application/json + nosniff'tir; bu, yanıtın yanlışlıkla HTML olarak yorumlanmasına karşı ek katmandır.
            json.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default;
        });
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

        var keysPath = builder.Configuration["DataProtection:KeysPath"];
        var dataProtection = services.AddDataProtection().SetApplicationName("Kulvar");
        if (!string.IsNullOrWhiteSpace(keysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        services.AddIdentityCookieAuthentication();
        services.AddAuthorizationBuilder()
            // Varsayılan kapalı: açıkça AllowAnonymous denmemiş her uç oturum ister.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddAntiforgery(o =>
        {
            o.HeaderName = CsrfHeader;
            o.Cookie.Name = "__Host-kulvar-csrf";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.Path = "/";
            o.SuppressXFrameOptionsHeader = true; // Güvenlik başlıkları tek yerden yazılır.
        });

        // Limitler istek anında okunur (kayıt anında değil): yapılandırma kaynakları host kurulurken tamamlanır.
        services.Configure<RateLimitOptions>(builder.Configuration.GetSection("RateLimits"));
        static RateLimitOptions Limits(HttpContext http) => http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, _) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromSeconds(60);
                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await Problems.WriteAsync(context.HttpContext, 429, "rate_limited", "Çok fazla istek. Biraz sonra yeniden deneyin.");
            };
            o.AddPolicy(IdentityModule.RateLimitPolicyLogin, http => PerIp(http, "login", Limits(http).LoginPerMinute));
            o.AddPolicy(IdentityModule.RateLimitPolicySignup, http => PerIp(http, "signup", Limits(http).SignupPerMinute));
            o.AddPolicy(Kulvar.Modules.Billing.BillingModule.WebhookRateLimitPolicy, http => PerIp(http, "webhook", Limits(http).WebhookPerMinute));
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                http.Request.Path.StartsWithSegments("/api") ? PerIp(http, "api", Limits(http).ApiPerMinute) : RateLimitPartition.GetNoLimiter("static"));
        });

        var app = builder.Build();

        app.Use(RequestLog.Write);
        app.UseExceptionHandler();
        app.Use(SecurityHeaders.Apply);
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.Use(ValidateCsrf);
        app.UseAuthorization();

        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
        app.MapGet("/health/ready", async (AppDb db, CancellationToken ct) =>
        {
            await db.RunAsync(DbScope.None, (session, c) => session.Db.ScalarAsync("SELECT 1", c), ct);
            return Results.Ok(new { status = "ready" });
        }).AllowAnonymous();

        app.MapIdentityEndpoints();
        app.MapTenancyEndpoints();
        app.MapCustomersEndpoints();
        app.MapSupportEndpoints();
        app.MapBillingEndpoints();
        app.MapOperationsEndpoints();

        // Bilinmeyen API yolu SPA'ya düşmez.
        app.MapFallback("/api/{**path}", (HttpContext http) => Problems.WriteAsync(http, 404, "not_found", "Uç bulunamadı.")).AllowAnonymous();
        if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html")))
        {
            app.MapFallbackToFile("index.html").AllowAnonymous();
        }
        return app;
    }

    private static RateLimitPartition<string> PerIp(HttpContext http, string bucket, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter($"{bucket}:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = perMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    /// <summary>
    /// Güvensiz yöntemli tüm /api istekleri CSRF token'ı ister. Tek istisna webhook'tur: cookie kullanmaz, HMAC imzalıdır.
    /// Token kimliğe bağlı olduğundan login/logout sonrası yeniden alınmalıdır.
    /// </summary>
    private static async Task ValidateCsrf(HttpContext http, RequestDelegate next)
    {
        var method = http.Request.Method;
        var unsafeMethod = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));
        if (unsafeMethod && http.Request.Path.StartsWithSegments("/api") && !http.Request.Path.StartsWithSegments("/api/webhooks"))
        {
            await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
        }
        await next(http);
    }
}

/// <summary>
/// API istek logu. Yalnız şunlar yazılır: yöntem, eşleşen uç ŞABLONU (ham yol/sorgu değil), durum kodu, süre ve kimlikler (kullanıcı, şirket).
/// Sorgu dizesi (arama terimi, indirme token'ı), gövde, e-posta, ad gibi kişisel veri veya sır loglanmaz.
/// </summary>
internal static class RequestLog
{
    public static async Task Write(HttpContext http, RequestDelegate next)
    {
        if (!http.Request.Path.StartsWithSegments("/api"))
        {
            await next(http);
            return;
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await next(http);
        }
        finally
        {
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Kulvar.Request");
            // Hata işleyici devreye girdiyse uç ve rota değerleri istekten silinmiştir; özgün değerler özellikte durur.
            var failure = http.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
            var route = ((http.GetEndpoint() ?? failure?.Endpoint) as RouteEndpoint)?.RoutePattern.RawText ?? "(eşleşmedi)";
            var routeValues = failure?.RouteValues ?? http.Request.RouteValues;
            var tenantId = routeValues.TryGetValue("tenantId", out var raw) && Guid.TryParse(raw?.ToString(), out var parsed) ? parsed : (Guid?)null;
            var userId = http.User.Identity?.IsAuthenticated == true ? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value : null;
            logger.LogInformation("{Method} {Route} {StatusCode} {ElapsedMs} ms user={UserId} tenant={TenantId}",
                http.Request.Method, route, http.Response.StatusCode, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, userId, tenantId);
        }
    }
}

internal static class SecurityHeaders
{
    private const string Csp = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
                               "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    public static Task Apply(HttpContext http, RequestDelegate next)
    {
        var headers = http.Response.Headers;
        headers.ContentSecurityPolicy = Csp;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
        if (http.Request.Path.StartsWithSegments("/api"))
        {
            headers.CacheControl = "no-store";
        }
        return next(http);
    }
}
