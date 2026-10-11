using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Kulvar.Platform.Http;

/// <summary>
/// Keyset sayfalama imleci. Şirket, kullanıcı, liste türü ve filtre/sıralama özetiyle birlikte mühürlenir (Data Protection):
/// A şirketinden alınan imleç B'de, başka kullanıcıda veya başka filtrede 400 <c>invalid_cursor</c> verir.
/// İmleç doğrulansa bile sorgu her zaman şirket filtresi + RLS altındadır; imleç yetki üretmez.
/// </summary>
public sealed class CursorCodec(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Kulvar.Cursor.v1");

    private sealed record Payload(Guid T, Guid U, string L, string F, string[] V);

    public string Encode(TenantContext ctx, string list, string filterKey, params string[] values)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Payload(ctx.TenantId, ctx.UserId, list, filterKey, values));
        return Base64Url.EncodeToString(_protector.Protect(json));
    }

    public string[]? Decode(TenantContext ctx, string list, string filterKey, string? cursor, int expectedValues)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(Base64Url.DecodeFromChars(cursor)));
            if (payload is not null && payload.T == ctx.TenantId && payload.U == ctx.UserId && payload.L == list && payload.F == filterKey && payload.V.Length == expectedValues)
            {
                return payload.V;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException)
        {
        }
        throw Problems.BadRequest("invalid_cursor", "Sayfalama imleci geçersiz veya bu listeye ait değil.");
    }

    public static string Format(DateTimeOffset value) => value.UtcTicks.ToString(CultureInfo.InvariantCulture);
    public static DateTimeOffset ParseTime(string value) => new(long.Parse(value, CultureInfo.InvariantCulture), TimeSpan.Zero);
}

/// <summary>Liste sorgu parametreleri: yalnız izin listesindeki adlar ve değerler kabul edilir.</summary>
public static class QueryGuard
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    public static void AllowOnly(HttpContext http, params string[] allowed)
    {
        foreach (var key in http.Request.Query.Keys)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                throw Problems.Validation(key, "Bilinmeyen sorgu parametresi.");
            }
        }
    }

    public static int Limit(HttpContext http)
    {
        var raw = http.Request.Query["limit"].ToString();
        if (raw.Length == 0)
        {
            return DefaultLimit;
        }
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > MaxLimit)
        {
            throw Problems.Validation("limit", $"1 ile {MaxLimit} arasında olmalıdır.");
        }
        return limit;
    }

    public static string OneOf(HttpContext http, string name, string fallback, params string[] allowed)
    {
        var raw = http.Request.Query[name].ToString();
        if (raw.Length == 0)
        {
            return fallback;
        }
        return allowed.Contains(raw, StringComparer.Ordinal) ? raw : throw Problems.Validation(name, $"İzin verilen değerler: {string.Join(", ", allowed)}.");
    }

    public static TEnum? Enum<TEnum>(HttpContext http, string name) where TEnum : struct, System.Enum
    {
        var raw = http.Request.Query[name].ToString();
        if (raw.Length == 0)
        {
            return null;
        }
        return System.Enum.TryParse<TEnum>(raw, ignoreCase: false, out var value) && System.Enum.IsDefined(value) && !int.TryParse(raw, out _)
            ? value
            : throw Problems.Validation(name, $"İzin verilen değerler: {string.Join(", ", System.Enum.GetNames<TEnum>())}.");
    }

    public static string? Text(HttpContext http, string name, int maxLength)
    {
        var raw = http.Request.Query[name].ToString().Trim();
        if (raw.Length == 0)
        {
            return null;
        }
        return raw.Length <= maxLength ? raw : throw Problems.Validation(name, $"En çok {maxLength} karakter olabilir.");
    }

    public static Guid? Id(HttpContext http, string name)
    {
        var raw = http.Request.Query[name].ToString();
        if (raw.Length == 0)
        {
            return null;
        }
        return Guid.TryParse(raw, out var id) ? id : throw Problems.Validation(name, "Geçerli bir kimlik olmalıdır.");
    }
}
