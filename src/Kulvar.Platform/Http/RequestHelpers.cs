using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Kulvar.Platform.Http;

/// <summary>İsteğin doğrulanmış kullanıcısı. Şirket/rol bilgisi taşımaz; onlar her istekte veritabanından okunur.</summary>
public interface ICurrentUser
{
    Guid? UserIdOrNull { get; }
    Guid? SessionIdOrNull { get; }
    Guid UserId { get; }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string SessionClaim = "sid";

    public Guid? UserIdOrNull => Read(ClaimTypes.NameIdentifier);
    public Guid? SessionIdOrNull => Read(SessionClaim);
    public Guid UserId => UserIdOrNull ?? throw Problems.Unauthenticated();

    private Guid? Read(string claimType)
    {
        var user = accessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }
        return Guid.TryParse(user.FindFirstValue(claimType), out var id) ? id : null;
    }
}

public static class ETags
{
    public static string Format(int version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    public static void Set(HttpContext http, int version) => http.Response.Headers.ETag = Format(version);

    /// <summary>If-Match zorunludur: eksikse 428, biçimi bozuksa 400. Karşılaştırmayı çağıran yapar.</summary>
    public static int RequireIfMatch(HttpContext http)
    {
        var raw = http.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw Problems.PreconditionRequired();
        }
        var trimmed = raw.Trim().Trim('"');
        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw Problems.BadRequest("invalid_if_match", "If-Match değeri geçersiz.");
        }
        return version;
    }

    public static void EnsureMatch(int expected, int actual)
    {
        if (expected != actual)
        {
            throw Problems.PreconditionFailed();
        }
    }
}

/// <summary>Küçük, açık doğrulama yardımcısı: hataları alan adına göre toplar.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public string Required(string field, string? value, int maxLength, int minLength = 1)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length < minLength)
        {
            Add(field, minLength <= 1 ? "Bu alan zorunludur." : $"En az {minLength} karakter olmalıdır.");
        }
        else if (trimmed.Length > maxLength)
        {
            Add(field, $"En çok {maxLength} karakter olabilir.");
        }
        return trimmed;
    }

    public string? Optional(string field, string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        if (trimmed.Length > maxLength)
        {
            Add(field, $"En çok {maxLength} karakter olabilir.");
        }
        return trimmed;
    }

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            _errors[field] = list = [];
        }
        list.Add(message);
    }

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
        {
            throw Problems.Validation(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }
    }
}
