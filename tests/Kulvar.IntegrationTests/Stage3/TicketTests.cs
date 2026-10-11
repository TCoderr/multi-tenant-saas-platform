using System.Net;
using System.Text.Json.Nodes;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Platform.Tenancy;

namespace Kulvar.IntegrationTests.Stage3;

internal static class TicketApi
{
    public static async Task<JsonObject> GetAsync(ApiClient client, Guid tenant, Guid id) =>
        await client.GetAsync($"/api/t/{tenant}/tickets/{id}").ShouldBeAsync(HttpStatusCode.OK);

    public static (string, string) IfMatch(JsonObject ticket) => ("If-Match", $"\"{ticket["version"].Int()}\"");

    public static async Task<JsonObject> TransitionAsync(ApiClient client, Guid tenant, Guid id, string action)
    {
        var current = await GetAsync(client, tenant, id);
        return await client.PostAsync($"/api/t/{tenant}/tickets/{id}/transition", new { action }, IfMatch(current)).ShouldBeAsync(HttpStatusCode.OK);
    }

    public static async Task<HttpResponseMessage> TryTransitionAsync(ApiClient client, Guid tenant, Guid id, string action, ApiClient? readAs = null)
    {
        var current = await GetAsync(readAs ?? client, tenant, id);
        return await client.PostAsync($"/api/t/{tenant}/tickets/{id}/transition", new { action }, IfMatch(current));
    }

    public static async Task<HttpResponseMessage> TryAssignAsync(ApiClient client, Guid tenant, Guid id, Guid? userId, ApiClient? readAs = null)
    {
        var current = await GetAsync(readAs ?? client, tenant, id);
        return await client.PutAsync($"/api/t/{tenant}/tickets/{id}/assignee", new { userId }, IfMatch(current));
    }
}

public sealed class TicketLifecycleTests(TestEnvironment env)
{
    private World World => env.World;

    [Fact]
    public async Task FullLifecycle_WithTimeline()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var agent = await World.CreateMemberAsync(tenant, TenantRole.Agent, "Aslı Agent");
        var created = await World.CreateTicketAsync(owner.Client, tenant, customerId, "Fatura düzeltme", "Vergi numarası hatalı.", "Urgent");
        var id = created["id"].Guid();
        Assert.Equal(1, created["number"].Int());
        Assert.Equal("Open", created["status"].Str());

        // Her adım arasında saat ilerler: zaman çizelgesi sırası deterministik olur.
        void Tick() => env.Clock.Advance(TimeSpan.FromSeconds(1));

        // Agent üstlenir: atanan yokken "start" talebi aktöre atar.
        Tick();
        var started = await TicketApi.TransitionAsync(agent.Client, tenant, id, "Start");
        Assert.Equal("InProgress", started["status"].Str());
        Assert.Equal(agent.Id, started["assigneeUserId"].Guid());
        Assert.Equal("Aslı Agent", started["assigneeName"].Str());

        Tick();
        await agent.Client.PostAsync($"/api/t/{tenant}/tickets/{id}/comments", new { body = "Müşteriyle görüştüm." }).ShouldBeAsync(HttpStatusCode.Created);
        Tick();
        var resolved = await TicketApi.TransitionAsync(agent.Client, tenant, id, "Resolve");
        Assert.NotNull(resolved["resolvedAt"]);
        Tick();
        var closed = await TicketApi.TransitionAsync(agent.Client, tenant, id, "Close");
        Assert.Equal("Closed", closed["status"].Str());
        Assert.Empty(closed["allowedActions"]!.AsArray()); // Agent kapalı talebi yeniden açamaz.

        // Kapalı talep: yorum ve atama yok; Agent yeniden açamaz, Owner açabilir.
        await agent.Client.PostAsync($"/api/t/{tenant}/tickets/{id}/comments", new { body = "Geç kalan yorum" }).ShouldBeProblemAsync(HttpStatusCode.Conflict, "ticket_closed");
        await TicketApi.TryAssignAsync(owner.Client, tenant, id, owner.Id).ShouldBeProblemAsync(HttpStatusCode.Conflict, "ticket_closed");
        await TicketApi.TryTransitionAsync(agent.Client, tenant, id, "Reopen").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Tick();
        var reopened = await TicketApi.TransitionAsync(owner.Client, tenant, id, "Reopen");
        Assert.Equal("Open", reopened["status"].Str());
        Assert.Equal(1, reopened["reopenCount"].Int());
        Assert.Null(reopened["closedAt"]);
        Assert.Null(reopened["resolvedAt"]);

        var timeline = await owner.Client.GetAsync($"/api/t/{tenant}/tickets/{id}/timeline").ShouldBeAsync(HttpStatusCode.OK);
        var kinds = timeline["items"]!.AsArray().Select(i => i!["kind"].Str() == "comment" ? "comment" : i["action"].Str()).ToList();
        Assert.Equal(["ticket.created", "ticket.status_changed", "comment", "ticket.status_changed", "ticket.status_changed", "ticket.status_changed"], kinds);
        Assert.Equal("Aslı Agent", timeline["items"]!.AsArray().First(i => i!["kind"].Str() == "comment")!["authorName"].Str());
    }

    [Fact]
    public async Task InvalidTransitions_409_AndReopenWindowExpires()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var id = (await World.CreateTicketAsync(owner.Client, tenant, customerId))["id"].Guid();

        await TicketApi.TryTransitionAsync(owner.Client, tenant, id, "Close").ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid_transition");
        await TicketApi.TryTransitionAsync(owner.Client, tenant, id, "Release").ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid_transition");
        await TicketApi.TryTransitionAsync(owner.Client, tenant, id, "Reopen").ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid_transition");
        var current = await TicketApi.GetAsync(owner.Client, tenant, id);
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets/{id}/transition", new { action = "Explode" }, TicketApi.IfMatch(current)).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "bad_request");

        await TicketApi.TransitionAsync(owner.Client, tenant, id, "Resolve"); // Open → Resolved doğrudan
        await TicketApi.TransitionAsync(owner.Client, tenant, id, "Close");

        env.Clock.Advance(TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));
        var fresh = env.App.NewClient();
        await fresh.LoginAsync(owner.Email, owner.Password).ShouldBeAsync(HttpStatusCode.OK); // Oturum 12 saatlik; yeniden giriş.
        await TicketApi.TryTransitionAsync(fresh, tenant, id, "Reopen").ShouldBeProblemAsync(HttpStatusCode.Conflict, "reopen_window_expired");
        Assert.Equal("Closed", (await TicketApi.GetAsync(fresh, tenant, id))["status"].Str());
    }
}

public sealed class TicketAuthorizationTests(TestEnvironment env)
{
    private World World => env.World;

    [Fact]
    public async Task Agent_OnlyOwnOrUnassigned_Viewer_ReadOnly()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var agentOne = await World.CreateMemberAsync(tenant, TenantRole.Agent, "Agent Bir");
        var agentTwo = await World.CreateMemberAsync(tenant, TenantRole.Agent, "Agent İki");
        var viewer = await World.CreateMemberAsync(tenant, TenantRole.Viewer, "Viewer");
        var mine = (await World.CreateTicketAsync(owner.Client, tenant, customerId, "Agent Bir'in", assigneeUserId: agentOne.Id))["id"].Guid();
        var unassigned = (await World.CreateTicketAsync(owner.Client, tenant, customerId, "Sahipsiz"))["id"].Guid();

        // Agent İki, Agent Bir'in talebini okuyabilir ama değiştiremez.
        var seen = await TicketApi.GetAsync(agentTwo.Client, tenant, mine);
        Assert.False(seen["canModify"]!.GetValue<bool>());
        Assert.Empty(seen["allowedActions"]!.AsArray());
        await agentTwo.Client.PatchAsync($"/api/t/{tenant}/tickets/{mine}", new { title = "Ele geçirildi" }, TicketApi.IfMatch(seen)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await TicketApi.TryTransitionAsync(agentTwo.Client, tenant, mine, "Start").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await agentTwo.Client.PostAsync($"/api/t/{tenant}/tickets/{mine}/comments", new { body = "Araya girdim" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await TicketApi.TryAssignAsync(agentTwo.Client, tenant, mine, agentTwo.Id).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Atanmamış talebi kendine alabilir, başkasına veremez; kendi talebini bırakabilir ama devredemez.
        await TicketApi.TryAssignAsync(agentTwo.Client, tenant, unassigned, agentOne.Id).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await TicketApi.TryAssignAsync(agentTwo.Client, tenant, unassigned, agentTwo.Id).ShouldBeAsync(HttpStatusCode.OK);
        await TicketApi.TryAssignAsync(agentTwo.Client, tenant, unassigned, agentOne.Id).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await TicketApi.TryAssignAsync(agentTwo.Client, tenant, unassigned, null).ShouldBeAsync(HttpStatusCode.OK);
        // Agent talep açarken başkasına atayamaz.
        await agentTwo.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "Başkasına", description = "x", assigneeUserId = agentOne.Id }, ("Idempotency-Key", Guid.NewGuid().ToString()))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Viewer: okur, hiçbir şey yazamaz.
        await TicketApi.GetAsync(viewer.Client, tenant, mine);
        await viewer.Client.GetAsync($"/api/t/{tenant}/tickets/{mine}/timeline").ShouldBeAsync(HttpStatusCode.OK);
        await viewer.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "Viewer", description = "x" }, ("Idempotency-Key", Guid.NewGuid().ToString()))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await TicketApi.TryTransitionAsync(viewer.Client, tenant, unassigned, "Start").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await viewer.Client.PostAsync($"/api/t/{tenant}/tickets/{unassigned}/comments", new { body = "Viewer yorumu" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await TicketApi.TryAssignAsync(viewer.Client, tenant, unassigned, viewer.Id).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Owner her talebi değiştirir.
        await TicketApi.TryAssignAsync(owner.Client, tenant, mine, agentTwo.Id).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Assignee_MustBeActiveMember_OfSameTenant()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var viewer = await World.CreateMemberAsync(tenant, TenantRole.Viewer);
        var formerAgent = await World.CreateMemberAsync(tenant, TenantRole.Agent);
        var stranger = await World.CreateUserAsync("Yabancı"); // Global olarak var, bu şirkette üyeliği yok.
        var id = (await World.CreateTicketAsync(owner.Client, tenant, customerId))["id"].Guid();
        await owner.Client.DeleteAsync($"/api/t/{tenant}/members/{formerAgent.Id}").ShouldBeAsync(HttpStatusCode.NoContent);

        foreach (var candidate in new[] { stranger.Id, viewer.Id, formerAgent.Id, Guid.NewGuid() })
        {
            await TicketApi.TryAssignAsync(owner.Client, tenant, id, candidate).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        }
        Assert.Null((await TicketApi.GetAsync(owner.Client, tenant, id))["assigneeUserId"]);
    }

    [Fact]
    public async Task MemberRemoval_UnassignsTheirTickets_InSameTransaction()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var agent = await World.CreateMemberAsync(tenant, TenantRole.Agent, "Ayrılan Agent");
        var first = (await World.CreateTicketAsync(owner.Client, tenant, customerId, "Bir", assigneeUserId: agent.Id))["id"].Guid();
        var second = (await World.CreateTicketAsync(owner.Client, tenant, customerId, "İki", assigneeUserId: agent.Id))["id"].Guid();
        await agent.Client.PostAsync($"/api/t/{tenant}/tickets/{first}/comments", new { body = "Ayrılmadan önceki yorum" }).ShouldBeAsync(HttpStatusCode.Created);

        await owner.Client.DeleteAsync($"/api/t/{tenant}/members/{agent.Id}").ShouldBeAsync(HttpStatusCode.NoContent);

        foreach (var id in new[] { first, second })
        {
            var ticket = await TicketApi.GetAsync(owner.Client, tenant, id);
            Assert.Null(ticket["assigneeUserId"]);
            Assert.Equal(2, ticket["version"].Int());
        }
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.tickets WHERE tenant_id = $1 AND assignee_user_id = $2", tenant, agent.Id));
        // Geçmiş korunur: ayrılan üyenin yorumu ve adı zaman çizelgesinde durur.
        var timeline = await owner.Client.GetAsync($"/api/t/{tenant}/tickets/{first}/timeline").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Contains(timeline["items"]!.AsArray(), i => i!["kind"].Str() == "comment" && i["authorName"].Str() == "Ayrılan Agent");
        // Ayrılan üye artık hiçbir talebe ulaşamaz.
        await agent.Client.GetAsync($"/api/t/{tenant}/tickets/{first}").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
    }
}

public sealed class TicketCommentTests(TestEnvironment env)
{
    private World World => env.World;

    [Fact]
    public async Task HtmlBody_StoredAndReturnedAsText()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var id = (await World.CreateTicketAsync(owner.Client, tenant, customerId, "<b>Başlık</b>", "<img src=x onerror=alert(1)>"))["id"].Guid();
        const string Payload = "<script>alert('xss')</script> & \"tırnak\" <a href=\"javascript:alert(1)\">bağlantı</a>";

        var created = await owner.Client.PostAsync($"/api/t/{tenant}/tickets/{id}/comments", new { body = Payload });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/json", created.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Payload, (await created.JsonAsync())["text"].Str());

        // Olduğu gibi saklanır (temizleme/yorumlama yok) ve yalnız JSON metin alanı olarak döner.
        Assert.Equal(Payload, await env.AdminScalarAsync<string>("SELECT body FROM app.ticket_comments WHERE ticket_id = $1", id));
        var timeline = await owner.Client.GetAsync($"/api/t/{tenant}/tickets/{id}/timeline");
        Assert.Equal("application/json", timeline.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", timeline.Headers.GetValues("X-Content-Type-Options").Single());
        var comment = (await timeline.JsonAsync())["items"]!.AsArray().Single(i => i!["kind"].Str() == "comment")!;
        Assert.Equal(Payload, comment["text"].Str());
        // JSON çıktısında ham "<script" dizisi yer almaz (System.Text.Json < > & karakterlerini kaçırır).
        Assert.DoesNotContain("<script", await (await owner.Client.GetAsync($"/api/t/{tenant}/tickets/{id}/timeline")).Content.ReadAsStringAsync());

        var ticket = await TicketApi.GetAsync(owner.Client, tenant, id);
        Assert.Equal("<b>Başlık</b>", ticket["title"].Str());
        Assert.Equal("<img src=x onerror=alert(1)>", ticket["description"].Str());
    }

    [Fact]
    public async Task Comment_Validation_AndDoesNotBumpTicketVersion()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var id = (await World.CreateTicketAsync(owner.Client, tenant, customerId))["id"].Guid();

        await owner.Client.PostAsync($"/api/t/{tenant}/tickets/{id}/comments", new { body = "   " }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets/{id}/comments", new { body = new string('a', 5001) }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets/{id}/comments", new { body = "Geçerli yorum" }).ShouldBeAsync(HttpStatusCode.Created);

        // Yorum, başkasının açık düzenlemesini geçersiz kılmaz.
        await owner.Client.PatchAsync($"/api/t/{tenant}/tickets/{id}", new { title = "Yorumdan sonra düzenleme" }, ("If-Match", "\"1\"")).ShouldBeAsync(HttpStatusCode.OK);
    }
}
