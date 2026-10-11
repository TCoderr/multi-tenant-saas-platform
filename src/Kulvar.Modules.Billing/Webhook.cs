using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kulvar.Modules.Billing.Domain;
using Kulvar.Modules.Billing.Provider;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Kulvar.Platform.Http;
using Kulvar.Platform.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kulvar.Modules.Billing;

public sealed class BillingOptions
{
    public WebhookOptions Webhook { get; set; } = new();
    public SimulationOptions Simulation { get; set; } = new();

    public sealed class WebhookOptions
    {
        /// <summary>HMAC sırrı (en az 32 karakter). Boşsa tüm webhook'lar reddedilir.</summary>
        public string Secret { get; set; } = string.Empty;
        public int ToleranceSeconds { get; set; } = 300;
        public int MaxBodyBytes { get; set; } = 64 * 1024;
    }

    public sealed class SimulationOptions
    {
        public bool Enabled { get; set; }
        public bool AllowOutcomeSelection { get; set; }
        public string? WebhookUrl { get; set; }
    }
}

/// <summary>
/// Webhook imzası: <c>Kulvar-Signature: t=&lt;unix saniye&gt;,v1=&lt;hex(HMAC-SHA256(secret, "&lt;t&gt;." + ham gövde))&gt;</c>.
/// İmza ham baytlar üzerinde hesaplanır (yeniden serileştirilmiş JSON üzerinde değil) ve sabit zamanlı karşılaştırılır.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "Kulvar-Signature";

    public static string Sign(string secret, long unixSeconds, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes(unixSeconds.ToString(CultureInfo.InvariantCulture) + ".");
        var data = new byte[prefix.Length + body.Length];
        prefix.CopyTo(data, 0);
        body.CopyTo(data.AsSpan(prefix.Length));
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);
        return $"t={unixSeconds.ToString(CultureInfo.InvariantCulture)},v1={Convert.ToHexStringLower(mac)}";
    }

    public static bool Verify(string secret, string? header, ReadOnlySpan<byte> body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (secret.Length < 32 || string.IsNullOrEmpty(header) || header.Length > 200)
        {
            return false;
        }
        long? timestamp = null;
        string? signature = null;
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                return false;
            }
            switch (pair[0])
            {
                case "t" when long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t):
                    timestamp = t;
                    break;
                case "v1":
                    signature = pair[1];
                    break;
            }
        }
        if (timestamp is null || signature is null || signature.Length != 64)
        {
            return false;
        }
        // Tekrar oynatma sınırı: zaman damgası tolerans penceresinin dışındaysa imza doğru olsa da reddedilir.
        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp.Value) > (long)tolerance.TotalSeconds)
        {
            return false;
        }
        var expected = Sign(secret, timestamp.Value, body);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes($"t={timestamp.Value.ToString(CultureInfo.InvariantCulture)},v1={signature.ToLowerInvariant()}"));
    }
}

internal static class WebhookEndpoint
{
    public const string RateLimitPolicy = BillingModule.WebhookRateLimitPolicy;

    /// <summary>
    /// Webhook alımı: imza doğrulanır, olay inbox'a yazılır, 2xx döner. Etki burada uygulanmaz (worker uygular); bu uç
    /// <c>kulvar_platform</c> rolüyle çalışır ve abonelik/işlem tablolarına yazamaz. Cookie/CSRF kullanmaz.
    /// </summary>
    public static async Task<IResult> Receive(HttpContext http, PlatformDb db, IOptions<BillingOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var webhook = options.Value.Webhook;
        if (http.Request.ContentLength > webhook.MaxBodyBytes)
        {
            throw new ProblemException(413, "payload_too_large", "Gövde çok büyük.");
        }
        // Ham baytlar: imza bunlar üzerinde doğrulanır. Sınırı aşan gövde okunmadan kesilir.
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > webhook.MaxBodyBytes)
            {
                throw new ProblemException(413, "payload_too_large", "Gövde çok büyük.");
            }
            buffer.Write(chunk, 0, read);
        }
        var body = buffer.ToArray();

        if (!WebhookSignature.Verify(webhook.Secret, http.Request.Headers[WebhookSignature.HeaderName].ToString(), body, clock.GetUtcNow(), TimeSpan.FromSeconds(webhook.ToleranceSeconds)))
        {
            Telemetry.WebhookEvents.Add(1, new KeyValuePair<string, object?>("outcome", "invalid_signature"));
            throw new ProblemException(401, "invalid_signature", "İmza doğrulanamadı."); // Hiçbir şey yazılmadı.
        }

        WebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WebhookPayload>(body, JsonDefaults.Storage);
        }
        catch (JsonException)
        {
            payload = null;
        }
        if (payload is null || string.IsNullOrWhiteSpace(payload.Id) || payload.Id.Length > 80 || string.IsNullOrWhiteSpace(payload.Type) || payload.Type.Length > 60)
        {
            throw Problems.BadRequest("invalid_event", "Olay gövdesi okunamadı.");
        }

        var hash = SHA256.HashData(body);
        var text = Encoding.UTF8.GetString(body);
        var outcome = await db.RunAsync(DbScope.None, async (session, c) =>
        {
            var inserted = await session.Db.ExecuteAsync(
                """
                INSERT INTO app.provider_events (provider_event_id, payload_hash, payload, received_at, status, attempts, next_attempt_at)
                VALUES ($1, $2, $3::jsonb, $4, 'Received', 0, $4)
                ON CONFLICT (provider_event_id) DO NOTHING
                """,
                c, payload.Id, hash, text, session.Now);
            if (inserted == 1)
            {
                return "accepted";
            }
            var existing = await session.Db.Set<ProviderEvent>().AsNoTracking().Where(e => e.ProviderEventId == payload.Id).Select(e => e.PayloadHash).SingleAsync(c);
            if (CryptographicOperations.FixedTimeEquals(existing, hash))
            {
                return "duplicate";
            }
            // Aynı olay kimliği, farklı gövde: ilk kayıt korunur; çelişki operatöre görünür biçimde kaydedilir.
            session.Db.Add(PlatformAuditEvent.Create(null, "webhook.payload_conflict", null, new { eventId = payload.Id, type = payload.Type }, session.Now));
            await session.Db.SaveChangesAsync(c);
            return "conflict";
        }, ct);

        Telemetry.WebhookEvents.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        return outcome switch
        {
            "accepted" => Results.Json(new { status = "accepted" }, statusCode: 202),
            "duplicate" => Results.Ok(new { status = "duplicate" }),
            _ => throw Problems.Conflict("event_payload_conflict", "Bu olay kimliği farklı bir gövdeyle daha önce alınmış."),
        };
    }
}
