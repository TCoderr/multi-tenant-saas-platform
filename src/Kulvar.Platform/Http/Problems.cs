using System.Diagnostics;
using Kulvar.Platform.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kulvar.Platform.Http;

/// <summary>İş kuralı veya yetki reddi. Transaction'ı geri aldırır ve ProblemDetails'e çevrilir.</summary>
public sealed class ProblemException(int status, string code, string title, IReadOnlyDictionary<string, object?>? extensions = null)
    : Exception($"{status} {code}")
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public IReadOnlyDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();
}

public static class Problems
{
    public const string TypeBase = "https://kulvar.example/problems/";

    public static ProblemException Unauthenticated() => new(401, "unauthenticated", "Oturum gerekli.");
    public static ProblemException InvalidCredentials() => new(401, "invalid_credentials", "E-posta veya parola hatalı.");
    public static ProblemException TenantNotFound() => new(404, "tenant_not_found", "Şirket bulunamadı.");
    public static ProblemException NotFound() => new(404, "not_found", "Kayıt bulunamadı.");
    public static ProblemException Forbidden(string requiredPermission) =>
        new(403, "forbidden", "Bu işlem için yetkiniz yok.", new Dictionary<string, object?> { ["requiredPermission"] = requiredPermission });
    public static ProblemException Conflict(string code, string title, IReadOnlyDictionary<string, object?>? ext = null) => new(409, code, title, ext);
    public static ProblemException BadRequest(string code, string title) => new(400, code, title);
    public static ProblemException PreconditionRequired() => new(428, "precondition_required", "Bu işlem If-Match başlığı ister.");
    public static ProblemException PreconditionFailed() => new(412, "precondition_failed", "Kayıt siz düzenlerken değişti.");

    public static ProblemException Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(400, "validation_failed", "Girdi doğrulanamadı.", new Dictionary<string, object?> { ["errors"] = errors });

    public static ProblemException Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static ProblemDetails ToDetails(HttpContext http, int status, string code, string title, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var details = new ProblemDetails { Type = TypeBase + code, Title = title, Status = status };
        details.Extensions["code"] = code;
        details.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;
        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                details.Extensions[key] = value;
            }
        }
        return details;
    }

    public static Task WriteAsync(HttpContext http, int status, string code, string title, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(ToDetails(http, status, code, title, extensions), options: null, contentType: "application/problem+json");
    }
}

/// <summary>Tüm işlenmemiş hataları ProblemDetails'e çevirir. Yanıta iç ayrıntı veya kişisel veri yazılmaz.</summary>
public sealed class ProblemExceptionHandler(ILogger<ProblemExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        switch (exception)
        {
            case ProblemException problem:
                await Problems.WriteAsync(http, problem.Status, problem.Code, problem.Title, problem.Extensions);
                return true;
            case AntiforgeryValidationException:
                await Problems.WriteAsync(http, 400, "csrf_invalid", "CSRF doğrulaması başarısız.");
                return true;
            case BadHttpRequestException bad:
                await Problems.WriteAsync(http, bad.StatusCode == 413 ? 413 : 400, bad.StatusCode == 413 ? "payload_too_large" : "bad_request", "İstek okunamadı.");
                return true;
            case DbUpdateConcurrencyException:
                await Problems.WriteAsync(http, 412, "precondition_failed", "Kayıt siz düzenlerken değişti.");
                return true;
            case OperationCanceledException when http.RequestAborted.IsCancellationRequested:
                http.Response.StatusCode = 499;
                return true;
        }

        if (PostgresErrors.IsUnavailable(exception))
        {
            log.LogError("Veritabanı kullanılamıyor: {ExceptionType}", exception.GetType().Name);
            await Problems.WriteAsync(http, 503, "dependency_unavailable", "Hizmet geçici olarak kullanılamıyor.");
            return true;
        }

        // İstisna mesajı kullanıcı girdisi içerebileceği için loglanmaz: yalnız tür, sqlstate ve yığın izi (yalnız yöntem/satır; veri taşımaz).
        log.LogError("İşlenmemiş hata: {ExceptionType} sqlstate={SqlState} trace={TraceId}\n{StackTrace}",
            exception.GetType().FullName, PostgresErrors.SqlState(exception), System.Diagnostics.Activity.Current?.Id ?? http.TraceIdentifier, exception.StackTrace);
        await Problems.WriteAsync(http, 500, "internal_error", "Beklenmeyen bir hata oluştu.");
        return true;
    }
}
