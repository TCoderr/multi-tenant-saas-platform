using System.Security.Cryptography;
using System.Text.Json;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kulvar.Platform.Http;

/// <summary>
/// Idempotent işlem kaydı. Anahtar (şirket, kullanıcı, işlem, anahtar) dörtlüsüdür: aynı anahtar başka şirkette veya başka kullanıcıda bağımsızdır.
/// Kayıt, etkisiyle aynı transaction'da yazılır; rollback ikisini birlikte geri alır.
/// </summary>
public sealed class IdempotencyKey : ITenantOwned
{
    private IdempotencyKey() { }

    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string Operation { get; private set; } = null!;
    public string Key { get; private set; } = null!;
    public byte[] RequestHash { get; private set; } = null!;
    public int ResponseStatus { get; private set; }
    public string ResponseBody { get; private set; } = "{}";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
}

internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> b)
    {
        b.ToTable("idempotency_keys");
        b.HasKey(x => new { x.TenantId, x.UserId, x.Operation, x.Key });
        b.Property(x => x.Operation).HasMaxLength(60);
        b.Property(x => x.Key).HasMaxLength(100);
        // Metin olarak saklanır (jsonb değil): tekrar oynatılan yanıt ilk yanıtla bayt bayt aynı olmalıdır; jsonb anahtar sırasını ve boşlukları değiştirir.
        b.Property(x => x.ResponseBody).HasColumnType("text");
        b.HasIndex(x => x.ExpiresAt);
    }
}

public sealed record IdempotentResult(int Status, string Json, bool Replayed)
{
    public IResult ToHttpResult(HttpContext http)
    {
        if (Replayed)
        {
            http.Response.Headers["Idempotent-Replay"] = "true";
        }
        return Results.Content(Json, "application/json; charset=utf-8", System.Text.Encoding.UTF8, Status);
    }

    /// <summary>Kaydedilmiş gövdeden tamsayı alan okur (ör. ETag için <c>version</c>).</summary>
    public int? ReadInt(string property)
    {
        using var document = JsonDocument.Parse(Json);
        return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    }
}

public static class Idempotency
{
    public const string HeaderName = "Idempotency-Key";
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public static string RequireKey(HttpContext http)
    {
        var key = http.Request.Headers[HeaderName].ToString().Trim();
        if (key.Length is < 1 or > 100 || key.Any(c => c is < '!' or > '~'))
        {
            throw Problems.BadRequest("idempotency_key_required", $"Bu işlem 1–100 karakterlik {HeaderName} başlığı ister.");
        }
        return key;
    }

    public static byte[] HashRequest<T>(T request) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, JsonDefaults.Storage));

    /// <summary>
    /// İşi en çok bir kez uygular. Çağrı, yetki/üyelik doğrulandıktan sonra ve şirket transaction'ı içinde yapılır:
    /// üyeliği kaldırılmış kullanıcı eski yanıtı tekrar oynatamaz. Aynı anahtar + aynı gövde → kayıtlı yanıt; farklı gövde → 409.
    /// </summary>
    public static async Task<IdempotentResult> ExecuteAsync<TBody>(TenantContext ctx, string operation, string key, byte[] requestHash, Func<Task<(int Status, TBody Body)>> work, CancellationToken ct)
    {
        // Yer tutucu: eşzamanlı aynı anahtar bu satırda bekler (ON CONFLICT, diğer transaction bitene kadar). Kazanan işi yapar.
        var claimed = await ctx.Db.ExecuteAsync(
            """
            INSERT INTO app.idempotency_keys (tenant_id, user_id, operation, key, request_hash, response_status, response_body, created_at, expires_at)
            VALUES ($1, $2, $3, $4, $5, 0, '{}', $6, $7)
            ON CONFLICT (tenant_id, user_id, operation, key) DO NOTHING
            """,
            ct, ctx.TenantId, ctx.UserId, operation, key, requestHash, ctx.Now, ctx.Now + Retention);

        if (claimed == 0)
        {
            var existing = await ctx.Db.Set<IdempotencyKey>().AsNoTracking()
                .SingleAsync(k => k.UserId == ctx.UserId && k.Operation == operation && k.Key == key, ct);
            if (!CryptographicOperations.FixedTimeEquals(existing.RequestHash, requestHash))
            {
                throw Problems.Conflict("idempotency_key_reused", "Bu Idempotency-Key başka bir istek gövdesiyle kullanılmış.");
            }
            return new IdempotentResult(existing.ResponseStatus, existing.ResponseBody, Replayed: true);
        }

        var (status, body) = await work();
        var json = JsonSerializer.Serialize(body, JsonDefaults.Storage);
        await ctx.Db.ExecuteAsync(
            "UPDATE app.idempotency_keys SET response_status = $5, response_body = $6 WHERE tenant_id = $1 AND user_id = $2 AND operation = $3 AND key = $4",
            ct, ctx.TenantId, ctx.UserId, operation, key, status, json);
        return new IdempotentResult(status, json, Replayed: false);
    }
}
