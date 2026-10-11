using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Modules.Billing;

namespace Kulvar.IntegrationTests.Stage6;

public sealed class WebhookTests(TestEnvironment env)
{
    private readonly BillingHarness _billing = new(env);

    private Task<long> InboxCountAsync(string eventId) => env.AdminScalarAsync<long>("SELECT count(*) FROM app.provider_events WHERE provider_event_id = $1", eventId);
    private Task<string?> InboxStatusAsync(string eventId) => env.AdminScalarAsync<string>("SELECT status FROM app.provider_events WHERE provider_event_id = $1", eventId);

    [Fact]
    public async Task InvalidSignature_OrStaleTimestamp_401_NoWrite()
    {
        var (_, tenant) = await _billing.TenantOnPlanAsync("team");
        var subscription = await _billing.ProviderSubscriptionAsync(tenant);
        var sequence = await _billing.ProviderSequenceAsync(tenant);
        var eventId = BillingHarness.NewEventId();
        var json = BillingHarness.EventJson("subscription.renewal_failed", subscription, sequence + 1, id: eventId);
        var validForOtherBody = WebhookSignature.Sign(KulvarApp.WebhookSecret, env.Clock.GetUtcNow().ToUnixTimeSeconds(), "{\"id\":\"baska\"}"u8);

        var attempts = new Dictionary<string, Task<HttpResponseMessage>>
        {
            ["yanlış sır"] = _billing.SendWebhookAsync(json, secret: "baska-bir-sir-0123456789abcdef0123456789"),
            ["imza yok"] = _billing.SendWebhookAsync(json, omitSignature: true),
            ["başka gövdenin imzası"] = _billing.SendWebhookAsync(json, signatureOverride: validForOtherBody),
            ["bozuk başlık"] = _billing.SendWebhookAsync(json, signatureOverride: "v1=deadbeef"),
            ["eski zaman damgası"] = _billing.SendWebhookAsync(json, timestampOffset: TimeSpan.FromMinutes(-6)),
            ["ileri zaman damgası"] = _billing.SendWebhookAsync(json, timestampOffset: TimeSpan.FromMinutes(6)),
        };
        foreach (var (name, attempt) in attempts)
        {
            var response = await attempt;
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{name}: {(int)response.StatusCode}");
            Assert.Contains("invalid_signature", await response.Content.ReadAsStringAsync());
        }

        // Hiçbir şey yazılmadı, hiçbir şey değişmedi.
        Assert.Equal(0L, await InboxCountAsync(eventId));
        await _billing.ProcessInboxAsync();
        Assert.Equal("Active:team", await _billing.SubscriptionAsync(tenant));

        // Aynı gövde doğru imzayla (tolerans içinde) kabul edilir.
        await _billing.SendWebhookAsync(json, timestampOffset: TimeSpan.FromMinutes(-4)).ShouldBeAsync(HttpStatusCode.Accepted);
        Assert.Equal(1L, await InboxCountAsync(eventId));
    }

    [Fact]
    public async Task OversizedBody_And_MalformedEvent_Rejected()
    {
        var oversized = BillingHarness.EventJson("subscription.renewed", "psub_x", 1, extra: new { padding = new string('x', 70 * 1024) });
        await _billing.SendWebhookAsync(oversized).ShouldBeProblemAsync(HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        await _billing.SendWebhookAsync("bu json değil").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid_event");
        await _billing.SendWebhookAsync("{\"type\":\"subscription.renewed\"}").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid_event");
    }

    [Fact]
    public async Task DuplicateEvent_SingleEffect()
    {
        var (owner, tenant) = await _billing.TenantOnPlanAsync("team");
        var json = BillingHarness.EventJson("subscription.renewal_failed", await _billing.ProviderSubscriptionAsync(tenant), await _billing.ProviderSequenceAsync(tenant) + 1);

        await _billing.SendWebhookAsync(json).ShouldBeAsync(HttpStatusCode.Accepted);
        var duplicate = await _billing.SendWebhookAsync(json).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("duplicate", duplicate["status"].Str());
        await _billing.ProcessInboxAsync();
        // İşlendikten sonra gelen kopya da etkisizdir.
        await _billing.SendWebhookAsync(json).ShouldBeAsync(HttpStatusCode.OK);
        await _billing.ProcessInboxAsync();

        Assert.Equal("PastDue:team", await _billing.SubscriptionAsync(tenant));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'subscription.status_changed'", tenant));
        await env.App.DrainOutboxAsync();
        var mails = (await owner.Client.GetAsync("/api/me/mailbox").ShouldBeAsync(HttpStatusCode.OK))["items"]!.AsArray().Count(m => m!["kind"].Str() == "subscription_changed");
        Assert.Equal(2, mails); // plan uygulandı + yenileme başarısız (kopya için ikinci ileti yok)
        Assert.Equal("Full", (await _billing.WorkspaceAsync(owner.Client, tenant))["accessMode"].Str()); // ödemesiz dönem: erişim sürer
    }

    [Fact]
    public async Task SameId_DifferentPayload_409_Conflict()
    {
        var (_, tenant) = await _billing.TenantOnPlanAsync("team");
        var subscription = await _billing.ProviderSubscriptionAsync(tenant);
        var sequence = await _billing.ProviderSequenceAsync(tenant);
        var eventId = BillingHarness.NewEventId();

        await _billing.SendWebhookAsync(BillingHarness.EventJson("subscription.renewal_failed", subscription, sequence + 1, id: eventId)).ShouldBeAsync(HttpStatusCode.Accepted);
        // Aynı olay kimliği, bu sefer "yenilendi" diyor: geçerli imzalı olsa da ilk kayıt korunur.
        await _billing.SendWebhookAsync(BillingHarness.EventJson("subscription.renewed", subscription, sequence + 2, id: eventId))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "event_payload_conflict");

        await _billing.ProcessInboxAsync();
        Assert.Equal("PastDue:team", await _billing.SubscriptionAsync(tenant));
        Assert.Equal("renewal_failed", await env.AdminScalarAsync<string>("SELECT replace(payload->>'type', 'subscription.', '') FROM app.provider_events WHERE provider_event_id = $1", eventId));
        // Çelişki operatöre görünür.
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.platform_audit_events WHERE action = 'webhook.payload_conflict' AND data->>'eventId' = $1", eventId));
    }

    [Fact]
    public async Task UnknownMapping_NoEffect_OperatorVisible()
    {
        var eventId = BillingHarness.NewEventId();
        await _billing.SendWebhookAsync(BillingHarness.EventJson("subscription.renewed", "psub_boyle_bir_abonelik_yok", 5, id: eventId)).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();
        Assert.Equal("UnknownMapping", await InboxStatusAsync(eventId));
        Assert.Null(await env.AdminScalarAsync<string>("SELECT tenant_id::text FROM app.provider_events WHERE provider_event_id = $1", eventId));

        // Eşleşme sonradan gelebilir diye sınırlı sayıda yeniden denenir; sonra denenmez ama operatöre görünür kalır.
        for (var i = 0; i < 6; i++)
        {
            env.Clock.Advance(TimeSpan.FromMinutes(5));
            await _billing.ProcessInboxAsync();
        }
        Assert.Equal("UnknownMapping", await InboxStatusAsync(eventId));
        Assert.Equal(5, await env.AdminScalarAsync<int>("SELECT attempts FROM app.provider_events WHERE provider_event_id = $1", eventId));
        Assert.Null(await env.AdminScalarAsync<string>("SELECT next_attempt_at::text FROM app.provider_events WHERE provider_event_id = $1", eventId));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.platform_audit_events WHERE action = 'webhook.unknown_mapping' AND data->>'eventId' = $1", eventId));

        // Bilinmeyen işlem referansı da aynı yoldan gider.
        var operationEvent = BillingHarness.NewEventId();
        await _billing.SendWebhookAsync(BillingHarness.EventJson("operation.succeeded", "psub_x", 1, "mref_boyle_bir_islem_yok", id: operationEvent)).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();
        Assert.Equal("UnknownMapping", await InboxStatusAsync(operationEvent));
    }

    [Fact]
    public async Task TenantIdInBody_DoesNotRoute()
    {
        var (_, tenantA) = await _billing.TenantOnPlanAsync("team");
        var (_, tenantB) = await _billing.TenantOnPlanAsync("starter");
        var subscriptionA = await _billing.ProviderSubscriptionAsync(tenantA);

        // Olay A'nın sağlayıcı aboneliğine ait; gövdeye B'nin kimliği sokulmuş. Yönlendirme yalnız bizim kayıtlarımızdaki eşleşmeden yapılır.
        var json = BillingHarness.EventJson("subscription.renewal_failed", subscriptionA, await _billing.ProviderSequenceAsync(tenantA) + 1, extra: new { tenantId = tenantB, tenant_id = tenantB });
        await _billing.SendWebhookAsync(json).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();
        Assert.Equal("PastDue:team", await _billing.SubscriptionAsync(tenantA));
        Assert.Equal("Active:starter", await _billing.SubscriptionAsync(tenantB));

        // Eşleşmesi olmayan olay, gövdesinde B yazıyor diye B'ye uygulanmaz.
        var orphan = BillingHarness.NewEventId();
        await _billing.SendWebhookAsync(BillingHarness.EventJson("subscription.renewal_failed", "psub_uydurma", 999, id: orphan, extra: new { tenantId = tenantB })).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();
        Assert.Equal("UnknownMapping", await InboxStatusAsync(orphan));
        Assert.Equal("Active:starter", await _billing.SubscriptionAsync(tenantB));
    }

    [Fact]
    public async Task OutOfOrder_OlderSequence_DoesNotRollBack()
    {
        // 1) Olaylar sırayla gelir; ardından eski bir olayın geç kopyası (yeni kimlikle) gelir.
        var (_, tenant) = await _billing.TenantOnPlanAsync("team");
        var subscription = await _billing.ProviderSubscriptionAsync(tenant);
        var sequence = await _billing.ProviderSequenceAsync(tenant);

        await _billing.SendWebhookAsync(BillingHarness.EventJson("subscription.renewal_failed", subscription, sequence + 1)).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();
        Assert.Equal("PastDue:team", await _billing.SubscriptionAsync(tenant));
        await _billing.SendWebhookAsync(BillingHarness.EventJson("subscription.renewed", subscription, sequence + 2)).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();
        Assert.Equal("Active:team", await _billing.SubscriptionAsync(tenant));

        var late = BillingHarness.NewEventId();
        await _billing.SendWebhookAsync(BillingHarness.EventJson("subscription.renewal_failed", subscription, sequence + 1, id: late)).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();
        Assert.Equal("Stale", await InboxStatusAsync(late));
        Assert.Equal("Active:team", await _billing.SubscriptionAsync(tenant)); // Geç gelen eski olay durumu geri almadı.
        Assert.Equal(sequence + 2, await _billing.ProviderSequenceAsync(tenant));

        // 2) Olaylar ters sırayla gelir: önce "yenilendi" (N+2), sonra "başarısız" (N+1). Alınma zamanı değil sağlayıcı sırası belirler.
        var (_, tenant2) = await _billing.TenantOnPlanAsync("starter");
        var subscription2 = await _billing.ProviderSubscriptionAsync(tenant2);
        var sequence2 = await _billing.ProviderSequenceAsync(tenant2);
        var failed = BillingHarness.NewEventId();
        await _billing.SendWebhookAsync(BillingHarness.EventJson("subscription.renewed", subscription2, sequence2 + 2)).ShouldBeAsync(HttpStatusCode.Accepted);
        env.Clock.Advance(TimeSpan.FromSeconds(5));
        await _billing.SendWebhookAsync(BillingHarness.EventJson("subscription.renewal_failed", subscription2, sequence2 + 1, id: failed)).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();
        Assert.Equal("Stale", await InboxStatusAsync(failed));
        Assert.Equal("Active:starter", await _billing.SubscriptionAsync(tenant2));
    }

    [Fact]
    public async Task Renewed_AfterCancel_DoesNotReactivate_AndRecoveryFromPastDueWorks()
    {
        var (owner, tenant) = await _billing.TenantOnPlanAsync("team");
        var subscription = await _billing.ProviderSubscriptionAsync(tenant);
        var sequence = await _billing.ProviderSequenceAsync(tenant);

        // PastDue → ödeme alındı → Active (kurtarma).
        await _billing.SendWebhookAsync(BillingHarness.EventJson("subscription.renewal_failed", subscription, sequence + 1)).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.SendWebhookAsync(BillingHarness.EventJson("subscription.renewed", subscription, sequence + 2)).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();
        Assert.Equal("Active:team", await _billing.SubscriptionAsync(tenant));

        // İptalden sonra gelen "yenilendi" olayı (daha yeni sırayla bile) aboneliği geri açmaz.
        await owner.Client.PostAsync($"/api/t/{tenant}/billing/cancel").ShouldBeAsync(HttpStatusCode.NoContent);
        await owner.Client.PostAsync($"/api/t/{tenant}/billing/cancel").ShouldBeProblemAsync(HttpStatusCode.Conflict, "already_cancelled");
        var renewed = BillingHarness.NewEventId();
        await _billing.SendWebhookAsync(BillingHarness.EventJson("subscription.renewed", subscription, sequence + 3, id: renewed)).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();
        Assert.Equal("Ignored", await InboxStatusAsync(renewed));
        Assert.Equal("Cancelled:team", await _billing.SubscriptionAsync(tenant));
        Assert.Equal("Recovery", (await _billing.WorkspaceAsync(owner.Client, tenant))["accessMode"].Str());
    }

    [Fact]
    public async Task Webhook_IgnoresCookiesAndCsrf_AndIsNotATenantEndpoint()
    {
        // Oturum cookie'si olan bir kullanıcının isteği de imzasız kabul edilmez: bu uç kimliğini yalnız imzadan alır.
        var user = await env.World.CreateUserAsync();
        var json = BillingHarness.EventJson("subscription.renewed", "psub_x", 1);
        await user.Client.PostAsync("/api/webhooks/billing", json).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "invalid_signature");
        // İmzalı istek CSRF token'ı olmadan da kabul edilir (cookie kullanılmadığı için CSRF yüzeyi yoktur).
        await _billing.SendWebhookAsync(json).ShouldBeAsync(HttpStatusCode.Accepted);
    }
}
