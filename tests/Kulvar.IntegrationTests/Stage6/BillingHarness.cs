using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Modules.Billing;
using Kulvar.Modules.Billing.Provider;
using Microsoft.Extensions.DependencyInjection;

namespace Kulvar.IntegrationTests.Stage6;

/// <summary>
/// Faturalama testlerinin yardımcıları. Arka plan döngüleri yoktur: her adım (outbox, inbox, mutabakat, sağlayıcı teslimatı)
/// test tarafından açıkça ve sırayla tetiklenir; sıralama deterministiktir.
/// </summary>
public sealed class BillingHarness(TestEnvironment env)
{
    private static int _eventSequence;

    public ProviderInboxProcessor Inbox => env.App.Services.GetRequiredService<ProviderInboxProcessor>();
    public BillingReconciler Reconciler => env.App.Services.GetRequiredService<BillingReconciler>();
    public ProviderSimulator Simulator => env.App.Services.GetRequiredService<ProviderSimulator>();

    public async Task<JsonObject> RequestAsync(ApiClient owner, Guid tenant, string planCode, string? outcome = null, string? key = null) =>
        await owner.PostAsync($"/api/t/{tenant}/billing/plan-change", new { planCode, simulatedOutcome = outcome }, ("Idempotency-Key", key ?? Guid.NewGuid().ToString("N")))
            .ShouldBeAsync(HttpStatusCode.Accepted);

    public async Task<JsonObject> OperationAsync(ApiClient owner, Guid tenant, Guid operationId) =>
        await owner.GetAsync($"/api/t/{tenant}/billing/operations/{operationId}").ShouldBeAsync(HttpStatusCode.OK);

    public async Task<JsonObject> WorkspaceAsync(ApiClient client, Guid tenant) =>
        await client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);

    /// <summary>Outbox'ı boşaltır: plan değişikliği sağlayıcıya gönderilir, bildirim iletileri üretilir.</summary>
    public Task SubmitAsync() => env.App.DrainOutboxAsync();

    public async Task ProcessInboxAsync()
    {
        for (var round = 0; round < 20 && await Inbox.RunOnceAsync(CancellationToken.None) > 0; round++)
        {
        }
    }

    public Task ReconcileAsync() => Reconciler.RunOnceAsync(CancellationToken.None);

    /// <summary>Simüle sağlayıcının kuyruğundaki olayları gerçek webhook ucuna (imzalı) teslim eder.</summary>
    public Task<int> DeliverProviderWebhooksAsync() =>
        Simulator.DeliverPendingAsync(env.App.CreateDefaultClient(new Uri("https://localhost")), "/api/webhooks/billing", CancellationToken.None);

    public Task<string?> OperationStatusAsync(Guid operationId) => env.AdminScalarAsync<string>("SELECT status FROM app.billing_operations WHERE id = $1", operationId);
    public Task<string?> MerchantReferenceAsync(Guid operationId) => env.AdminScalarAsync<string>("SELECT merchant_reference FROM app.billing_operations WHERE id = $1", operationId);
    public Task<string?> ProviderSubscriptionAsync(Guid tenant) => env.AdminScalarAsync<string>("SELECT provider_subscription_id FROM app.subscriptions WHERE tenant_id = $1", tenant);
    public Task<long> ProviderSequenceAsync(Guid tenant) => env.AdminScalarAsync<long>("SELECT provider_sequence FROM app.subscriptions WHERE tenant_id = $1", tenant);
    public Task<string?> SubscriptionAsync(Guid tenant) => env.AdminScalarAsync<string>(
        "SELECT s.status || ':' || v.plan_code FROM app.subscriptions s JOIN app.plan_versions v ON v.id = s.plan_version_id WHERE s.tenant_id = $1", tenant);
    public Task<int> ChargeCountAsync(Guid operationId) => env.AdminScalarAsync<int>(
        "SELECT coalesce(sum(f.charge_count), 0)::int FROM fakepsp.operations f JOIN app.billing_operations o ON o.merchant_reference = f.merchant_reference WHERE o.id = $1", operationId);

    public static string NewEventId() => $"evt_test_{Interlocked.Increment(ref _eventSequence)}_{Guid.NewGuid():N}";

    public static string EventJson(string type, string? subscriptionId = null, long sequence = 0, string? merchantReference = null, string? id = null, object? extra = null)
    {
        var node = new JsonObject
        {
            ["id"] = id ?? NewEventId(),
            ["type"] = type,
            ["subscriptionId"] = subscriptionId,
            ["sequence"] = sequence,
            ["merchantReference"] = merchantReference,
            ["occurredAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        if (extra is not null)
        {
            foreach (var (key, value) in JsonSerializer.SerializeToNode(extra)!.AsObject().ToList())
            {
                node[key] = value?.DeepClone();
            }
        }
        return node.ToJsonString();
    }

    /// <summary>İmzalı webhook isteği. Varsayılan: doğru sır ve şimdiki (sahte saat) zaman damgası.</summary>
    public async Task<HttpResponseMessage> SendWebhookAsync(string json, string? secret = null, TimeSpan? timestampOffset = null, string? signatureOverride = null, bool omitSignature = false)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var timestamp = (env.Clock.GetUtcNow() + (timestampOffset ?? TimeSpan.Zero)).ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/billing") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (!omitSignature)
        {
            request.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, signatureOverride ?? WebhookSignature.Sign(secret ?? KulvarApp.WebhookSecret, timestamp, body));
        }
        // Webhook cookie ve CSRF kullanmaz: çıplak istemci.
        return await env.App.CreateDefaultClient(new Uri("https://localhost")).SendAsync(request);
    }

    /// <summary>Sahibi, şirketi olan ve gerçek akışla (istek → sağlayıcı → uygulama) verilen plana geçmiş şirket.</summary>
    public async Task<(TestUser Owner, Guid Tenant)> TenantOnPlanAsync(string planCode)
    {
        var owner = await env.World.CreateUserAsync("Plan Sahibi");
        var tenant = await env.World.CreateTenantAsync(owner);
        await RequestAsync(owner.Client, tenant, planCode);
        await SubmitAsync();
        Assert.Equal($"Active:{planCode}", await SubscriptionAsync(tenant));
        return (owner, tenant);
    }
}
