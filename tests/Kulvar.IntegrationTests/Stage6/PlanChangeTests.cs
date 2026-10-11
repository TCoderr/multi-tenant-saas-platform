using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Modules.Billing.Provider;
using Kulvar.Platform.Tenancy;

namespace Kulvar.IntegrationTests.Stage6;

public sealed class PlanChangeTests(TestEnvironment env)
{
    private readonly BillingHarness _billing = new(env);
    private World World => env.World;

    private async Task<(TestUser Owner, Guid Tenant)> NewTenantAsync()
    {
        var owner = await World.CreateUserAsync("Fatura Sahibi");
        return (owner, await World.CreateTenantAsync(owner));
    }

    private async Task<ApiClient> OperatorAsync()
    {
        var user = await World.CreateUserAsync("Test Operatörü");
        await using var owner = Npgsql.NpgsqlDataSource.Create(env.Postgres.Owner());
        Assert.True(await Kulvar.Api.Hosting.OperatorSetup.GrantAsync(owner, user.Email, env.Clock.GetUtcNow(), CancellationToken.None));
        return user.Client;
    }

    [Fact]
    public async Task Accepted_DoesNotChangeEntitlement_UntilApplied()
    {
        var (owner, tenant) = await NewTenantAsync();
        var accepted = await _billing.RequestAsync(owner.Client, tenant, "team");
        var operationId = accepted["operationId"].Guid();
        Assert.Equal("Pending", accepted["status"].Str());

        // 202 "kabul edildi": plan ve limitler henüz değişmedi.
        var before = await _billing.WorkspaceAsync(owner.Client, tenant);
        Assert.Equal("trial", before["subscription"]!["planCode"].Str());
        Assert.Equal(3, before["entitlements"]!["seatLimit"].Int());
        Assert.Equal(0, await _billing.ChargeCountAsync(operationId));

        await _billing.SubmitAsync();

        var after = await _billing.WorkspaceAsync(owner.Client, tenant);
        Assert.Equal("team", after["subscription"]!["planCode"].Str());
        Assert.Equal("Active", after["subscription"]!["status"].Str());
        Assert.Equal(25, after["entitlements"]!["seatLimit"].Int());
        var operation = await _billing.OperationAsync(owner.Client, tenant, operationId);
        Assert.Equal("Succeeded", operation["status"].Str());
        Assert.True(operation["terminal"]!.GetValue<bool>());
        Assert.Equal(149000L, operation["amountMinor"]!.GetValue<long>());
        Assert.Equal(1, await _billing.ChargeCountAsync(operationId));
        Assert.NotNull(await _billing.ProviderSubscriptionAsync(tenant));

        // Sağlayıcının aynı işlem için gönderdiği webhook etkisizdir (iş zaten eşzamanlı yanıtla uygulandı).
        Assert.True(await _billing.DeliverProviderWebhooksAsync() >= 1);
        await _billing.ProcessInboxAsync();
        Assert.Equal(1L, await env.AdminScalarAsync<long>(
            "SELECT count(*) FROM app.provider_events WHERE tenant_id = $1 AND status = 'Ignored' AND note = 'already_settled'", tenant));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'billing.plan_applied'", tenant));
        Assert.Equal("Active:team", await _billing.SubscriptionAsync(tenant));

        await env.App.DrainOutboxAsync();
        var mails = (await owner.Client.GetAsync("/api/me/mailbox").ShouldBeAsync(HttpStatusCode.OK))["items"]!.AsArray().Count(m => m!["kind"].Str() == "subscription_changed");
        Assert.Equal(1, mails);
    }

    [Fact]
    public async Task PlanChange_Rules_OwnerOnly_Idempotent_Validated()
    {
        var (owner, tenant) = await NewTenantAsync();
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin);

        await admin.Client.PostAsync($"/api/t/{tenant}/billing/plan-change", new { planCode = "team" }, ("Idempotency-Key", "a")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await owner.Client.PostAsync($"/api/t/{tenant}/billing/plan-change", new { planCode = "team" }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        foreach (var plan in new[] { "trial", "enterprise", "" })
        {
            await owner.Client.PostAsync($"/api/t/{tenant}/billing/plan-change", new { planCode = plan }, ("Idempotency-Key", Guid.NewGuid().ToString())).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        }
        await owner.Client.PostAsync($"/api/t/{tenant}/billing/plan-change", new { planCode = "team", simulatedOutcome = "free_money" }, ("Idempotency-Key", Guid.NewGuid().ToString())).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        // Fiyat ve şirket istemciden gelmez: bilinmeyen alanlar reddedilir.
        await owner.Client.PostAsync($"/api/t/{tenant}/billing/plan-change", new { planCode = "team", amountMinor = 1 }, ("Idempotency-Key", Guid.NewGuid().ToString())).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "bad_request");

        var first = await _billing.RequestAsync(owner.Client, tenant, "starter", key: "ayni-istek");
        var replay = await _billing.RequestAsync(owner.Client, tenant, "starter", key: "ayni-istek");
        Assert.Equal(first["operationId"].Str(), replay["operationId"].Str());
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.billing_operations WHERE tenant_id = $1", tenant));

        await _billing.SubmitAsync();
        await owner.Client.PostAsync($"/api/t/{tenant}/billing/plan-change", new { planCode = "starter" }, ("Idempotency-Key", Guid.NewGuid().ToString())).ShouldBeProblemAsync(HttpStatusCode.Conflict, "already_on_plan");
    }

    [Fact]
    public async Task SimulatedOutcome_Rejected_WhenSelectionDisabled()
    {
        await using var strict = env.NewApp(new Dictionary<string, string?> { ["Billing:Simulation:AllowOutcomeSelection"] = "false" });
        var owner = await World.CreateUserAsync(app: strict);
        var tenant = (await owner.Client.PostAsync("/api/tenants", new { name = "Katı Ortam" }).ShouldBeAsync(HttpStatusCode.Created))["tenantId"].Guid();
        await owner.Client.PostAsync($"/api/t/{tenant}/billing/plan-change", new { planCode = "team", simulatedOutcome = SimulatedOutcomes.Decline }, ("Idempotency-Key", "x"))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Declined_NoEntitlement()
    {
        var (owner, tenant) = await NewTenantAsync();
        var operationId = (await _billing.RequestAsync(owner.Client, tenant, "team", SimulatedOutcomes.Decline))["operationId"].Guid();
        await _billing.SubmitAsync();

        var operation = await _billing.OperationAsync(owner.Client, tenant, operationId);
        Assert.Equal("Declined", operation["status"].Str());
        Assert.Equal("card_declined", operation["failureCode"].Str());
        Assert.Equal("Trialing:trial", await _billing.SubscriptionAsync(tenant));
        Assert.Equal(3, (await _billing.WorkspaceAsync(owner.Client, tenant))["entitlements"]!["seatLimit"].Int());
        Assert.Equal(0, await _billing.ChargeCountAsync(operationId));

        // Ret terminaldir; yeni bir işlem başlatılabilir ve o başarılı olabilir.
        await _billing.RequestAsync(owner.Client, tenant, "starter");
        await _billing.SubmitAsync();
        Assert.Equal("Active:starter", await _billing.SubscriptionAsync(tenant));
        Assert.Equal("Declined", await _billing.OperationStatusAsync(operationId));
    }

    [Fact]
    public async Task Timeout_BecomesUnknown_ReconcileAppliesOnce()
    {
        var (owner, tenant) = await NewTenantAsync();
        var operationId = (await _billing.RequestAsync(owner.Client, tenant, "team", SimulatedOutcomes.TimeoutThenSuccess))["operationId"].Guid();
        await _billing.SubmitAsync();

        // Sağlayıcı tahsilatı yaptı ama yanıt kayboldu: bizim tarafta sonuç BİLİNMİYOR. Başarısız sayılmaz, uygulanmış da sayılmaz.
        Assert.Equal("Unknown", await _billing.OperationStatusAsync(operationId));
        Assert.Equal("Trialing:trial", await _billing.SubscriptionAsync(tenant));
        Assert.Equal(1, await _billing.ChargeCountAsync(operationId));

        // Sonuç beklenirken ikinci bir plan değişikliği başlatılamaz.
        await owner.Client.PostAsync($"/api/t/{tenant}/billing/plan-change", new { planCode = "starter" }, ("Idempotency-Key", Guid.NewGuid().ToString()))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "operation_in_progress");

        // Mutabakat vakti gelmeden çalışırsa hiçbir şey yapmaz; vakti gelince sağlayıcıyı sorgular ve başarıyı uygular.
        await _billing.ReconcileAsync();
        Assert.Equal("Unknown", await _billing.OperationStatusAsync(operationId));
        env.Clock.Advance(TimeSpan.FromSeconds(31));
        await _billing.ReconcileAsync();
        Assert.Equal("Succeeded", await _billing.OperationStatusAsync(operationId));
        Assert.Equal("Active:team", await _billing.SubscriptionAsync(tenant));

        // Aynı başarı başka yollardan tekrar gelir: mutabakat yeniden, sağlayıcı webhook'u, webhook'un kopyası. Tek uygulama, tek tahsilat.
        await _billing.ReconcileAsync();
        await _billing.DeliverProviderWebhooksAsync();
        await _billing.ProcessInboxAsync();
        Assert.Equal(1, await _billing.ChargeCountAsync(operationId));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'billing.plan_applied'", tenant));
        // Abonelik satırı yalnız iki kez değişti: istek (epoch) ve tek uygulama. Sürüm 1 → 3.
        Assert.Equal(3, await env.AdminScalarAsync<int>("SELECT version FROM app.subscriptions WHERE tenant_id = $1", tenant));
    }

    [Fact]
    public async Task Retry_SameMerchantReference_SingleCharge()
    {
        // 1) İlk gönderim sağlayıcıya hiç ulaşmadı (zaman aşımı): sorgu "bulunamadı" der; aynı referansla yeniden gönderilir.
        var (owner, tenant) = await NewTenantAsync();
        var lost = (await _billing.RequestAsync(owner.Client, tenant, "starter", SimulatedOutcomes.TimeoutNotRecorded))["operationId"].Guid();
        await _billing.SubmitAsync();
        Assert.Equal("Unknown", await _billing.OperationStatusAsync(lost));
        Assert.Equal(0, await _billing.ChargeCountAsync(lost));
        env.Clock.Advance(TimeSpan.FromSeconds(31));
        await _billing.ReconcileAsync();
        Assert.Equal("Succeeded", await _billing.OperationStatusAsync(lost));
        Assert.Equal(1, await _billing.ChargeCountAsync(lost));
        Assert.Equal(1L, await env.AdminScalarAsync<long>(
            "SELECT count(*) FROM fakepsp.operations f JOIN app.billing_operations o ON o.merchant_reference = f.merchant_reference WHERE o.tenant_id = $1", tenant));

        // 2) Geçici hata: outbox aynı işlemi yeniden dener; sağlayıcı aynı referansı tek tahsilatla sonuçlandırır.
        var (owner2, tenant2) = await NewTenantAsync();
        var flaky = (await _billing.RequestAsync(owner2.Client, tenant2, "team", SimulatedOutcomes.TransientThenSuccess))["operationId"].Guid();
        await _billing.SubmitAsync();
        Assert.Equal("Pending", await _billing.OperationStatusAsync(flaky));
        Assert.Equal("Trialing:trial", await _billing.SubscriptionAsync(tenant2));
        env.Clock.Advance(TimeSpan.FromSeconds(10)); // outbox bekleme süresi
        await _billing.SubmitAsync();
        Assert.Equal("Succeeded", await _billing.OperationStatusAsync(flaky));
        Assert.Equal(1, await _billing.ChargeCountAsync(flaky));
        Assert.Equal(2, await env.AdminScalarAsync<int>(
            "SELECT f.calls FROM fakepsp.operations f JOIN app.billing_operations o ON o.merchant_reference = f.merchant_reference WHERE o.id = $1", flaky));
    }

    [Fact]
    public async Task LateSuccess_AfterCancel_NoReactivation_Refunded()
    {
        var (owner, tenant) = await NewTenantAsync();
        var operationId = (await _billing.RequestAsync(owner.Client, tenant, "team", SimulatedOutcomes.PendingThenWebhook))["operationId"].Guid();
        await _billing.SubmitAsync();
        Assert.Equal("Pending", await _billing.OperationStatusAsync(operationId)); // Sağlayıcı "beklemede" dedi.

        // Owner, ödeme sonucu gelmeden aboneliği iptal eder.
        await owner.Client.PostAsync($"/api/t/{tenant}/billing/cancel").ShouldBeAsync(HttpStatusCode.NoContent);
        Assert.Equal("Cancelled:trial", await _billing.SubscriptionAsync(tenant));

        // Sonra sağlayıcıdan o işlemin başarısı gelir.
        var reference = await _billing.MerchantReferenceAsync(operationId);
        await _billing.SendWebhookAsync(BillingHarness.EventJson("operation.succeeded", "psub_gec_gelen", 1, reference)).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();

        // Abonelik yeniden etkinleşmez, plan yükselmez; alınan ödeme iade yoluna girer ve terminal olur.
        Assert.Equal("Cancelled:trial", await _billing.SubscriptionAsync(tenant));
        Assert.Equal("RefundPending", await _billing.OperationStatusAsync(operationId));
        Assert.Equal("Recovery", (await _billing.WorkspaceAsync(owner.Client, tenant))["accessMode"].Str());
        await _billing.ReconcileAsync();
        Assert.Equal("Refunded", await _billing.OperationStatusAsync(operationId));
        Assert.True((await _billing.OperationAsync(owner.Client, tenant, operationId))["terminal"]!.GetValue<bool>());
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'billing.plan_applied'", tenant));

        // İptalden sonra AÇIKÇA istenen yeni işlem ise uygulanır (kurtarma kilitlenmez).
        await _billing.RequestAsync(owner.Client, tenant, "starter");
        await _billing.SubmitAsync();
        Assert.Equal("Active:starter", await _billing.SubscriptionAsync(tenant));
    }

    [Fact]
    public async Task LateSuccess_ForInactiveTenant_NoReactivation()
    {
        var (owner, tenant) = await NewTenantAsync();
        var operationId = (await _billing.RequestAsync(owner.Client, tenant, "team", SimulatedOutcomes.PendingThenWebhook))["operationId"].Guid();
        await _billing.SubmitAsync();
        // Şirket silme bekliyor (kurulum: durum doğrudan yazılır; gerçek silme akışı Aşama 7 testlerindedir).
        await env.AdminExecuteAsync("UPDATE app.tenants SET status = 'DeletionPending', deletion_requested_at = now(), purge_after = now() + interval '7 days' WHERE id = $1", tenant);

        await _billing.SendWebhookAsync(BillingHarness.EventJson("operation.succeeded", "psub_silinen", 1, await _billing.MerchantReferenceAsync(operationId))).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();

        Assert.Equal("RefundPending", await _billing.OperationStatusAsync(operationId));
        Assert.Equal("Trialing:trial", await _billing.SubscriptionAsync(tenant));
        Assert.Equal("DeletionPending", await env.AdminScalarAsync<string>("SELECT status FROM app.tenants WHERE id = $1", tenant));
        await _billing.ReconcileAsync();
        Assert.Equal("Refunded", await _billing.OperationStatusAsync(operationId));
    }

    [Fact]
    public async Task NewRequest_SupersedesOlder_OlderLateSuccessIsRefunded()
    {
        var (owner, tenant) = await NewTenantAsync();
        var older = (await _billing.RequestAsync(owner.Client, tenant, "team", SimulatedOutcomes.Unresolvable))["operationId"].Guid();
        await _billing.SubmitAsync();
        await DriveToNeedsResolutionAsync(older);

        // Çözüm bekleyen işlem şirketi kilitlemez: yeni işlem istenebilir ve uygulanır.
        var newer = (await _billing.RequestAsync(owner.Client, tenant, "starter"))["operationId"].Guid();
        await _billing.SubmitAsync();
        Assert.Equal("Active:starter", await _billing.SubscriptionAsync(tenant));
        Assert.Equal("Succeeded", await _billing.OperationStatusAsync(newer));

        // Eski işlemin başarısı çok sonra gelse de planı Team'e çeviremez.
        await _billing.SendWebhookAsync(BillingHarness.EventJson("operation.succeeded", "psub_eski", 99, await _billing.MerchantReferenceAsync(older))).ShouldBeAsync(HttpStatusCode.Accepted);
        await _billing.ProcessInboxAsync();
        Assert.Equal("Active:starter", await _billing.SubscriptionAsync(tenant));
        Assert.Equal("NeedsResolution", await _billing.OperationStatusAsync(older));
    }

    [Fact]
    public async Task UnresolvedOutcome_NeedsResolution_OperatorResolvesTerminal()
    {
        var (owner, tenant) = await NewTenantAsync();
        var operatorClient = await OperatorAsync();

        // A) Sonuç hiç öğrenilemiyor → otomatik deneme sınırı → operatör kuyruğu → operatör "void" (tahsilat yok) → terminal.
        var unknown = (await _billing.RequestAsync(owner.Client, tenant, "team", SimulatedOutcomes.Unresolvable))["operationId"].Guid();
        await _billing.SubmitAsync();
        await DriveToNeedsResolutionAsync(unknown);
        Assert.Equal("Trialing:trial", await _billing.SubscriptionAsync(tenant)); // Belirsiz sonuç entitlement açmaz.
        Assert.False((await _billing.OperationAsync(owner.Client, tenant, unknown))["terminal"]!.GetValue<bool>()); // Kuyrukta durmak çözüm değildir.

        var queue = await operatorClient.GetAsync("/api/platform/billing/operations").ShouldBeAsync(HttpStatusCode.OK);
        var queued = queue["items"]!.AsArray().Single(o => o!["operationId"].Guid() == unknown)!;
        Assert.Equal("outcome_unknown", queued["failureCode"].Str());

        // Yetki ve doğrulama: operatör olmayan göremez; gerekçesiz veya geçersiz eylem reddedilir.
        await owner.Client.GetAsync("/api/platform/billing/operations").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await owner.Client.PostAsync($"/api/platform/billing/operations/{unknown}/resolve", new { action = "void", note = "kendi işlemimi kapatıyorum" }).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await operatorClient.PostAsync($"/api/platform/billing/operations/{unknown}/resolve", new { action = "void", note = "kısa" }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        await operatorClient.PostAsync($"/api/platform/billing/operations/{unknown}/resolve", new { action = "succeed", note = "elle başarılı sayalım lütfen" }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");

        var resolved = await operatorClient.PostAsync($"/api/platform/billing/operations/{unknown}/resolve", new { action = "void", note = "Sağlayıcı panelinde tahsilat yok; işlem geçersiz sayıldı." }).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Voided", resolved["status"].Str());
        Assert.True((await _billing.OperationAsync(owner.Client, tenant, unknown))["terminal"]!.GetValue<bool>());
        await operatorClient.PostAsync($"/api/platform/billing/operations/{unknown}/resolve", new { action = "close", note = "ikinci kez çözmeye çalışıyorum" }).ShouldBeProblemAsync(HttpStatusCode.Conflict, "operation_not_awaiting_resolution");

        // B) Ödeme alındı ama uygulanamadı ve iade de başarısız → operatör kuyruğu → "refund" (yeniden iade denemesi) yine başarısız → "close".
        var stuck = (await _billing.RequestAsync(owner.Client, tenant, "team", SimulatedOutcomes.SuccessRefundFails))["operationId"].Guid();
        await owner.Client.PostAsync($"/api/t/{tenant}/billing/cancel").ShouldBeAsync(HttpStatusCode.NoContent); // Uygulanmadan önce iptal: başarı sahipsiz kalır.
        await _billing.SubmitAsync();
        Assert.Equal("RefundPending", await _billing.OperationStatusAsync(stuck));
        Assert.Equal(1, await _billing.ChargeCountAsync(stuck));
        await DriveToNeedsResolutionAsync(stuck);
        await operatorClient.PostAsync($"/api/platform/billing/operations/{stuck}/resolve", new { action = "refund", note = "İade yeniden denensin (sağlayıcı kesintisi bitti)." }).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("RefundPending", await _billing.OperationStatusAsync(stuck));
        await DriveToNeedsResolutionAsync(stuck);
        var closed = await operatorClient.PostAsync($"/api/platform/billing/operations/{stuck}/resolve", new { action = "close", note = "İade sağlayıcı destek kaydı #SIM-1 ile elle yapıldı." }).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("ManuallyClosed", closed["status"].Str());
        Assert.Equal("Cancelled:trial", await _billing.SubscriptionAsync(tenant));

        // Her operatör eylemi ve her kuyruğa düşüş platform denetim kaydında.
        var audit = await (await operatorClient.GetAsync("/api/platform/audit?limit=100")).Content.ReadAsStringAsync();
        Assert.Contains("billing.needs_resolution", audit);
        Assert.Contains("billing.operation_resolved", audit);
        Assert.Contains("billing.orphaned_success", audit);
        Assert.Equal(3L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.platform_audit_events WHERE tenant_id = $1 AND action = 'billing.operation_resolved' AND actor_user_id IS NOT NULL", tenant));
    }

    /// <summary>Mutabakatı, işlem otomatik deneme sınırına ulaşıp operatör kuyruğuna düşene kadar (sahte saatle) ilerletir.</summary>
    private async Task DriveToNeedsResolutionAsync(Guid operationId)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (await _billing.OperationStatusAsync(operationId) == "NeedsResolution")
            {
                return;
            }
            env.Clock.Advance(TimeSpan.FromMinutes(31));
            await _billing.ReconcileAsync();
        }
        Assert.Equal("NeedsResolution", await _billing.OperationStatusAsync(operationId));
    }
}
