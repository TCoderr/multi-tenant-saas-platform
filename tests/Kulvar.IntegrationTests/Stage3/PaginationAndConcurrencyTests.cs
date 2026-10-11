using System.Net;
using System.Text.Json.Nodes;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Platform.Tenancy;

namespace Kulvar.IntegrationTests.Stage3;

public sealed class PaginationTests(TestEnvironment env)
{
    private World World => env.World;

    private static async Task<List<JsonNode>> ReadAllPagesAsync(ApiClient client, string basePath, int limit, string? startCursor = null)
    {
        var all = new List<JsonNode>();
        var cursor = startCursor;
        for (var page = 0; page < 50; page++)
        {
            var separator = basePath.Contains('?') ? '&' : '?';
            var path = $"{basePath}{separator}limit={limit}" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var body = await client.GetAsync(path).ShouldBeAsync(HttpStatusCode.OK);
            var items = body["items"]!.AsArray();
            Assert.True(items.Count <= limit);
            all.AddRange(items.Select(i => i!.DeepClone()));
            cursor = body["nextCursor"]?.GetValue<string>();
            if (cursor is null)
            {
                return all;
            }
        }
        throw new InvalidOperationException("Sayfalama bitmedi.");
    }

    [Fact]
    public async Task Keyset_Pages_AreStable_AndComplete()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync(customerName: "Müşteri 00");
        var names = new List<string> { "Müşteri 00" };
        for (var i = 1; i <= 12; i++)
        {
            names.Add($"Müşteri {i:00}");
            await World.CreateCustomerAsync(owner.Client, tenant, $"Müşteri {i:00}");
            if (i % 4 == 0)
            {
                env.Clock.Advance(TimeSpan.FromSeconds(1)); // Bazı satırlar aynı ana düşer: sıra kimlikle belirlenir.
            }
        }

        foreach (var (query, expected) in new[]
                 {
                     ("sort=name&dir=asc", names.Order(StringComparer.Ordinal).ToList()),
                     ("sort=name&dir=desc", names.OrderDescending(StringComparer.Ordinal).ToList()),
                 })
        {
            var rows = await ReadAllPagesAsync(owner.Client, $"/api/t/{tenant}/customers?{query}", limit: 5);
            Assert.Equal(expected, rows.Select(r => r["name"].Str()).ToList());
        }

        var newestFirst = await ReadAllPagesAsync(owner.Client, $"/api/t/{tenant}/customers", limit: 4);
        Assert.Equal(13, newestFirst.Select(r => r["id"].Str()).Distinct().Count());
        var oldestFirst = await ReadAllPagesAsync(owner.Client, $"/api/t/{tenant}/customers?sort=createdAt&dir=asc", limit: 4);
        Assert.Equal(newestFirst.Select(r => r["id"].Str()).Reverse().ToList(), oldestFirst.Select(r => r["id"].Str()).ToList());

        // Sayfalar arasında yeni kayıt eklenmesi daha önce verilen satırları tekrar ettirmez veya atlatmaz.
        var first = await owner.Client.GetAsync($"/api/t/{tenant}/customers?sort=createdAt&dir=asc&limit=5").ShouldBeAsync(HttpStatusCode.OK);
        await World.CreateCustomerAsync(owner.Client, tenant, "Araya Giren");
        var rest = await ReadAllPagesAsync(owner.Client, $"/api/t/{tenant}/customers?sort=createdAt&dir=asc", limit: 5, startCursor: first["nextCursor"].Str());
        var combined = first["items"]!.AsArray().Select(i => i!["id"].Str()).Concat(rest.Select(r => r["id"].Str())).ToList();
        Assert.Equal(14, combined.Distinct().Count());
        Assert.Equal(14, combined.Count);

        // Talepler
        for (var i = 0; i < 7; i++)
        {
            await World.CreateTicketAsync(owner.Client, tenant, customerId, $"Talep {i}");
        }
        var tickets = await ReadAllPagesAsync(owner.Client, $"/api/t/{tenant}/tickets?sort=createdAt&dir=asc", limit: 3);
        Assert.Equal(Enumerable.Range(1, 7).ToList(), tickets.Select(x => x["number"].Int()).ToList());
        var filtered = await ReadAllPagesAsync(owner.Client, $"/api/t/{tenant}/tickets?status=Open&assignee=none&q=talep", limit: 2);
        Assert.Equal(7, filtered.Count);
    }

    [Fact]
    public async Task Cursor_FromTenantA_RejectedInTenantB_AndOtherFilter()
    {
        var shared = await World.CreateUserAsync("Ortak");
        var tenantA = await World.CreateTenantAsync(shared, "İmleç A");
        var tenantB = await World.CreateTenantAsync(shared, "İmleç B");
        var colleague = await World.CreateMemberAsync(tenantA, TenantRole.Admin);
        for (var i = 0; i < 4; i++)
        {
            await World.CreateCustomerAsync(shared.Client, tenantA, $"A-{i}");
            await World.CreateCustomerAsync(shared.Client, tenantB, $"CANARY-B-{i}");
        }

        var page = await shared.Client.GetAsync($"/api/t/{tenantA}/customers?limit=2").ShouldBeAsync(HttpStatusCode.OK);
        var cursor = Uri.EscapeDataString(page["nextCursor"].Str());

        // Aynı kullanıcı, başka şirket: imleç geçersiz. Yanıtta veri yok.
        var inB = await shared.Client.GetAsync($"/api/t/{tenantB}/customers?limit=2&cursor={cursor}").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid_cursor");
        Assert.DoesNotContain("A-", inB.ToJsonString());
        // Aynı şirket, başka filtre/sıralama/liste.
        await shared.Client.GetAsync($"/api/t/{tenantA}/customers?limit=2&sort=name&cursor={cursor}").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid_cursor");
        await shared.Client.GetAsync($"/api/t/{tenantA}/customers?limit=2&q=a&cursor={cursor}").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid_cursor");
        await shared.Client.GetAsync($"/api/t/{tenantA}/tickets?limit=2&cursor={cursor}").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid_cursor");
        // Aynı şirket, başka kullanıcı.
        await colleague.Client.GetAsync($"/api/t/{tenantA}/customers?limit=2&cursor={cursor}").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid_cursor");
        // Bozulmuş / uydurma imleç.
        await shared.Client.GetAsync($"/api/t/{tenantA}/customers?limit=2&cursor={cursor[..^4]}AAAA").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid_cursor");
        await shared.Client.GetAsync($"/api/t/{tenantA}/customers?limit=2&cursor=eyJ0IjoiMDAwIn0").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid_cursor");
        // Geçerli kullanım hâlâ çalışır.
        await shared.Client.GetAsync($"/api/t/{tenantA}/customers?limit=2&cursor={cursor}").ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnknownSortOrFilter_Rejected()
    {
        var (owner, tenant, _) = await World.CreateTenantWithCustomerAsync();
        foreach (var query in new[]
                 {
                     "customers?sort=email", "customers?sort=name;DROP%20TABLE", "customers?dir=sideways", "customers?limit=0", "customers?limit=101", "customers?limit=abc",
                     "customers?tenantId=" + Guid.NewGuid(), "customers?orderBy=password_hash",
                     "tickets?status=Bogus", "tickets?status=1", "tickets?priority=Critical", "tickets?assignee=herkes", "tickets?sort=title", "tickets?customerId=abc", "tickets?select=*",
                 })
        {
            await owner.Client.GetAsync($"/api/t/{tenant}/{query}").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        }
    }
}

public sealed class ConcurrencyTests(TestEnvironment env)
{
    private World World => env.World;

    [Fact]
    public async Task StaleIfMatch_412_MissingIfMatch_428()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var ticket = await World.CreateTicketAsync(owner.Client, tenant, customerId);
        var ticketId = ticket["id"].Guid();

        // Müşteri
        await owner.Client.PutAsync($"/api/t/{tenant}/customers/{customerId}", new { name = "Başlıksız" }).ShouldBeProblemAsync((HttpStatusCode)428, "precondition_required");
        await owner.Client.PutAsync($"/api/t/{tenant}/customers/{customerId}", new { name = "Biçimsiz" }, ("If-Match", "abc")).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "invalid_if_match");
        await owner.Client.PutAsync($"/api/t/{tenant}/customers/{customerId}", new { name = "İlk Düzenleme" }, ("If-Match", "\"1\"")).ShouldBeAsync(HttpStatusCode.OK);
        await owner.Client.PutAsync($"/api/t/{tenant}/customers/{customerId}", new { name = "Eski Sürümle" }, ("If-Match", "\"1\"")).ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        Assert.Equal("İlk Düzenleme", (await owner.Client.GetAsync($"/api/t/{tenant}/customers/{customerId}").ShouldBeAsync(HttpStatusCode.OK))["name"].Str());

        // Talep: düzenleme, geçiş ve atama aynı sürüm zincirini paylaşır.
        await owner.Client.PatchAsync($"/api/t/{tenant}/tickets/{ticketId}", new { title = "x" }).ShouldBeProblemAsync((HttpStatusCode)428, "precondition_required");
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets/{ticketId}/transition", new { action = "Start" }).ShouldBeProblemAsync((HttpStatusCode)428, "precondition_required");
        await owner.Client.PutAsync($"/api/t/{tenant}/tickets/{ticketId}/assignee", new { userId = owner.Id }).ShouldBeProblemAsync((HttpStatusCode)428, "precondition_required");

        var edited = await owner.Client.PatchAsync($"/api/t/{tenant}/tickets/{ticketId}", new { title = "Düzenlendi" }, ("If-Match", "\"1\"")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(2, edited["version"].Int());
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets/{ticketId}/transition", new { action = "Start" }, ("If-Match", "\"1\"")).ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        await owner.Client.PutAsync($"/api/t/{tenant}/tickets/{ticketId}/assignee", new { userId = owner.Id }, ("If-Match", "\"1\"")).ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        var current = await owner.Client.GetAsync($"/api/t/{tenant}/tickets/{ticketId}").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Open", current["status"].Str());
        Assert.Null(current["assigneeUserId"]);
    }

    [Fact]
    public async Task ParallelUpdates_SameVersion_OnlyOneWins()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var ticketId = (await World.CreateTicketAsync(owner.Client, tenant, customerId))["id"].Guid();
        const int Writers = 12;

        // Aynı sürümü okumuş 12 düzenleyici aynı anda yazar. Sonuç serpiştirmeden bağımsızdır: tam 1 başarı, diğerleri 412.
        using var gate = new SemaphoreSlim(0);
        var customerWrites = Enumerable.Range(0, Writers).Select(async i =>
        {
            var client = owner.Client.Clone();
            await gate.WaitAsync();
            return await client.PutAsync($"/api/t/{tenant}/customers/{customerId}", new { name = $"Yazar {i}" }, ("If-Match", "\"1\""));
        }).ToList();
        var ticketWrites = Enumerable.Range(0, Writers).Select(async i =>
        {
            var client = owner.Client.Clone();
            await gate.WaitAsync();
            return await client.PatchAsync($"/api/t/{tenant}/tickets/{ticketId}", new { title = $"Başlık {i}" }, ("If-Match", "\"1\""));
        }).ToList();
        gate.Release(Writers * 2);
        var customerResults = await Task.WhenAll(customerWrites);
        var ticketResults = await Task.WhenAll(ticketWrites);

        foreach (var results in new[] { customerResults, ticketResults })
        {
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(Writers - 1, results.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed));
        }
        Assert.Equal(2, await env.AdminScalarAsync<int>("SELECT version FROM app.customers WHERE id = $1", customerId));
        Assert.Equal(2, await env.AdminScalarAsync<int>("SELECT version FROM app.tickets WHERE id = $1", ticketId));
        // Kaybeden yazarların denetim kaydı da yoktur (transaction'ları geri alındı).
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'customer.updated'", tenant));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'ticket.edited'", tenant));
    }
}
