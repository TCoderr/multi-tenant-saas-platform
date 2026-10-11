using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Platform.Tenancy;

namespace Kulvar.IntegrationTests.Stage4;

public sealed class MailboxTests(TestEnvironment env)
{
    private World World => env.World;

    [Fact]
    public async Task OnlyRecipient_SeesMessage_AdminDoesNot()
    {
        var owner = await World.CreateUserAsync("Sahip");
        var tenant = await World.CreateTenantAsync(owner);
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin);
        var invitee = await World.CreateUserAsync("Davetli");
        var outsider = await World.CreateUserAsync("Dışarıdan");
        await World.InviteAsync(owner, tenant, invitee.Email, TenantRole.Agent);
        var token = await World.InviteTokenAsync(invitee);

        var inviteeBox = await invitee.Client.GetAsync("/api/me/mailbox").ShouldBeAsync(HttpStatusCode.OK);
        var messageId = inviteeBox["items"]!.AsArray().Single(m => m!["kind"].Str() == "invite")!["id"].Str();

        // Şirketin sahibi ve yöneticisi, başka şirketin kullanıcısı: davet iletisini listede göremez, kimliğiyle açamaz.
        foreach (var other in new[] { owner, admin, outsider })
        {
            var box = await (await other.Client.GetAsync("/api/me/mailbox")).Content.ReadAsStringAsync();
            Assert.DoesNotContain(messageId, box);
            Assert.DoesNotContain("\"kind\":\"invite\"", box);
            var direct = await other.Client.GetAsync($"/api/me/mailbox/{messageId}");
            Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
            Assert.DoesNotContain(token, await direct.Content.ReadAsStringAsync());
        }
        // Posta kutusu şirket uçlarından da erişilemez (şirket kapsamlı bir "tüm iletiler" ucu yoktur).
        await owner.Client.GetAsync($"/api/t/{tenant}/mailbox").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task RedeliveredOutbox_ProducesSingleMessage()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var invitee = await World.CreateUserAsync();
        var inviteId = await World.InviteAsync(owner, tenant, invitee.Email, TenantRole.Agent);
        var token = await World.InviteTokenAsync(invitee);

        // Aynı mesajların yeniden teslim edildiğini canlandır: tamamlanmış mesajlar yeniden kuyruğa alınır (kurulum, superuser).
        var requeued = await env.AdminExecuteAsync(
            "UPDATE app.outbox_messages SET status = 'Pending', processed_at = NULL, available_at = $2 WHERE tenant_id = $1 OR user_id = $3",
            tenant, env.Clock.GetUtcNow(), invitee.Id);
        Assert.True(requeued >= 2);
        await env.App.DrainOutboxAsync();
        await env.App.DrainOutboxAsync();

        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.sink_messages WHERE tenant_id = $1 AND kind = 'invite'", tenant));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.sink_messages WHERE recipient_normalized_email = $1 AND kind = 'email_verification'", invitee.Email));
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.outbox_messages WHERE (tenant_id = $1 OR user_id = $2) AND status <> 'Done'", tenant, invitee.Id));
        // Token değişmedi: ilk iletideki bağlantı hâlâ geçerli.
        Assert.Equal(token, await World.LatestMailTokenAsync(invitee.Client, "invite"));
        await invitee.Client.PostAsync("/api/invites/accept", new { token }).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Accepted", await env.AdminScalarAsync<string>("SELECT status FROM app.invites WHERE id = $1", inviteId));
    }
}

public sealed class OwnershipTests(TestEnvironment env)
{
    private World World => env.World;

    private Task<long> OwnerCountAsync(Guid tenant) =>
        env.AdminScalarAsync<long>("SELECT count(*) FROM app.memberships WHERE tenant_id = $1 AND role = 'Owner' AND status = 'Active'", tenant);

    private Task<string?> RoleAsync(Guid tenant, Guid user) =>
        env.AdminScalarAsync<string>("SELECT role || ':' || status FROM app.memberships WHERE tenant_id = $1 AND user_id = $2", tenant, user);

    private static async Task<Guid> OfferAsync(TestUser from, Guid tenant, TestUser to, bool demoteSelf = true) =>
        (await from.Client.PostAsync($"/api/t/{tenant}/ownership-offers", new { toUserId = to.Id, demoteSelf }).ShouldBeAsync(HttpStatusCode.Created))["id"].Guid();

    [Fact]
    public async Task Offer_RequiresTargetAcceptance_Atomic()
    {
        var owner = await World.CreateUserAsync("Devreden");
        var tenant = await World.CreateTenantAsync(owner);
        var target = await World.CreateMemberAsync(tenant, TenantRole.Agent, "Devralan");
        var bystander = await World.CreateMemberAsync(tenant, TenantRole.Admin, "Seyirci");

        var offerId = await OfferAsync(owner, tenant, target);
        // Teklif tek başına hiçbir rolü değiştirmez.
        Assert.Equal("Agent:Active", await RoleAsync(tenant, target.Id));
        Assert.Equal(1L, await OwnerCountAsync(tenant));

        // Teklifi yalnız tarafları görür; hedef olmayan kabul edemez.
        Assert.Empty((await bystander.Client.GetAsync($"/api/t/{tenant}/ownership-offers").ShouldBeAsync(HttpStatusCode.OK))["items"]!.AsArray());
        await bystander.Client.PostAsync($"/api/t/{tenant}/ownership-offers/{offerId}/accept").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await owner.Client.PostAsync($"/api/t/{tenant}/ownership-offers/{offerId}/accept").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        var visible = await target.Client.GetAsync($"/api/t/{tenant}/ownership-offers").ShouldBeAsync(HttpStatusCode.OK);
        Assert.True(visible["items"]![0]!["canAccept"]!.GetValue<bool>());

        // Hedefe bildirim iletisi.
        await env.App.DrainOutboxAsync();
        Assert.Contains((await target.Client.GetAsync("/api/me/mailbox").ShouldBeAsync(HttpStatusCode.OK))["items"]!.AsArray(), m => m!["kind"].Str() == "ownership_offer");

        await target.Client.PostAsync($"/api/t/{tenant}/ownership-offers/{offerId}/accept").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Owner:Active", await RoleAsync(tenant, target.Id));
        Assert.Equal("Admin:Active", await RoleAsync(tenant, owner.Id)); // Devir: eski sahip aynı transaction'da Admin oldu.
        Assert.Equal(1L, await OwnerCountAsync(tenant));

        // Eski sahip artık Owner yetkisi kullanamaz; teklif tekrar kullanılamaz.
        var workspace = await owner.Client.GetAsync($"/api/t/{tenant}/");
        await owner.Client.PatchAsync($"/api/t/{tenant}/settings", new { name = "Eski Sahip" }, ("If-Match", workspace.ETag())).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await target.Client.PostAsync($"/api/t/{tenant}/ownership-offers/{offerId}/accept").ShouldBeProblemAsync(HttpStatusCode.Conflict, "offer_not_pending");
    }

    [Fact]
    public async Task CoOwnerOffer_KeepsBothOwners_AndThenEitherCanLeave()
    {
        var first = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(first);
        var second = await World.CreateMemberAsync(tenant, TenantRole.Viewer); // Viewer da hedef olabilir.

        var offerId = await OfferAsync(first, tenant, second, demoteSelf: false);
        await second.Client.PostAsync($"/api/t/{tenant}/ownership-offers/{offerId}/accept").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(2L, await OwnerCountAsync(tenant));

        await first.Client.PostAsync($"/api/t/{tenant}/members/leave").ShouldBeAsync(HttpStatusCode.NoContent);
        await second.Client.PostAsync($"/api/t/{tenant}/members/leave").ShouldBeProblemAsync(HttpStatusCode.Conflict, "last_owner");
        Assert.Equal(1L, await OwnerCountAsync(tenant));
    }

    [Fact]
    public async Task OfferRules_OnlyOwner_SinglePending_Expiry_Withdraw()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin);
        var agent = await World.CreateMemberAsync(tenant, TenantRole.Agent);
        var stranger = await World.CreateUserAsync();

        await admin.Client.PostAsync($"/api/t/{tenant}/ownership-offers", new { toUserId = agent.Id, demoteSelf = false }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await owner.Client.PostAsync($"/api/t/{tenant}/ownership-offers", new { toUserId = owner.Id }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        await owner.Client.PostAsync($"/api/t/{tenant}/ownership-offers", new { toUserId = stranger.Id }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");

        var offerId = await OfferAsync(owner, tenant, admin);
        await owner.Client.PostAsync($"/api/t/{tenant}/ownership-offers", new { toUserId = agent.Id }).ShouldBeProblemAsync(HttpStatusCode.Conflict, "offer_already_pending");

        // Hedef reddeder → yeni teklif verilebilir; teklif eden iptal eder.
        await admin.Client.DeleteAsync($"/api/t/{tenant}/ownership-offers/{offerId}").ShouldBeAsync(HttpStatusCode.NoContent);
        await admin.Client.PostAsync($"/api/t/{tenant}/ownership-offers/{offerId}/accept").ShouldBeProblemAsync(HttpStatusCode.Conflict, "offer_not_pending");
        var second = await OfferAsync(owner, tenant, agent);
        await owner.Client.DeleteAsync($"/api/t/{tenant}/ownership-offers/{second}").ShouldBeAsync(HttpStatusCode.NoContent);

        // Süresi dolan teklif kabul edilemez.
        var third = await OfferAsync(owner, tenant, agent);
        env.Clock.Advance(TimeSpan.FromHours(72) + TimeSpan.FromSeconds(1));
        var agentAgain = env.App.NewClient();
        await agentAgain.LoginAsync(agent.Email, agent.Password).ShouldBeAsync(HttpStatusCode.OK);
        await agentAgain.PostAsync($"/api/t/{tenant}/ownership-offers/{third}/accept").ShouldBeProblemAsync(HttpStatusCode.Conflict, "offer_not_pending");
        Assert.Equal(1L, await OwnerCountAsync(tenant));
        Assert.Equal("Agent:Active", await RoleAsync(tenant, agent.Id));
    }

    [Fact]
    public async Task Offer_BecomesVoid_WhenOffererLosesOwnership_OrTargetIsRemoved()
    {
        var first = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(first);
        var second = await World.CreateMemberAsync(tenant, TenantRole.Owner);
        var target = await World.CreateMemberAsync(tenant, TenantRole.Agent);

        // Teklif eden, kabulden önce başka bir Owner tarafından düşürülür.
        var offerId = await OfferAsync(first, tenant, target);
        var members = await second.Client.GetAsync($"/api/t/{tenant}/members").ShouldBeAsync(HttpStatusCode.OK);
        var version = members["items"]!.AsArray().Single(m => m!["userId"].Guid() == first.Id)!["version"].Int();
        await second.Client.PatchAsync($"/api/t/{tenant}/members/{first.Id}", new { role = "Admin" }, ("If-Match", $"\"{version}\"")).ShouldBeAsync(HttpStatusCode.OK);
        await target.Client.PostAsync($"/api/t/{tenant}/ownership-offers/{offerId}/accept").ShouldBeProblemAsync(HttpStatusCode.Conflict, "offer_not_pending");
        Assert.Equal("Agent:Active", await RoleAsync(tenant, target.Id));

        // Hedef kaldırılırsa açık teklif de kapanır.
        var another = await OfferAsync(second, tenant, target);
        await second.Client.DeleteAsync($"/api/t/{tenant}/members/{target.Id}").ShouldBeAsync(HttpStatusCode.NoContent);
        Assert.Equal("Cancelled", await env.AdminScalarAsync<string>("SELECT status FROM app.ownership_offers WHERE id = $1", another));
        Assert.Equal(1L, await OwnerCountAsync(tenant));
    }

    [Fact]
    public async Task TwoOwners_ParallelLeaveOrDemote_ExactlyOneSucceeds()
    {
        for (var round = 0; round < 4; round++)
        {
            var first = await World.CreateUserAsync();
            var tenant = await World.CreateTenantAsync(first);
            var second = await World.CreateMemberAsync(tenant, TenantRole.Owner);
            var members = await first.Client.GetAsync($"/api/t/{tenant}/members").ShouldBeAsync(HttpStatusCode.OK);
            string Tag(Guid user) => $"\"{members["items"]!.AsArray().Single(m => m!["userId"].Guid() == user)!["version"].Int()}\"";

            // Çift turlarda ikisi de ayrılır; tek turlarda birbirlerini düşürürler. Her serpiştirmede tam bir Owner kalmalıdır.
            using var gate = new SemaphoreSlim(0);
            Task<HttpResponseMessage> Act(TestUser actor, TestUser other) => Task.Run(async () =>
            {
                var client = actor.Client.Clone();
                await gate.WaitAsync();
                return round % 2 == 0
                    ? await client.PostAsync($"/api/t/{tenant}/members/leave")
                    : await client.PatchAsync($"/api/t/{tenant}/members/{other.Id}", new { role = "Admin" }, ("If-Match", Tag(other.Id)));
            });
            var a = Act(first, second);
            var b = Act(second, first);
            gate.Release(2);
            var results = await Task.WhenAll(a, b);

            Assert.Equal(1, results.Count(r => r.IsSuccessStatusCode));
            var loser = results.Single(r => !r.IsSuccessStatusCode);
            // Kaybeden: ya "son Owner" (409) ya da bu arada Owner olmaktan/üyelikten çıktığı için yetkisiz (403/404).
            Assert.Contains(loser.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
            Assert.Equal(1L, await OwnerCountAsync(tenant));
        }
    }

    [Fact]
    public async Task Accept_Races_WithRemoval_NeverZeroOwners()
    {
        for (var round = 0; round < 6; round++)
        {
            var owner = await World.CreateUserAsync();
            var tenant = await World.CreateTenantAsync(owner);
            var target = await World.CreateMemberAsync(tenant, TenantRole.Admin);
            var offerId = await OfferAsync(owner, tenant, target);

            using var gate = new SemaphoreSlim(0);
            var accept = Task.Run(async () =>
            {
                var client = target.Client.Clone();
                await gate.WaitAsync();
                return await client.PostAsync($"/api/t/{tenant}/ownership-offers/{offerId}/accept");
            });
            var remove = Task.Run(async () =>
            {
                var client = owner.Client.Clone();
                await gate.WaitAsync();
                return await client.DeleteAsync($"/api/t/{tenant}/members/{target.Id}");
            });
            gate.Release(2);
            var accepted = (await accept).IsSuccessStatusCode;
            var removed = (await remove).IsSuccessStatusCode;

            // İkisi birlikte başarılı olamaz: ya devir oldu (eski sahip artık Admin, yeni Owner'ı kaldıramaz) ya da hedef kaldırıldı (teklif hükümsüz).
            Assert.True(accepted ^ removed, $"tur {round}: accept={accepted} remove={removed}");
            Assert.Equal(1L, await OwnerCountAsync(tenant));
            Assert.Equal(accepted ? "Owner:Active" : "Admin:Removed", await RoleAsync(tenant, target.Id));
            Assert.Equal(accepted ? "Admin:Active" : "Owner:Active", await RoleAsync(tenant, owner.Id));
        }
    }
}

public sealed class MembershipRevocationTests(TestEnvironment env)
{
    private World World => env.World;

    [Fact]
    public async Task RemovedMember_CannotReplayIdempotentResponse()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var agent = await World.InviteMemberAsync(owner, tenant, TenantRole.Agent, "Ayrılacak Agent");
        var body = new { customerId, title = "Ayrılmadan önce", description = "x" };
        var key = ("Idempotency-Key", "ayrilmadan-once");

        var created = await agent.Client.PostAsync($"/api/t/{tenant}/tickets", body, key).ShouldBeAsync(HttpStatusCode.Created);
        // Üyeyken tekrar: kayıtlı yanıt.
        var replay = await agent.Client.PostAsync($"/api/t/{tenant}/tickets", body, key);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replay").Single());

        await owner.Client.DeleteAsync($"/api/t/{tenant}/members/{agent.Id}").ShouldBeAsync(HttpStatusCode.NoContent);

        // Üyelik kalktıktan sonra aynı anahtar, aynı (hâlâ geçerli) oturum: kayıtlı yanıt DÖNMEZ; şirket "yok".
        var afterRemoval = await agent.Client.PostAsync($"/api/t/{tenant}/tickets", body, key);
        var text = await afterRemoval.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, afterRemoval.StatusCode);
        Assert.Contains("tenant_not_found", text);
        Assert.DoesNotContain(created["id"].Str(), text);
        Assert.False(afterRemoval.Headers.Contains("Idempotent-Replay"));
        // İmleç gibi diğer "eski" yetkiler de işe yaramaz.
        await agent.Client.GetAsync($"/api/t/{tenant}/tickets/{created["id"].Str()}").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
    }

    [Fact]
    public async Task Removal_WaitsForInFlightWrite_ThenBlocksNext()
    {
        var (owner, tenant, _) = await World.CreateTenantWithCustomerAsync();
        var agent = await World.CreateMemberAsync(tenant, TenantRole.Agent);

        // Agent'ın devam eden bir yazma isteğini canlandır: şirket kapsamında açık transaction + kendi üyelik satırında FOR SHARE
        // (TenantExecutor yazma isteklerinde tam olarak bunu yapar) + henüz commit edilmemiş bir müşteri.
        var inFlight = await RoleSession.OpenAsync(env.Postgres.App(), agent.Id, tenant);
        await using (inFlight)
        {
            await inFlight.ExecAsync("SELECT role FROM app.memberships WHERE tenant_id = $1 AND user_id = $2 AND status = 'Active' FOR SHARE", tenant, agent.Id);
            await inFlight.ExecAsync(
                "INSERT INTO app.customers (id, tenant_id, name, created_by_user_id, created_at, updated_at, version) VALUES (gen_random_uuid(), $1, 'Yarıştaki Müşteri', $2, now(), now(), 1)",
                tenant, agent.Id);

            // Owner üyeliği kaldırmaya çalışır: üyelik satırını güncellemek zorundadır, yani devam eden yazmanın bitmesini bekler.
            var removal = owner.Client.DeleteAsync($"/api/t/{tenant}/members/{agent.Id}");
            await WaitUntilBlockedOnMembershipAsync();
            Assert.False(removal.IsCompleted);
            Assert.Equal("Agent:Active", await env.AdminScalarAsync<string>("SELECT role || ':' || status FROM app.memberships WHERE tenant_id = $1 AND user_id = $2", tenant, agent.Id));

            // Devam eden yazma tamamlanır → kaldırma ilerler.
            await inFlight.CommitAsync();
            await (await removal.WaitAsync(TimeSpan.FromSeconds(20))).ShouldBeNoContentAsync();
        }

        // Kaldırmadan önce başlamış yazma geçerlidir; sonraki istek artık reddedilir.
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.customers WHERE tenant_id = $1 AND name = 'Yarıştaki Müşteri'", tenant));
        await agent.Client.PostAsync($"/api/t/{tenant}/customers", new { name = "Geç Kalan" }).ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.customers WHERE tenant_id = $1 AND name = 'Geç Kalan'", tenant));
    }

    /// <summary>Rastgele bekleme yerine gözlem: bir backend üyelik satırı kilidini beklemeye başlayana kadar pg_locks yoklanır.</summary>
    private async Task WaitUntilBlockedOnMembershipAsync()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var waiting = await env.AdminScalarAsync<long>(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%memberships%'");
            if (waiting > 0)
            {
                return;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Kaldırma işlemi üyelik kilidinde beklemeye geçmedi.");
    }
}

internal static class RevocationAssertions
{
    public static Task ShouldBeNoContentAsync(this HttpResponseMessage response) => Task.FromResult(response).ShouldBeAsync(HttpStatusCode.NoContent);
}
