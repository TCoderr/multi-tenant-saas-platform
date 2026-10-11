using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Kulvar.Platform.Security;

public static class Ids
{
    /// <summary>Zaman sıralı UUIDv7. Kimlikler gizli değildir; yetki hiçbir yerde tahmin edilemezliğe dayanmaz.</summary>
    public static Guid New() => Guid.CreateVersion7();
}

/// <summary>Gizli token'lar: CSPRNG çıktısı; veritabanında yalnız SHA-256 hash'i saklanır.</summary>
public static class Tokens
{
    public static string NewToken(int bytes = 32) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static string HashHex(string token) => Convert.ToHexStringLower(Hash(token));

    /// <summary>Token biçimi makul mü (uzunluk/karakter)? Veritabanına gitmeden önce ucuz ret.</summary>
    public static bool LooksValid(string? token) =>
        token is { Length: >= 20 and <= 200 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public static class EmailNormalizer
{
    /// <summary>
    /// Trim → NFKC → kültürden bağımsız küçültme. Büyük/küçük harf farkı aynı adrestir; artı/nokta katlaması yapılmaz.
    /// Kültüre bağlı küçültme kullanılmaz (tr-TR'de "I" → "ı" olur ve aynı adres iki farklı anahtara dönüşür).
    /// </summary>
    public static string Normalize(string email) => email.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    public static bool IsPlausible(string? email)
    {
        if (email is null)
        {
            return false;
        }
        var trimmed = email.Trim();
        if (trimmed.Length is < 3 or > 254 || trimmed.Any(char.IsWhiteSpace) || trimmed.Any(char.IsControl))
        {
            return false;
        }
        var at = trimmed.IndexOf('@');
        return at > 0 && at == trimmed.LastIndexOf('@') && at < trimmed.Length - 1 && trimmed.IndexOf('.', at) > at + 1 && !trimmed.EndsWith('.');
    }

    /// <summary>Maskeli gösterim: Viewer rolü ve loglar için. <c>a***@example.test</c></summary>
    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }
}
