using System.Net;
using System.Text;
using Kulvar.Platform.Data;
using Kulvar.Platform.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kulvar.Modules.Billing.Provider;

/// <summary>
/// Simüle sağlayıcının "dış dünya" tarafı: kuyruğundaki olayları imzalı HTTP isteğiyle bizim webhook ucumuza teslim eder ve
/// operatörün istediği sağlayıcı olaylarını (yenileme başarısız/başarılı) üretir. Yalnız <c>Billing:Simulation:Enabled</c> iken çalışır.
/// Teslimat gerçek uçtan geçer: imza, tekrar, sıra ve eşleşme kuralları simülasyonda da aynen uygulanır.
/// </summary>
public sealed class ProviderSimulator(WorkerDb db, IOptions<BillingOptions> options, TimeProvider clock, ILogger<ProviderSimulator> log)
{
    private sealed record Pending(string Id, string Payload);

    /// <summary>Operatörün simülasyon isteklerini sağlayıcı olayına çevirir (sağlayıcının abonelik sırası ilerler).</summary>
    public Task<int> ProduceRequestedEventsAsync(CancellationToken ct) =>
        db.RunAsync(DbScope.None, async (session, c) =>
        {
            var requests = await session.Db.QueryAsync(
                "SELECT id, customer_reference, kind FROM fakepsp.simulation_requests WHERE processed_at IS NULL ORDER BY created_at LIMIT 20 FOR UPDATE SKIP LOCKED",
                r => (Id: r.GetGuid(0), Customer: r.GetString(1), Kind: r.GetString(2)), c);
            foreach (var request in requests)
            {
                var subscription = await session.Db.QueryAsync(
                    "UPDATE fakepsp.subscriptions SET sequence = sequence + 1 WHERE customer_reference = $1 RETURNING id, sequence, plan_code",
                    r => (Id: r.GetString(0), Sequence: r.GetInt64(1), Plan: r.GetString(2)), c, request.Customer);
                if (subscription.Count == 1 && request.Kind is "renewal_failed" or "renewed")
                {
                    await FakeBillingProvider.EnqueueWebhookAsync(session, "subscription." + request.Kind, subscription[0].Id, subscription[0].Sequence, null, subscription[0].Plan, c);
                }
                await session.Db.ExecuteAsync("UPDATE fakepsp.simulation_requests SET processed_at = $2 WHERE id = $1", c, request.Id, session.Now);
            }
            return requests.Count;
        }, ct);

    /// <summary>Bekleyen olayları teslim eder. 2xx ve 409 (çelişki; yeniden göndermek sonucu değiştirmez) teslim edilmiş sayılır.</summary>
    public async Task<int> DeliverPendingAsync(HttpClient http, string webhookUrl, CancellationToken ct)
    {
        var pending = await db.RunAsync(DbScope.None, (session, c) => session.Db.QueryAsync(
            "SELECT id, payload FROM fakepsp.webhooks WHERE delivered_at IS NULL AND attempts < 10 ORDER BY created_at, id LIMIT 20",
            r => new Pending(r.GetString(0), r.GetString(1)), c), ct);

        var delivered = 0;
        foreach (var webhook in pending)
        {
            var body = Encoding.UTF8.GetBytes(webhook.Payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, WebhookSignature.Sign(options.Value.Webhook.Secret, clock.GetUtcNow().ToUnixTimeSeconds(), body));
            var ok = false;
            try
            {
                using var response = await http.SendAsync(request, ct);
                ok = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict;
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning("Simüle webhook teslim edilemedi: {ExceptionType}", ex.GetType().Name);
            }
            await db.RunAsync(DbScope.None, (session, c) => session.Db.ExecuteAsync(
                "UPDATE fakepsp.webhooks SET attempts = attempts + 1, delivered_at = CASE WHEN $2 THEN $3::timestamptz ELSE NULL END WHERE id = $1",
                c, webhook.Id, ok, session.Now), ct);
            delivered += ok ? 1 : 0;
        }
        return delivered;
    }

    public static string NewRequestSql => "INSERT INTO fakepsp.simulation_requests (id, customer_reference, kind, created_at) VALUES ($1, $2, $3, $4)";

    public static Guid NewRequestId() => Ids.New();
}
