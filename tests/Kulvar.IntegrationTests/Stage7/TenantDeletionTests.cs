using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Modules.Operations;
using Kulvar.Platform.Data;
using Kulvar.Platform.Messaging;
using Kulvar.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kulvar.IntegrationTests.Stage7;

public sealed class TenantDeletionTests(TestEnvironment env)
{
    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);
    private World World => env.World;
    private ExportRunner Exports => env.App.Services.GetRequiredService<ExportRunner>();
    private ExportOptions ExportOptions => env.App.Services.GetRequiredService<IOptions<ExportOptions>>().Value;

    private TenantPurger NewPurger() => new(env.App.WorkerDb, env.App.Services.GetRequiredService<IOptions<ExportOptions>>(), NullLogger<TenantPurger>.Instance);

    private static Task<System.Text.Json.Nodes.JsonObject> RequestDeletionAsync(TestUser owner, Guid tenant, string name) =>
        owner.Client.PostAsync($"/api/t/{tenant}/deletion", new { confirmName = name, password = owner.Password }).ShouldBeAsync(HttpStatusCode.OK);

    private Task<string?> StatusAsync(Guid tenant) => env.AdminScalarAsync<string>("SELECT status FROM app.tenants WHERE id = $1", tenant);

    /// <summary>Şirkete ait tüm tablolardaki satır sayıları (tablo=sayı;…). Temizlemenin eksiksizliğini ve komşunun dokunulmazlığını ölçer.</summary>
    private async Task<string> RowCountsAsync(Guid tenant)
    {
        var parts = new List<string>();
        foreach (var table in TenantPurger.PurgeOrder.Concat(["subscriptions", "billing_operations"]))
        {
            parts.Add($"{table}={await env.AdminScalarAsync<long>($"SELECT count(*) FROM app.{table} WHERE tenant_id = $1", tenant)}");
        }
        return string.Join(";", parts);
    }

    private static string WithoutAudit(string counts) => string.Join(";", counts.Split(';').Where(p => !p.StartsWith("audit_events=", StringComparison.Ordinal)));

    /// <summary>Vakti gelen tüm şirketleri temizler (paylaşılan veritabanında başka testlerin şirketleri de olabilir).</summary>
    private async Task RunPurgerAsync()
    {
        for (var round = 0; round < 50 && await NewPurger().RunOnceAsync(CancellationToken.None) > 0; round++)
        {
        }
    }

    private async Task<ApiClient> ReloginAsync(TestUser user)
    {
        var client = env.App.NewClient();
        await client.LoginAsync(user.Email, user.Password).ShouldBeAsync(HttpStatusCode.OK);
        return client;
    }

    /// <summary>Her kiracı tablosunda satırı olan dolu bir şirket.</summary>
    private async Task<(TestUser Owner, Guid Tenant, TestUser Shared)> PopulatedTenantAsync(string name, string canary, TestUser? shared = null)
    {
        // Kanarya yalnız şirket verisinde yer alır; kullanıcının görünen adı global kimliktir ve şirket silinince bilerek kalır.
        var owner = await World.CreateUserAsync("Şirket Sahibi");
        var tenant = await World.CreateTenantAsync(owner, name);
        await World.SetPlanDirectAsync(tenant, "team");
        shared ??= await World.CreateUserAsync("Ortak Kullanıcı");
        await World.InviteAsync(owner, tenant, shared.Email, TenantRole.Agent);
        await shared.Client.PostAsync("/api/invites/accept", new { token = await World.InviteTokenAsync(shared) }).ShouldBeAsync(HttpStatusCode.OK);
        await World.InviteAsync(owner, tenant, World.UniqueEmail("bekleyen"), TenantRole.Viewer); // bekleyen davet
        var customer = (await World.CreateCustomerAsync(owner.Client, tenant, $"{canary} Müşteri", $"{canary.ToLowerInvariant()}@ornek.test", notes: $"{canary} not"))["id"].Guid();
        var ticket = (await World.CreateTicketAsync(shared.Client, tenant, customer, $"{canary} talep", $"{canary} açıklama", assigneeUserId: shared.Id))["id"].Guid();
        await shared.Client.PostAsync($"/api/t/{tenant}/tickets/{ticket}/comments", new { body = $"{canary} yorum" }).ShouldBeAsync(HttpStatusCode.Created);
        await owner.Client.PostAsync($"/api/t/{tenant}/ownership-offers", new { toUserId = shared.Id, demoteSelf = false }).ShouldBeAsync(HttpStatusCode.Created);
        await owner.Client.PostAsync($"/api/t/{tenant}/exports").ShouldBeAsync(HttpStatusCode.Accepted);
        await Exports.RunOnceAsync(CancellationToken.None);
        await env.App.DrainOutboxAsync();
        return (owner, tenant, shared);
    }

    [Fact]
    public async Task Request_RequiresOwner_NameAndPassword()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner, "Silinecek Şirket");
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin);

        await admin.Client.PostAsync($"/api/t/{tenant}/deletion", new { confirmName = "Silinecek Şirket", password = admin.Password }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await owner.Client.PostAsync($"/api/t/{tenant}/deletion", new { confirmName = "silinecek şirket", password = owner.Password }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "confirmation_failed");
        await owner.Client.PostAsync($"/api/t/{tenant}/deletion", new { confirmName = "Silinecek Şirket", password = "yanlis-parola-123" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "confirmation_failed");
        await owner.Client.PostAsync($"/api/t/{tenant}/deletion", new { confirmName = "Silinecek Şirket" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "confirmation_failed");
        Assert.Equal("Active", await StatusAsync(tenant));

        var accepted = await RequestDeletionAsync(owner, tenant, "Silinecek Şirket");
        Assert.Equal("DeletionPending", accepted["status"].Str());
        Assert.Equal(env.Clock.GetUtcNow() + Grace, accepted["purgeAfter"]!.GetValue<DateTimeOffset>());
        // İkinci istek: şirket artık salt-okunur modda; bu izin o modda yok.
        await owner.Client.PostAsync($"/api/t/{tenant}/deletion", new { confirmName = "Silinecek Şirket", password = owner.Password }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "access_mode_restricted");
    }

    [Fact]
    public async Task Pending_StopsWrites_CancelRestores()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync("Bekleyen Şirket");
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin);
        await RequestDeletionAsync(owner, tenant, "Bekleyen Şirket");

        var summary = await owner.Client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("ReadOnly", summary["accessMode"].Str());
        Assert.Equal("DeletionPending", summary["tenant"]!["status"].Str());
        Assert.NotNull(summary["tenant"]!["purgeAfter"]);

        // Okuma sürer; yazma herkes için durur (içerik, ekip, plan).
        await admin.Client.GetAsync($"/api/t/{tenant}/customers/{customerId}").ShouldBeAsync(HttpStatusCode.OK);
        foreach (var attempt in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => owner.Client.PostAsync($"/api/t/{tenant}/customers", new { name = "Yazılmamalı" }),
                     () => admin.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "x", description = "x" }, ("Idempotency-Key", "silme")),
                     () => owner.Client.PostAsync($"/api/t/{tenant}/invites", new { email = World.UniqueEmail(), role = "Viewer" }),
                     () => owner.Client.PostAsync($"/api/t/{tenant}/billing/plan-change", new { planCode = "team" }, ("Idempotency-Key", "silme-plan")),
                     () => owner.Client.DeleteAsync($"/api/t/{tenant}/members/{admin.Id}"),
                 })
        {
            await attempt().ShouldBeProblemAsync(HttpStatusCode.Forbidden, "access_mode_restricted");
        }
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.customers WHERE tenant_id = $1", tenant));

        // İptal yalnız Owner'a; iptalden sonra her şey geri gelir.
        await admin.Client.DeleteAsync($"/api/t/{tenant}/deletion").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await owner.Client.DeleteAsync($"/api/t/{tenant}/deletion").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Active", await StatusAsync(tenant));
        await World.CreateCustomerAsync(owner.Client, tenant, "İptalden sonra");
        await owner.Client.DeleteAsync($"/api/t/{tenant}/deletion").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "access_mode_restricted");

        // İptal edilmiş istek, süre geçse de temizlenmez.
        env.Clock.Advance(Grace + TimeSpan.FromDays(1));
        await RunPurgerAsync();
        Assert.Equal("Active", await StatusAsync(tenant));
        Assert.Equal(2L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.customers WHERE tenant_id = $1", tenant));
    }

    [Fact]
    public async Task Purge_RemovesTenantA_LeavesTenantB_AndSharedUser()
    {
        var (ownerA, tenantA, shared) = await PopulatedTenantAsync("Akasya Silinecek", "CANARYDEL-A");
        var (ownerB, tenantB, _) = await PopulatedTenantAsync("Boreas Kalacak", "CANARYDEL-B", shared);
        var artifactDirA = ExportOptions.TenantDirectory(tenantA);
        var artifactDirB = ExportOptions.TenantDirectory(tenantB);
        Assert.NotEmpty(Directory.GetFiles(artifactDirA));
        var countsA = await RowCountsAsync(tenantA);
        var countsB = await RowCountsAsync(tenantB);
        Assert.DoesNotContain("=0", countsA.Replace("billing_operations=0", "billing_operations=-")); // Her tabloda satır var.
        var usersBefore = await env.AdminScalarAsync<long>("SELECT count(*) FROM app.users");
        var sharedSessionsBefore = await env.AdminScalarAsync<long>("SELECT count(*) FROM app.sessions WHERE user_id = $1", shared.Id);

        await RequestDeletionAsync(ownerA, tenantA, "Akasya Silinecek");
        // Süre dolmadan temizlenmez.
        env.Clock.Advance(Grace - TimeSpan.FromMinutes(1));
        await RunPurgerAsync();
        Assert.Equal("DeletionPending", await StatusAsync(tenantA));
        Assert.Equal(WithoutAudit(countsA), WithoutAudit(await RowCountsAsync(tenantA))); // Bekleme süresinde veri yerinde (yalnız denetim kaydı arttı).

        env.Clock.Advance(TimeSpan.FromMinutes(2));
        await RunPurgerAsync();

        // A: bütün kiracı satırları silindi; mezar taşı kaldı (kişisel veri yok).
        Assert.Equal("Deleted", await StatusAsync(tenantA));
        var afterA = await RowCountsAsync(tenantA);
        foreach (var table in TenantPurger.PurgeOrder)
        {
            Assert.Contains($"{table}=0", afterA);
        }
        Assert.Contains("subscriptions=1", afterA);
        Assert.Equal("(silindi)|Cancelled", await env.AdminScalarAsync<string>(
            "SELECT t.name || '|' || s.status FROM app.tenants t JOIN app.subscriptions s ON s.tenant_id = t.id WHERE t.id = $1", tenantA));
        Assert.False(Directory.Exists(artifactDirA));
        // A'nın kanaryası hiçbir tabloda kalmadı.
        foreach (var table in new[] { "customers", "tickets", "ticket_comments", "invites", "sink_messages", "outbox_messages", "audit_events", "tenants", "platform_audit_events" })
        {
            Assert.Equal(0L, await env.AdminScalarAsync<long>($"SELECT count(*) FROM app.{table} x WHERE x::text ILIKE '%CANARYDEL-A%'"));
        }

        // B ve global kimlik: hiçbir şey değişmedi.
        Assert.Equal(countsB, await RowCountsAsync(tenantB));
        Assert.NotEmpty(Directory.GetFiles(artifactDirB));
        Assert.Equal(usersBefore, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.users"));
        Assert.Equal(sharedSessionsBefore, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.sessions WHERE user_id = $1", shared.Id));

        // Ortak kullanıcı: giriş yapabilir, yalnız B'yi görür, B'de çalışmaya devam eder; A onun için "yok".
        var sharedClient = await ReloginAsync(shared);
        var memberships = await sharedClient.GetAsync("/api/me/memberships").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal([tenantB], memberships["items"]!.AsArray().Select(m => m!["tenantId"].Guid()).ToList());
        await sharedClient.GetAsync($"/api/t/{tenantB}/tickets").ShouldBeAsync(HttpStatusCode.OK);
        await sharedClient.GetAsync($"/api/t/{tenantA}/tickets").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        var ownerAClient = await ReloginAsync(ownerA);
        await ownerAClient.GetAsync($"/api/t/{tenantA}/").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        await ownerAClient.DeleteAsync($"/api/t/{tenantA}/deletion").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        Assert.Empty((await ownerAClient.GetAsync("/api/me/memberships").ShouldBeAsync(HttpStatusCode.OK))["items"]!.AsArray());
        var ownerBClient = await ReloginAsync(ownerB);
        var list = await (await ownerBClient.GetAsync($"/api/t/{tenantB}/customers")).Content.ReadAsStringAsync();
        Assert.Contains("CANARYDEL-B", list);

        // Platform denetimi: temizleme kaydı (içerik yok, sayılar var).
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.platform_audit_events WHERE tenant_id = $1 AND action = 'tenant.purged'", tenantA));
        // Temizleme yinelenebilir: tekrar çalışınca hiçbir şey yapmaz.
        Assert.False(await NewPurger().PurgeAsync(tenantA, CancellationToken.None));
    }

    [Fact]
    public async Task Purge_Interrupted_ThenRepeated_Completes()
    {
        var (owner, tenant, _) = await PopulatedTenantAsync("Yarıda Kalan", "CANARYDEL-C");
        await RequestDeletionAsync(owner, tenant, "Yarıda Kalan");
        env.Clock.Advance(Grace + TimeSpan.FromSeconds(1));

        // Worker "tickets" tablosunu sildikten hemen sonra çöker.
        var crashing = NewPurger();
        crashing.AfterStepHook = table => table == "tickets" ? throw new IOException("simulated crash mid-purge") : Task.CompletedTask;
        await Assert.ThrowsAsync<IOException>(() => crashing.PurgeAsync(tenant, CancellationToken.None));

        Assert.Equal("Purging", await StatusAsync(tenant));
        var partial = await RowCountsAsync(tenant);
        Assert.Contains("tickets=0", partial);
        Assert.DoesNotContain("customers=0", partial);
        Assert.DoesNotContain("memberships=0", partial);
        // Yarı temizlenmiş şirket hiçbir üyeye görünmez; iptal de artık mümkün değildir.
        var ownerClient = await ReloginAsync(owner);
        await ownerClient.GetAsync($"/api/t/{tenant}/customers").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        await ownerClient.DeleteAsync($"/api/t/{tenant}/deletion").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");

        // Yeniden çalışan worker kaldığı yerden bitirir.
        await RunPurgerAsync();
        Assert.Equal("Deleted", await StatusAsync(tenant));
        var done = await RowCountsAsync(tenant);
        foreach (var table in TenantPurger.PurgeOrder)
        {
            Assert.Contains($"{table}=0", done);
        }
    }

    [Fact]
    public async Task PendingJobs_DoNotRecreateTenantData_AndLatePaymentIsRefunded()
    {
        var owner = await World.CreateUserAsync("Geç İş Sahibi");
        var tenant = await World.CreateTenantAsync(owner, "Geç İşler");
        // Silmeden önce kuyrukta bekleyen işler: işlenmemiş davet postası ve sonucu "beklemede" bir ödeme.
        var billing = new Stage6.BillingHarness(env);
        var operationId = (await billing.RequestAsync(owner.Client, tenant, "team", Kulvar.Modules.Billing.Provider.SimulatedOutcomes.PendingThenWebhook))["operationId"].Guid();
        await billing.SubmitAsync();
        var reference = await billing.MerchantReferenceAsync(operationId);
        var inviteId = await World.InviteAsync(owner, tenant, World.UniqueEmail("gec-davet"), TenantRole.Agent); // outbox'ta bekliyor
        await env.AdminExecuteAsync("UPDATE app.outbox_messages SET available_at = $2 WHERE tenant_id = $1 AND status = 'Pending'", tenant, env.Clock.GetUtcNow() + Grace + TimeSpan.FromDays(1));

        await RequestDeletionAsync(owner, tenant, "Geç İşler");
        env.Clock.Advance(Grace + TimeSpan.FromSeconds(1));
        await RunPurgerAsync();
        Assert.Equal("Deleted", await StatusAsync(tenant));

        // Bekleyen işler şirketle birlikte silindi; sonradan çalışan worker veri üretmez.
        env.Clock.Advance(TimeSpan.FromDays(2));
        await env.App.DrainOutboxAsync();
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.invites WHERE id = $1", inviteId));
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.sink_messages WHERE tenant_id = $1", tenant));

        // Silinmiş şirket adına (ör. gecikmiş bir üretici) kuyruğa mesaj düşse bile etkisiz kapanır.
        var late = await env.App.WorkerDb.RunAsync(DbScope.None, async (session, ct) =>
        {
            var message = OutboxMessage.Create(tenant, null, "tenancy.invite_created", new { inviteId }, session.Now);
            session.Db.Add(message);
            await session.Db.SaveChangesAsync(ct);
            return message.Id;
        }, CancellationToken.None);
        await env.App.DrainOutboxAsync();
        Assert.Equal("Done|tenant_gone", await env.AdminScalarAsync<string>("SELECT status || '|' || last_error FROM app.outbox_messages WHERE id = $1", late));
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.sink_messages WHERE tenant_id = $1", tenant));
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.memberships WHERE tenant_id = $1", tenant));

        // Geç gelen ödeme başarısı: şirket yeniden doğmaz; ödeme iade edilir.
        await billing.SendWebhookAsync(Stage6.BillingHarness.EventJson("operation.succeeded", "psub_silinmis", 1, reference)).ShouldBeAsync(HttpStatusCode.Accepted);
        await billing.ProcessInboxAsync();
        Assert.Equal("RefundPending", await billing.OperationStatusAsync(operationId));
        await billing.ReconcileAsync();
        Assert.Equal("Refunded", await billing.OperationStatusAsync(operationId));
        Assert.Equal("Deleted", await StatusAsync(tenant));
        Assert.Equal("Cancelled:trial", await billing.SubscriptionAsync(tenant));
    }

    [Fact]
    public async Task Export_NotDownloadable_AfterDeletion_FilesRemoved_AndPurgeWaitsForRunningExport()
    {
        var (owner, tenant, _) = await World.CreateTenantWithCustomerAsync("Export Yarışı");
        await World.SetPlanDirectAsync(tenant, "team");
        var ready = (await owner.Client.PostAsync($"/api/t/{tenant}/exports").ShouldBeAsync(HttpStatusCode.Accepted))["id"].Guid();
        await Exports.RunOnceAsync(CancellationToken.None);
        var url = (await owner.Client.PostAsync($"/api/t/{tenant}/exports/{ready}/download-link").ShouldBeAsync(HttpStatusCode.OK))["url"].Str();
        var directory = ExportOptions.TenantDirectory(tenant);

        // Silme bekleyen şirkette Owner (plan izin veriyorsa) hâlâ export alabilir: verisini götürebilsin.
        await RequestDeletionAsync(owner, tenant, "Export Yarışı");
        var running = (await owner.Client.PostAsync($"/api/t/{tenant}/exports").ShouldBeAsync(HttpStatusCode.Accepted))["id"].Guid();
        env.Clock.Advance(Grace + TimeSpan.FromSeconds(1));

        // Export üretimi sürerken (şirket satırı FOR SHARE) temizleme başlatılır: export bitene kadar bekler.
        var runner = new ExportRunner(env.App.WorkerDb, env.App.Services.GetRequiredService<IOptions<ExportOptions>>(), NullLogger<ExportRunner>.Instance) { WorkerId = "export-race" };
        Task<bool>? purge = null;
        runner.BeforeFinalizeHook = async _ =>
        {
            purge = Task.Run(() => NewPurger().PurgeAsync(tenant, CancellationToken.None));
            await WaitUntilBlockedOnTenantAsync();
            Assert.False(purge.IsCompleted);
            Assert.Equal("DeletionPending", await StatusAsync(tenant));
        };
        await runner.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(purge);
        Assert.True(await purge!.WaitAsync(TimeSpan.FromSeconds(30)));

        // Export tamamlandı, ardından temizleme her şeyi (yeni dosya dahil) sildi.
        Assert.Equal("Deleted", await StatusAsync(tenant));
        Assert.False(Directory.Exists(directory));
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.export_jobs WHERE tenant_id = $1", tenant));
        var ownerClient = await ReloginAsync(owner);
        await ownerClient.GetAsync(url).ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        await ownerClient.PostAsync($"/api/t/{tenant}/exports/{running}/download-link").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");

        // Temizlemeden SONRA başlamaya çalışan export işi (ör. gecikmiş kiralama) dosya üretmez.
        await Exports.RunOnceAsync(CancellationToken.None);
        Assert.False(Directory.Exists(directory));
    }

    private async Task WaitUntilBlockedOnTenantAsync()
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var waiting = await env.AdminScalarAsync<long>(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%app.tenants%FOR UPDATE%'");
            if (waiting > 0)
            {
                return;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Temizleme şirket kilidinde beklemeye geçmedi.");
    }
}
