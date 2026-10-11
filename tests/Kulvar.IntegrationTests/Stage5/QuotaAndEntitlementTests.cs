using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Modules.Billing;
using Kulvar.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Kulvar.IntegrationTests.Stage5;

public sealed class QuotaTests(TestEnvironment env)
{
    private const int TrialTicketLimit = 20;
    private World World => env.World;

    private Task<long> TicketCountAsync(Guid tenant) => env.AdminScalarAsync<long>("SELECT count(*) FROM app.tickets WHERE tenant_id = $1", tenant);

    private async Task FillAsync(ApiClient client, Guid tenant, Guid customerId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await World.CreateTicketAsync(client, tenant, customerId, $"Dolgu {i}");
        }
    }

    [Fact]
    public async Task LastSlot_25ConcurrentCreates_ExactlyOneSucceeds()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        await FillAsync(owner.Client, tenant, customerId, TrialTicketLimit - 1); // Son tek hak kaldı.
        const int Attempts = 25;

        using var gate = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, Attempts).Select(async i =>
        {
            var client = owner.Client.Clone();
            await gate.WaitAsync();
            var response = await client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = $"Yarışan {i}", description = "x" }, ("Idempotency-Key", $"yaris-{i}"));
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
        }).ToList();
        gate.Release(Attempts);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        var rejected = results.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        Assert.Equal(Attempts - 1, rejected.Count);
        Assert.All(rejected, r =>
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Contains("\"code\":\"quota_exceeded\"", r.Body);
        });

        // Sayaç, kayıt defteri ve gerçek talep sayısı birbirini tutar; reddedilenlerden hiçbir iz kalmaz.
        Assert.Equal(TrialTicketLimit, await World.TicketUsageAsync(tenant));
        Assert.Equal((long)TrialTicketLimit, await TicketCountAsync(tenant));
        Assert.Equal((long)TrialTicketLimit, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.usage_events WHERE tenant_id = $1", tenant));
        Assert.Equal((long)TrialTicketLimit, await env.AdminScalarAsync<long>("SELECT max(number) FROM app.tickets WHERE tenant_id = $1", tenant));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.idempotency_keys WHERE tenant_id = $1 AND key LIKE 'yaris-%'", tenant));
    }

    [Fact]
    public async Task TwoTenants_HaveIndependentCounters()
    {
        var (ownerA, tenantA, customerA) = await World.CreateTenantWithCustomerAsync("Kota A");
        var (ownerB, tenantB, customerB) = await World.CreateTenantWithCustomerAsync("Kota B");
        await FillAsync(ownerA.Client, tenantA, customerA, TrialTicketLimit);

        await ownerA.Client.PostAsync($"/api/t/{tenantA}/tickets", new { customerId = customerA, title = "A dolu", description = "x" }, ("Idempotency-Key", "a-dolu"))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "quota_exceeded");
        // A'nın dolu olması B'yi etkilemez.
        await World.CreateTicketAsync(ownerB.Client, tenantB, customerB, "B serbest");
        Assert.Equal(TrialTicketLimit, await World.TicketUsageAsync(tenantA));
        Assert.Equal(1, await World.TicketUsageAsync(tenantB));
    }

    [Fact]
    public async Task IdempotentReplay_DoesNotConsumeTwice()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var body = new { customerId, title = "Tek kota", description = "x" };
        for (var i = 0; i < 5; i++)
        {
            await owner.Client.PostAsync($"/api/t/{tenant}/tickets", body, ("Idempotency-Key", "tek-kota")).ShouldBeAsync(HttpStatusCode.Created);
        }
        Assert.Equal(1, await World.TicketUsageAsync(tenant));
        Assert.Equal(1L, await TicketCountAsync(tenant));
    }

    [Fact]
    public async Task FailedCreate_DoesNotConsume()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        await World.CreateTicketAsync(owner.Client, tenant, customerId);

        // Doğrulama hatası, olmayan müşteri, olmayan atanan: kota tüketilmez.
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "", description = "x" }, ("Idempotency-Key", "k1")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId = Guid.NewGuid(), title = "x", description = "x" }, ("Idempotency-Key", "k2")).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "x", description = "x", assigneeUserId = Guid.NewGuid() }, ("Idempotency-Key", "k3")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(1, await World.TicketUsageAsync(tenant));

        // Kota tüketildikten SONRA oluşan hata: tüketim de geri alınır.
        await env.AdminExecuteAsync(
            """
            CREATE FUNCTION kulvar_test.fail_ticket_insert() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
              IF NEW.tenant_id = TG_ARGV[0]::uuid THEN RAISE EXCEPTION 'injected failure after quota consume' USING ERRCODE = 'P0001'; END IF;
              RETURN NEW;
            END $fn$
            """);
        try
        {
            await env.AdminExecuteAsync($"CREATE TRIGGER trg_fail_ticket_insert BEFORE INSERT ON app.tickets FOR EACH ROW EXECUTE FUNCTION kulvar_test.fail_ticket_insert('{tenant}')");
            await owner.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "Yarım", description = "x" }, ("Idempotency-Key", "k4"))
                .ShouldBeProblemAsync(HttpStatusCode.InternalServerError, "internal_error");
        }
        finally
        {
            await env.AdminExecuteAsync("DROP TRIGGER IF EXISTS trg_fail_ticket_insert ON app.tickets");
            await env.AdminExecuteAsync("DROP FUNCTION IF EXISTS kulvar_test.fail_ticket_insert()");
        }
        Assert.Equal(1, await World.TicketUsageAsync(tenant));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.usage_events WHERE tenant_id = $1", tenant));
    }

    [Fact]
    public async Task Period_ResetsAtUtcMonthBoundary_NotLocalTime()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        // Abonelik dönem boyunca etkin kalsın (trial 14 günde biter): test planı kurulumda Starter yapılır ama limit için sayaç doldurulur.
        await World.SetPlanDirectAsync(tenant, "starter");
        const int StarterLimit = 200;

        // Saati bir sonraki UTC ay başından 2 saniye öncesine getir.
        var now = env.Clock.GetUtcNow();
        var nextMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        env.Clock.Advance(nextMonth - now - TimeSpan.FromSeconds(2));
        var client = env.App.NewClient();
        await client.LoginAsync(owner.Email, owner.Password).ShouldBeAsync(HttpStatusCode.OK);

        // Bu dönemin sayacını limitin bir eksiğine getir (kurulum), son hakkı gerçek istekle tüket.
        var period = new DateOnly(env.Clock.GetUtcNow().Year, env.Clock.GetUtcNow().Month, 1);
        await env.AdminExecuteAsync("INSERT INTO app.usage_counters (tenant_id, metric, period_start, used) VALUES ($1, 'tickets_created', $2, $3)", tenant, period, StarterLimit - 1);
        await World.CreateTicketAsync(client, tenant, customerId, "Dönemin son talebi");

        var problem = await client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "Bir fazla", description = "x" }, ("Idempotency-Key", "bir-fazla"))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "quota_exceeded");
        Assert.Equal(nextMonth, problem["resetsAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(StarterLimit, problem["limit"].Int());
        Assert.Equal(UsageMetrics.TicketsCreated, problem["metric"].Str());

        // 23:59:59Z hâlâ eski dönem; 00:00:00Z yeni dönem.
        env.Clock.Advance(TimeSpan.FromSeconds(1));
        await client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "Hâlâ dolu", description = "x" }, ("Idempotency-Key", "hala-dolu"))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "quota_exceeded");
        env.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(nextMonth, env.Clock.GetUtcNow());
        await World.CreateTicketAsync(client, tenant, customerId, "Yeni dönemin ilk talebi");

        Assert.Equal(
            $"{period:yyyy-MM-dd}={StarterLimit},{DateOnly.FromDateTime(nextMonth.UtcDateTime):yyyy-MM-dd}=1",
            await env.AdminScalarAsync<string>("SELECT string_agg(period_start::text || '=' || used, ',' ORDER BY period_start) FROM app.usage_counters WHERE tenant_id = $1", tenant));
        var summary = await client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(1, summary["usage"]!["ticketsThisPeriod"].Int());
    }

    [Fact]
    public async Task CloseOrReopen_DoesNotRefundOrConsume()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var id = (await World.CreateTicketAsync(owner.Client, tenant, customerId))["id"].Guid();
        foreach (var action in new[] { "Resolve", "Close", "Reopen", "Start", "Resolve", "Reopen" })
        {
            await Stage3.TicketApi.TransitionAsync(owner.Client, tenant, id, action);
        }
        Assert.Equal(1, await World.TicketUsageAsync(tenant));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.usage_events WHERE tenant_id = $1", tenant));
        // Kayıt defteri uygulama rolü için yalnız-ekle: güncelleme ve silme yetkisi yoktur.
        await using var app = await RoleSession.OpenAsync(env.Postgres.App(), owner.Id, tenant);
        Assert.Equal("42501", (await app.TryExecAsync("DELETE FROM app.usage_events WHERE tenant_id = $1", tenant)).SqlState);
        Assert.Equal("42501", (await app.TryExecAsync("UPDATE app.usage_events SET period_start = date '2000-01-01' WHERE tenant_id = $1", tenant)).SqlState);
        Assert.Equal("42501", (await app.TryExecAsync("DELETE FROM app.tickets WHERE tenant_id = $1", tenant)).SqlState);
    }

    [Fact]
    public async Task Exceeded_Returns403Problem_WithResetAt_Not429()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        await env.AdminExecuteAsync("INSERT INTO app.usage_counters (tenant_id, metric, period_start, used) VALUES ($1, 'tickets_created', $2, $3)",
            tenant, new DateOnly(env.Clock.GetUtcNow().Year, env.Clock.GetUtcNow().Month, 1), TrialTicketLimit);

        var response = await owner.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "Kota dolu", description = "x" }, ("Idempotency-Key", "kota-dolu"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // 429 değil: bu abonelik kotasıdır, hız sınırı değil.
        Assert.False(response.Headers.Contains("Retry-After"));
        var problem = await response.JsonAsync();
        Assert.Equal("quota_exceeded", problem["code"].Str());
        Assert.Equal(TrialTicketLimit, problem["limit"].Int());
        Assert.Equal(TrialTicketLimit, problem["used"].Int());
        Assert.True(problem["resetsAt"]!.GetValue<DateTimeOffset>() > env.Clock.GetUtcNow());
        // Kota reddi idempotency anahtarını "yakmaz": yer açılınca aynı anahtar çalışır.
        await World.SetPlanDirectAsync(tenant, "team");
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "Kota dolu", description = "x" }, ("Idempotency-Key", "kota-dolu")).ShouldBeAsync(HttpStatusCode.Created);
    }
}

public sealed class EntitlementTests(TestEnvironment env)
{
    private World World => env.World;

    [Fact]
    public async Task Plans_ArePublic_AndWorkspaceShowsCurrentEntitlements()
    {
        var plans = await env.App.NewClient().GetAsync("/api/plans").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(["trial", "starter", "team"], plans["items"]!.AsArray().Select(p => p!["code"].Str()).ToList());
        Assert.Equal(149000L, plans["items"]![2]!["priceMinor"]!.GetValue<long>());
        Assert.Equal("TRY", plans["currency"].Str());

        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var trial = await owner.Client.GetAsync($"/api/t/{tenant}/billing").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Trialing", trial["subscription"]!["status"].Str());
        Assert.Equal(3, trial["entitlements"]!["seatLimit"].Int());
        Assert.False(trial["entitlements"]!["exportEnabled"]!.GetValue<bool>());
        Assert.Equal(env.Clock.GetUtcNow().AddDays(14), trial["subscription"]!["trialEndsAt"]!.GetValue<DateTimeOffset>());

        // Plan değişince entitlement bir sonraki istekte güncel okunur (önbellek yok).
        await World.SetPlanDirectAsync(tenant, "team");
        var team = await owner.Client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(25, team["entitlements"]!["seatLimit"].Int());
        Assert.Equal(2000, team["entitlements"]!["monthlyTicketLimit"].Int());

        // Faturalama özeti Owner/Admin'e; Agent ve Viewer göremez.
        var agent = await World.CreateMemberAsync(tenant, TenantRole.Agent);
        await agent.Client.GetAsync($"/api/t/{tenant}/billing").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Downgrade_OverLimit_KeepsData_BlocksNewSeats()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        await World.SetPlanDirectAsync(tenant, "team");
        var members = new List<TestUser>();
        for (var i = 0; i < 5; i++)
        {
            members.Add(await World.InviteMemberAsync(owner, tenant, TenantRole.Agent, $"Üye {i}"));
        }
        var customer = await World.CreateCustomerAsync(owner.Client, tenant, "Kalıcı Müşteri");
        var before = await env.AdminScalarAsync<string>(
            "SELECT (SELECT count(*) FROM app.memberships WHERE tenant_id = $1 AND status = 'Active') || ':' || (SELECT count(*) FROM app.customers WHERE tenant_id = $1)", tenant);

        // Team (25 koltuk) → Starter (5 koltuk): 6 aktif üye var.
        await World.SetPlanDirectAsync(tenant, "starter");

        // Hiçbir veri silinmedi; limit üstündeki üyeler çalışmaya devam eder.
        Assert.Equal(before, await env.AdminScalarAsync<string>(
            "SELECT (SELECT count(*) FROM app.memberships WHERE tenant_id = $1 AND status = 'Active') || ':' || (SELECT count(*) FROM app.customers WHERE tenant_id = $1)", tenant));
        await members[4].Client.GetAsync($"/api/t/{tenant}/customers/{customer["id"].Str()}").ShouldBeAsync(HttpStatusCode.OK);
        await World.CreateCustomerAsync(members[4].Client, tenant, "Limit üstü üyenin müşterisi");
        var summary = await owner.Client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(6, summary["usage"]!["seatsUsed"].Int());
        Assert.Equal(5, summary["entitlements"]!["seatLimit"].Int());

        // Yeni davet reddedilir; limit altına inilince yeniden mümkün olur.
        var blocked = await owner.Client.PostAsync($"/api/t/{tenant}/invites", new { email = World.UniqueEmail(), role = "Viewer" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "quota_exceeded");
        Assert.Equal("seats", blocked["metric"].Str());
        await owner.Client.DeleteAsync($"/api/t/{tenant}/members/{members[0].Id}").ShouldBeAsync(HttpStatusCode.NoContent);
        await owner.Client.PostAsync($"/api/t/{tenant}/invites", new { email = World.UniqueEmail(), role = "Viewer" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "quota_exceeded");
        await owner.Client.DeleteAsync($"/api/t/{tenant}/members/{members[1].Id}").ShouldBeAsync(HttpStatusCode.NoContent);
        await owner.Client.PostAsync($"/api/t/{tenant}/invites", new { email = World.UniqueEmail(), role = "Viewer" }).ShouldBeAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Accept_AfterDowngrade_BlockedBySeatLimit()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        await World.SetPlanDirectAsync(tenant, "starter"); // 5 koltuk
        for (var i = 0; i < 2; i++)
        {
            await World.InviteMemberAsync(owner, tenant, TenantRole.Agent);
        }
        var late = await World.CreateUserAsync("Geç Kabul Eden");
        await World.InviteAsync(owner, tenant, late.Email, TenantRole.Agent);
        var token = await World.InviteTokenAsync(late);

        // Davet verildiğinde yer vardı (3 üye + 1 davet ≤ 5). Kabulden önce plan Trial'a (3 koltuk) düşer.
        await World.SetPlanDirectAsync(tenant, "trial", "Trialing");
        var problem = await late.Client.PostAsync("/api/invites/accept", new { token }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "quota_exceeded");
        Assert.Equal(3, problem["limit"].Int());
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.memberships WHERE tenant_id = $1 AND user_id = $2", tenant, late.Id));

        // Plan yükselince aynı davet kabul edilebilir.
        await World.SetPlanDirectAsync(tenant, "starter");
        await late.Client.PostAsync("/api/invites/accept", new { token }).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Downgrade_Racing_Create_UsesCommittedPlan()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        await World.SetPlanDirectAsync(tenant, "starter"); // 200 talep/ay
        var period = new DateOnly(env.Clock.GetUtcNow().Year, env.Clock.GetUtcNow().Month, 1);
        await env.AdminExecuteAsync("INSERT INTO app.usage_counters (tenant_id, metric, period_start, used) VALUES ($1, 'tickets_created', $2, 50)", tenant, period);

        // Plan değişikliğinin uygulanmasını canlandır: abonelik satırı FOR UPDATE kilitli, henüz commit edilmemiş.
        await using var planChange = await RoleSession.OpenAsync(env.Postgres.Worker());
        await planChange.ExecAsync("SELECT 1 FROM app.subscriptions WHERE tenant_id = $1 FOR UPDATE", tenant);

        // Talep oluşturma, limiti okumak için aynı satırı FOR SHARE ister: plan değişikliği bitene kadar bekler.
        var create = owner.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "Yarıştaki talep", description = "x" }, ("Idempotency-Key", "yaristaki"));
        await WaitUntilBlockedAsync("subscriptions");
        Assert.False(create.IsCompleted);

        // Düşürme commit edilir: Trial limiti (20) mevcut kullanımın (50) altında.
        await planChange.ExecAsync("UPDATE app.subscriptions SET plan_version_id = '018f1000-0000-7000-8000-000000000001', version = version + 1 WHERE tenant_id = $1", tenant);
        await planChange.CommitAsync();

        // Oluşturma, commit edilmiş yeni plana göre değerlendirilir: eski (200) limitle "kaçamaz".
        var response = await create.WaitAsync(TimeSpan.FromSeconds(20));
        var problem = await Task.FromResult(response).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "quota_exceeded");
        Assert.Equal(20, problem["limit"].Int());
        Assert.Equal(50, await World.TicketUsageAsync(tenant));
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.tickets WHERE tenant_id = $1", tenant));

        // Ters sıra: oluşturma önce commit olursa eski planla geçerlidir; sonradan düşürme onu geri almaz.
        await World.SetPlanDirectAsync(tenant, "starter");
        await World.CreateTicketAsync(owner.Client, tenant, customerId, "Düşürmeden önce");
        await World.SetPlanDirectAsync(tenant, "trial", "Trialing");
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.tickets WHERE tenant_id = $1", tenant));
        Assert.Equal(51, await World.TicketUsageAsync(tenant));
    }

    private async Task WaitUntilBlockedAsync(string relation)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var waiting = await env.AdminScalarAsync<long>(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%' || $1 || '%'", relation);
            if (waiting > 0)
            {
                return;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"Hiçbir oturum '{relation}' kilidinde beklemeye geçmedi.");
    }
}

public sealed class AccessModeTests(TestEnvironment env)
{
    private World World => env.World;

    /// <summary>Zamanı gelen tüm abonelik geçişlerini uygular (paylaşılan veritabanında başka testlerin şirketleri de olabilir).</summary>
    private async Task RunTimersAsync()
    {
        var timers = env.App.Services.GetRequiredService<SubscriptionTimers>();
        for (var round = 0; round < 50 && await timers.RunOnceAsync(CancellationToken.None) > 0; round++)
        {
        }
    }

    private async Task<ApiClient> ReloginAsync(TestUser user)
    {
        var client = env.App.NewClient();
        await client.LoginAsync(user.Email, user.Password).ShouldBeAsync(HttpStatusCode.OK);
        return client;
    }

    [Fact]
    public async Task TrialExpiry_PastDue_ThenSuspended_ByTimers_WithOwnerMail()
    {
        var (owner, tenant, _) = await World.CreateTenantWithCustomerAsync();
        await RunTimersAsync();
        Assert.Equal(0L, await CountChangedAsync(tenant)); // Süresi gelmeden geçiş yok.

        // Trial biter → PastDue (ödemesiz dönem): erişim tam, uyarı var.
        env.Clock.Advance(TimeSpan.FromDays(14) + TimeSpan.FromSeconds(1));
        await RunTimersAsync();
        var ownerClient = await ReloginAsync(owner);
        var pastDue = await ownerClient.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("PastDue", pastDue["subscription"]!["status"].Str());
        Assert.Equal("Full", pastDue["accessMode"].Str());
        Assert.NotNull(pastDue["subscription"]!["graceEndsAt"]);
        await World.CreateCustomerAsync(ownerClient, tenant, "Ödemesiz dönemde eklenen");

        // Zamanlayıcı yinelenebilir: tekrar çalışınca yeni geçiş/ileti üretmez.
        Assert.Equal(1L, await CountChangedAsync(tenant));
        await RunTimersAsync();
        Assert.Equal(1L, await CountChangedAsync(tenant));

        // Ödemesiz dönem biter → Suspended: salt-okunur + kurtarma.
        env.Clock.Advance(TimeSpan.FromDays(7));
        await RunTimersAsync();
        ownerClient = await ReloginAsync(owner);
        var suspended = await ownerClient.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Suspended", suspended["subscription"]!["status"].Str());
        Assert.Equal("Recovery", suspended["accessMode"].Str());

        await env.App.DrainOutboxAsync();
        var kinds = (await ownerClient.GetAsync("/api/me/mailbox").ShouldBeAsync(HttpStatusCode.OK))["items"]!.AsArray().Count(m => m!["kind"].Str() == "subscription_changed");
        Assert.Equal(2, kinds);

        Task<long> CountChangedAsync(Guid t) => env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'subscription.status_changed'", t);
    }

    [Fact]
    public async Task Suspended_ReadOnly_OwnerCanRecover_ViewerCanRead()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var agent = await World.CreateMemberAsync(tenant, TenantRole.Agent);
        var viewer = await World.CreateMemberAsync(tenant, TenantRole.Viewer);
        var ticketId = (await World.CreateTicketAsync(owner.Client, tenant, customerId))["id"].Guid();
        await World.SetPlanDirectAsync(tenant, "starter", "Suspended");

        // Giriş ve diğer şirketler etkilenmez; şirket özeti durumu ve modu söyler.
        var summary = await viewer.Client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Recovery", summary["accessMode"].Str());
        Assert.DoesNotContain("customers.write", summary["permissions"]!.AsArray().Select(p => p.Str()));

        // Okuma: tüm roller.
        foreach (var member in new[] { owner, agent, viewer })
        {
            await member.Client.GetAsync($"/api/t/{tenant}/customers").ShouldBeAsync(HttpStatusCode.OK);
            await member.Client.GetAsync($"/api/t/{tenant}/tickets/{ticketId}").ShouldBeAsync(HttpStatusCode.OK);
            await member.Client.GetAsync($"/api/t/{tenant}/tickets/{ticketId}/timeline").ShouldBeAsync(HttpStatusCode.OK);
        }

        // İçerik yazma: Owner dahil herkese kapalı; neden açıkça döner.
        var write = await owner.Client.PostAsync($"/api/t/{tenant}/customers", new { name = "Askıdayken" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "access_mode_restricted");
        Assert.Equal("Suspended", write["subscriptionStatus"].Str());
        Assert.Equal("Recovery", write["accessMode"].Str());
        await agent.Client.PostAsync($"/api/t/{tenant}/tickets/{ticketId}/comments", new { body = "Askıdayken" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "access_mode_restricted");
        await agent.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "x", description = "x" }, ("Idempotency-Key", "aski")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "access_mode_restricted");
        await owner.Client.PostAsync($"/api/t/{tenant}/invites", new { email = World.UniqueEmail(), role = "Viewer" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "access_mode_restricted");
        await viewer.Client.PostAsync($"/api/t/{tenant}/customers", new { name = "Viewer" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Kurtarma tek global yasakla kilitlenmez: Owner faturalamayı görür ve üye kaldırabilir; üyeler ayrılabilir.
        var billing = await owner.Client.GetAsync($"/api/t/{tenant}/billing").ShouldBeAsync(HttpStatusCode.OK);
        Assert.True(billing["canManage"]!.GetValue<bool>());
        await owner.Client.DeleteAsync($"/api/t/{tenant}/members/{agent.Id}").ShouldBeAsync(HttpStatusCode.NoContent);
        await viewer.Client.PostAsync($"/api/t/{tenant}/members/leave").ShouldBeAsync(HttpStatusCode.NoContent);

        // Abonelik yeniden etkinleşince yazma geri gelir (veri yerinde).
        await World.SetPlanDirectAsync(tenant, "starter");
        await World.CreateCustomerAsync(owner.Client, tenant, "Kurtarmadan sonra");
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.customers WHERE tenant_id = $1 AND name = 'Askıdayken'", tenant));
    }
}
