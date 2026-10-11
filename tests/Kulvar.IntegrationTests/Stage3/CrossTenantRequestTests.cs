using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Platform.Tenancy;

namespace Kulvar.IntegrationTests.Stage3;

/// <summary>İki bağımsız şirket; B'nin tüm içerik alanları kanarya değerleri taşır.</summary>
public sealed class CanaryTenants(TestEnvironment env) : IAsyncLifetime
{
    public const string CanaryB = "CANARY-B-7f3a";

    public TestUser OwnerA { get; private set; } = null!;
    public TestUser OwnerB { get; private set; } = null!;
    public TestUser AgentB { get; private set; } = null!;
    public Guid TenantA { get; private set; }
    public Guid TenantB { get; private set; }
    public Guid CustomerA { get; private set; }
    public Guid CustomerB { get; private set; }
    public Guid TicketA { get; private set; }
    public Guid TicketB { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var world = env.World;
        OwnerA = await world.CreateUserAsync("Ayşe A");
        OwnerB = await world.CreateUserAsync($"{CanaryB} Burak");
        TenantA = await world.CreateTenantAsync(OwnerA, "Akasya İzolasyon");
        TenantB = await world.CreateTenantAsync(OwnerB, $"{CanaryB} Şirket");
        AgentB = await world.CreateMemberAsync(TenantB, TenantRole.Agent, $"{CanaryB} Agent");

        CustomerA = (await World.CreateCustomerAsync(OwnerA.Client, TenantA, "A Müşterisi", "a.musteri@ornek.test", "+90 555 111 11 11"))["id"].Guid();
        TicketA = (await World.CreateTicketAsync(OwnerA.Client, TenantA, CustomerA, "A talebi", "A açıklaması"))["id"].Guid();

        CustomerB = (await World.CreateCustomerAsync(OwnerB.Client, TenantB, $"{CanaryB} Müşteri", "canary-b-7f3a@ornek.test", "+90 555 737 37 37", $"{CanaryB} Unvan", $"{CanaryB} not"))["id"].Guid();
        var ticketB = await World.CreateTicketAsync(OwnerB.Client, TenantB, CustomerB, $"{CanaryB} talep", $"{CanaryB} açıklama", assigneeUserId: AgentB.Id);
        TicketB = ticketB["id"].Guid();
        await OwnerB.Client.PostAsync($"/api/t/{TenantB}/tickets/{TicketB}/comments", new { body = $"{CanaryB} yorum" }).ShouldBeAsync(HttpStatusCode.Created);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>İstek düzeyi saldırılar: başka şirketin kimliklerini yola veya gövdeye koymak hiçbir yüzeyde veri açmaz/değiştirmez.</summary>
public sealed class CrossTenantRequestTests(TestEnvironment env, CanaryTenants t) : IClassFixture<CanaryTenants>
{
    private Task<string?> SnapshotBAsync() => env.AdminScalarAsync<string>(
        """
        SELECT md5(concat_ws('|',
          (SELECT string_agg(id::text || name || coalesce(email, '') || version::text, ',' ORDER BY id) FROM app.customers WHERE tenant_id = $1),
          (SELECT string_agg(id::text || title || status || coalesce(assignee_user_id::text, '') || version::text, ',' ORDER BY id) FROM app.tickets WHERE tenant_id = $1),
          (SELECT count(*)::text FROM app.ticket_comments WHERE tenant_id = $1),
          (SELECT count(*)::text FROM app.audit_events WHERE tenant_id = $1)))
        """, t.TenantB);

    [Fact]
    public async Task ForeignIds_InPathAndBody_Return404()
    {
        var before = await SnapshotBAsync();
        var a = t.OwnerA.Client;
        var ifMatch = ("If-Match", "\"1\"");

        // B'nin kaynak kimlikleri, A'nın yolunda: kaynak "yok".
        await a.GetAsync($"/api/t/{t.TenantA}/customers/{t.CustomerB}").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await a.PutAsync($"/api/t/{t.TenantA}/customers/{t.CustomerB}", new { name = "ele geçirildi" }, ifMatch).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await a.DeleteAsync($"/api/t/{t.TenantA}/customers/{t.CustomerB}").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await a.GetAsync($"/api/t/{t.TenantA}/tickets/{t.TicketB}").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await a.GetAsync($"/api/t/{t.TenantA}/tickets/{t.TicketB}/timeline").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await a.PatchAsync($"/api/t/{t.TenantA}/tickets/{t.TicketB}", new { title = "ele geçirildi" }, ifMatch).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await a.PostAsync($"/api/t/{t.TenantA}/tickets/{t.TicketB}/transition", new { action = "Resolve" }, ifMatch).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await a.PutAsync($"/api/t/{t.TenantA}/tickets/{t.TicketB}/assignee", new { userId = t.OwnerA.Id }, ifMatch).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await a.PostAsync($"/api/t/{t.TenantA}/tickets/{t.TicketB}/comments", new { body = "sızma" }).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await a.GetAsync($"/api/t/{t.TenantA}/tickets?customerId={t.CustomerB}").ShouldBeAsync(HttpStatusCode.OK);

        // B'nin şirket yolunda A'nın oturumu: şirket "yok".
        await a.GetAsync($"/api/t/{t.TenantB}/customers").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        await a.GetAsync($"/api/t/{t.TenantB}/tickets/{t.TicketB}").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        await a.PostAsync($"/api/t/{t.TenantB}/customers", new { name = "B'ye yazma" }).ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        await a.PostAsync($"/api/t/{t.TenantB}/tickets/{t.TicketB}/comments", new { body = "sızma" }).ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");

        // Gövdede B'nin kimlikleri: A'da talep, B'nin müşterisine veya B'nin üyesine bağlanamaz.
        await a.PostAsync($"/api/t/{t.TenantA}/tickets", new { customerId = t.CustomerB, title = "yabancı müşteri", description = "x" }, ("Idempotency-Key", Guid.NewGuid().ToString()))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await a.PostAsync($"/api/t/{t.TenantA}/tickets", new { customerId = t.CustomerA, title = "yabancı atanan", description = "x", assigneeUserId = t.AgentB.Id }, ("Idempotency-Key", Guid.NewGuid().ToString()))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        var ticketA = await a.GetAsync($"/api/t/{t.TenantA}/tickets/{t.TicketA}");
        await a.PutAsync($"/api/t/{t.TenantA}/tickets/{t.TicketA}/assignee", new { userId = t.AgentB.Id }, ("If-Match", ticketA.ETag()))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");

        Assert.Equal(before, await SnapshotBAsync());
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.tickets WHERE tenant_id = $1 AND title LIKE 'yabancı%'", t.TenantA));
    }

    [Fact]
    public async Task TenantId_InBody_IsRejected()
    {
        var a = t.OwnerA.Client;
        // Bilinmeyen alan sessizce yok sayılmaz; gövdeden şirket seçilemez.
        await a.PostAsync($"/api/t/{t.TenantA}/customers", new { name = "Gövdeden şirket", tenantId = t.TenantB }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "bad_request");
        await a.PostAsync($"/api/t/{t.TenantA}/tickets", new { customerId = t.CustomerA, title = "x", description = "x", tenantId = t.TenantB }, ("Idempotency-Key", Guid.NewGuid().ToString()))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "bad_request");
        await a.PostAsync($"/api/t/{t.TenantA}/tickets/{t.TicketA}/comments", new { body = "x", tenantId = t.TenantB, authorUserId = t.OwnerB.Id }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "bad_request");
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.customers WHERE name = 'Gövdeden şirket'"));
    }

    [Fact]
    public async Task CanaryValues_NeverAppear_InOtherTenant()
    {
        var a = t.OwnerA.Client;
        var surfaces = new[]
        {
            $"/api/t/{t.TenantA}/",
            $"/api/t/{t.TenantA}/customers",
            $"/api/t/{t.TenantA}/customers?q={CanaryTenants.CanaryB}",
            $"/api/t/{t.TenantA}/customers?q=canary-b&sort=name",
            $"/api/t/{t.TenantA}/customers/{t.CustomerA}",
            $"/api/t/{t.TenantA}/tickets",
            $"/api/t/{t.TenantA}/tickets?q={CanaryTenants.CanaryB}",
            $"/api/t/{t.TenantA}/tickets?assignee={t.AgentB.Id}",
            $"/api/t/{t.TenantA}/tickets?customerId={t.CustomerB}",
            $"/api/t/{t.TenantA}/tickets/{t.TicketA}",
            $"/api/t/{t.TenantA}/tickets/{t.TicketA}/timeline",
            $"/api/t/{t.TenantA}/members",
            $"/api/t/{t.TenantA}/assignable-members",
            "/api/me",
            "/api/me/memberships",
            "/api/me/mailbox",
        };
        foreach (var path in surfaces)
        {
            var response = await a.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{path} → {(int)response.StatusCode}");
            Assert.False(body.Contains("CANARY-B", StringComparison.OrdinalIgnoreCase), $"B kanaryası A yüzeyinde: {path}");
            Assert.DoesNotContain(t.TenantB.ToString(), body);
            Assert.DoesNotContain(t.CustomerB.ToString(), body);
            Assert.DoesNotContain(t.TicketB.ToString(), body);
        }

        // Karşılaştırma: aynı sorgular B'nin kendi oturumunda kanaryayı döndürür (testin boş geçmediğinin kanıtı).
        var own = await (await t.OwnerB.Client.GetAsync($"/api/t/{t.TenantB}/tickets/{t.TicketB}/timeline")).Content.ReadAsStringAsync();
        Assert.Contains(CanaryTenants.CanaryB, own);
        var ownSearch = await t.OwnerB.Client.GetAsync($"/api/t/{t.TenantB}/customers?q={CanaryTenants.CanaryB}").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Single(ownSearch["items"]!.AsArray());
    }
}

/// <summary>Bileşik FK'ler: RLS'i atlayan superuser bile şirketler arası satır bağlayamaz; kısıt veritabanının kendisindedir.</summary>
public sealed class CompositeFkTests(TestEnvironment env, CanaryTenants t) : IClassFixture<CanaryTenants>
{
    private const string ForeignKeyViolation = "23503";

    [Fact]
    public async Task CrossTenant_Customer_Assignee_Comment_Rejected()
    {
        await using var super = await RoleSession.OpenAsync(env.Postgres.Superuser());

        // A şirketinde talep, B'nin müşterisine bağlanmak isteniyor.
        var ticket = await super.TryExecAsync(
            "INSERT INTO app.tickets (id, tenant_id, number, customer_id, title, description, priority, status, created_by_user_id, created_at, updated_at, reopen_count, version) VALUES (gen_random_uuid(), $1, 9101, $2, 'fk', 'fk', 'Normal', 'Open', $3, now(), now(), 0, 1)",
            t.TenantA, t.CustomerB, t.OwnerA.Id);
        Assert.Equal(ForeignKeyViolation, ticket.SqlState);

        // A'nın talebi, yalnız B'ye üye olan kullanıcıya atanmak isteniyor (kullanıcı global olarak var; üyeliği A'da yok).
        var assign = await super.TryExecAsync("UPDATE app.tickets SET assignee_user_id = $1 WHERE id = $2", t.AgentB.Id, t.TicketA);
        Assert.Equal(ForeignKeyViolation, assign.SqlState);

        // A'nın talebini B'ye "taşımak": müşteri ve üyelik bağları kopar.
        var move = await super.TryExecAsync("UPDATE app.tickets SET tenant_id = $1, number = 9103 WHERE id = $2", t.TenantB, t.TicketA);
        Assert.Equal(ForeignKeyViolation, move.SqlState);

        // A şirketi adına, B'nin talebine yorum.
        var comment = await super.TryExecAsync(
            "INSERT INTO app.ticket_comments (id, tenant_id, ticket_id, author_user_id, body, created_at) VALUES (gen_random_uuid(), $1, $2, $3, 'fk', now())",
            t.TenantA, t.TicketB, t.OwnerA.Id);
        Assert.Equal(ForeignKeyViolation, comment.SqlState);

        // B'nin talebine, B'de üye olmayan yazar.
        var author = await super.TryExecAsync(
            "INSERT INTO app.ticket_comments (id, tenant_id, ticket_id, author_user_id, body, created_at) VALUES (gen_random_uuid(), $1, $2, $3, 'fk', now())",
            t.TenantB, t.TicketB, t.OwnerA.Id);
        Assert.Equal(ForeignKeyViolation, author.SqlState);

        // A'nın denetim kaydı, B'nin talebine.
        var audit = await super.TryExecAsync(
            "INSERT INTO app.audit_events (id, tenant_id, action, target_type, ticket_id, data, occurred_at) VALUES (gen_random_uuid(), $1, 'fk', 'ticket', $2, '{}', now())",
            t.TenantA, t.TicketB);
        Assert.Equal(ForeignKeyViolation, audit.SqlState);
    }

    [Fact]
    public async Task AppRole_InScopeA_CannotBindToTenantB_EvenWithCorrectTenantIdOnRow()
    {
        // Satırın tenant_id'si A olduğu için RLS WITH CHECK geçer; yabancı kimliği durduran bileşik FK'dir.
        await using var app = await RoleSession.OpenAsync(env.Postgres.App(), t.OwnerA.Id, t.TenantA);
        var ticket = await app.TryExecAsync(
            "INSERT INTO app.tickets (id, tenant_id, number, customer_id, title, description, priority, status, created_by_user_id, created_at, updated_at, reopen_count, version) VALUES (gen_random_uuid(), $1, 9102, $2, 'fk', 'fk', 'Normal', 'Open', $3, now(), now(), 0, 1)",
            t.TenantA, t.CustomerB, t.OwnerA.Id);
        Assert.Equal(ForeignKeyViolation, ticket.SqlState);
        var assign = await app.TryExecAsync("UPDATE app.tickets SET assignee_user_id = $1 WHERE id = $2", t.AgentB.Id, t.TicketA);
        Assert.Equal(ForeignKeyViolation, assign.SqlState);
    }
}
