using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Platform.Tenancy;

namespace Kulvar.IntegrationTests.Stage3;

public sealed class IdempotencyTests(TestEnvironment env)
{
    private World World => env.World;

    private Task<long> TicketCountAsync(Guid tenant) => env.AdminScalarAsync<long>("SELECT count(*) FROM app.tickets WHERE tenant_id = $1", tenant);

    [Fact]
    public async Task Replay_ReturnsSameTicket_SingleEffect()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var body = new { customerId, title = "Kargo gecikmesi", description = "Sipariş üç gündür yolda.", priority = "High" };
        var key = ("Idempotency-Key", "siparis-1042-tekrar");

        var first = await owner.Client.PostAsync($"/api/t/{tenant}/tickets", body, key);
        var firstText = await first.Content.ReadAsStringAsync();
        var second = await owner.Client.PostAsync($"/api/t/{tenant}/tickets", body, key);
        var secondText = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(firstText, secondText);
        Assert.False(first.Headers.Contains("Idempotent-Replay"));
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replay").Single());
        Assert.Equal(first.ETag(), second.ETag());

        Assert.Equal(1L, await TicketCountAsync(tenant));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'ticket.created'", tenant));
        Assert.Equal(2, await env.AdminScalarAsync<int>("SELECT next_ticket_number FROM app.tenant_counters WHERE tenant_id = $1", tenant));
    }

    [Fact]
    public async Task SameKey_DifferentPayload_409()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var key = ("Idempotency-Key", "ayni-anahtar");
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "İlk", description = "x" }, key).ShouldBeAsync(HttpStatusCode.Created);
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "Farklı gövde", description = "x" }, key)
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "idempotency_key_reused");
        Assert.Equal(1L, await TicketCountAsync(tenant));
    }

    [Fact]
    public async Task MissingOrInvalidKey_400()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var body = new { customerId, title = "Anahtarsız", description = "x" };
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets", body).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets", body, ("Idempotency-Key", new string('k', 101))).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        Assert.Equal(0L, await TicketCountAsync(tenant));
    }

    [Fact]
    public async Task SameKey_DifferentTenantOrUser_Independent()
    {
        var shared = await World.CreateUserAsync("Ortak");
        var tenantA = await World.CreateTenantAsync(shared);
        var tenantB = await World.CreateTenantAsync(shared);
        var colleague = await World.CreateMemberAsync(tenantA, TenantRole.Agent);
        var customerA = (await World.CreateCustomerAsync(shared.Client, tenantA, "A Müşterisi"))["id"].Guid();
        var customerB = (await World.CreateCustomerAsync(shared.Client, tenantB, "B Müşterisi"))["id"].Guid();
        const string Key = "paylasilan-anahtar";

        var inA = await World.CreateTicketAsync(shared.Client, tenantA, customerA, "A'da", idempotencyKey: Key);
        // Aynı anahtar başka şirkette: A'nın kayıtlı yanıtı dönmez, B'de yeni talep oluşur.
        var inB = await World.CreateTicketAsync(shared.Client, tenantB, customerB, "B'de", idempotencyKey: Key);
        // Aynı anahtar aynı şirkette başka kullanıcı: bağımsız.
        var byColleague = await World.CreateTicketAsync(colleague.Client, tenantA, customerA, "Meslektaş", idempotencyKey: Key);

        Assert.Equal(3, new[] { inA, inB, byColleague }.Select(x => x["id"].Str()).Distinct().Count());
        Assert.Equal("B'de", inB["title"].Str());
        Assert.Equal("B Müşterisi", inB["customerName"].Str());
        Assert.Equal(2L, await TicketCountAsync(tenantA));
        Assert.Equal(1L, await TicketCountAsync(tenantB));
    }

    [Fact]
    public async Task ParallelSameKey_SingleTicket()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var body = new { customerId, title = "Çift tıklama", description = "x" };
        const int Requests = 10;

        using var gate = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, Requests).Select(async _ =>
        {
            var client = owner.Client.Clone();
            await gate.WaitAsync();
            var response = await client.PostAsync($"/api/t/{tenant}/tickets", body, ("Idempotency-Key", "cift-tiklama"));
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
        }).ToList();
        gate.Release(Requests);
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Single(results.Select(r => r.Body).Distinct());
        Assert.Equal(1L, await TicketCountAsync(tenant));
    }
}

public sealed class AtomicityTests(TestEnvironment env)
{
    [Fact]
    public async Task TicketCreate_FailureAfterInsert_LeavesNothing()
    {
        var (owner, tenant, customerId) = await env.World.CreateTenantWithCustomerAsync();
        await World.CreateTicketAsync(owner.Client, tenant, customerId, "Önceki talep");
        var before = await SnapshotAsync(tenant);

        // Talep satırı eklendikten SONRA, aynı transaction'daki denetim kaydı ekleme başarısız olsun (yalnız bu test şirketi için).
        await env.AdminExecuteAsync(
            """
            CREATE FUNCTION kulvar_test.fail_ticket_audit() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN
              IF NEW.action = 'ticket.created' AND NEW.tenant_id = TG_ARGV[0]::uuid THEN
                RAISE EXCEPTION 'injected failure after ticket insert' USING ERRCODE = 'P0001';
              END IF;
              RETURN NEW;
            END $fn$
            """);
        try
        {
            await env.AdminExecuteAsync($"CREATE TRIGGER trg_fail_ticket_audit BEFORE INSERT ON app.audit_events FOR EACH ROW EXECUTE FUNCTION kulvar_test.fail_ticket_audit('{tenant}')");

            var key = ("Idempotency-Key", "yarim-kalan");
            await owner.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "Yarım kalan", description = "x" }, key)
                .ShouldBeProblemAsync(HttpStatusCode.InternalServerError, "internal_error");

            // Hiçbir parça kalmadı: talep, etkinlik, sayaç artışı, idempotency kaydı.
            Assert.Equal(before, await SnapshotAsync(tenant));
            Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.idempotency_keys WHERE tenant_id = $1 AND key = 'yarim-kalan'", tenant));

            await env.AdminExecuteAsync("DROP TRIGGER trg_fail_ticket_audit ON app.audit_events");

            // Hata kalkınca aynı anahtarla yeniden deneme normal çalışır (anahtar "yakılmamış") ve numara boşluksuz devam eder.
            var retried = await owner.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "Yarım kalan", description = "x" }, key).ShouldBeAsync(HttpStatusCode.Created);
            Assert.Equal(2, retried["number"].Int());
        }
        finally
        {
            await env.AdminExecuteAsync("DROP TRIGGER IF EXISTS trg_fail_ticket_audit ON app.audit_events");
            await env.AdminExecuteAsync("DROP FUNCTION IF EXISTS kulvar_test.fail_ticket_audit()");
        }
    }

    private Task<string?> SnapshotAsync(Guid tenant) => env.AdminScalarAsync<string>(
        """
        SELECT concat_ws('|',
          (SELECT count(*) FROM app.tickets WHERE tenant_id = $1),
          (SELECT count(*) FROM app.audit_events WHERE tenant_id = $1),
          (SELECT count(*) FROM app.idempotency_keys WHERE tenant_id = $1),
          (SELECT coalesce(max(next_ticket_number), 0) FROM app.tenant_counters WHERE tenant_id = $1),
          (SELECT count(*) FROM app.outbox_messages WHERE tenant_id = $1))
        """, tenant);
}
