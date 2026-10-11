using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Platform.Tenancy;

namespace Kulvar.IntegrationTests.Stage3;

public sealed class CustomerTests(TestEnvironment env)
{
    private World World => env.World;

    [Fact]
    public async Task Create_Get_Update_Search_RoundTrip()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);

        var created = await World.CreateCustomerAsync(owner.Client, tenant, "Deniz Arslan", "deniz@musteri.test", "+90 555 000 00 01", "Arslan Tekstil Ltd.", "Öncelikli müşteri");
        var id = created["id"].Guid();
        Assert.Equal(1, created["version"].Int());
        Assert.False(created["piiMasked"]!.GetValue<bool>());

        var fetched = await owner.Client.GetAsync($"/api/t/{tenant}/customers/{id}");
        Assert.Equal("\"1\"", fetched.ETag());
        Assert.Equal("deniz@musteri.test", (await fetched.JsonAsync())["email"].Str());

        var updated = await owner.Client.PutAsync($"/api/t/{tenant}/customers/{id}",
            new { name = "Deniz Arslan Yılmaz", email = "deniz@musteri.test", phone = "+90 555 000 00 01", companyTitle = "Arslan Tekstil A.Ş." }, ("If-Match", "\"1\"")).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(2, updated["version"].Int());
        Assert.Null(updated["notes"]);

        // Arama: ad, unvan ve e-posta üzerinden; büyük/küçük harf duyarsız (ASCII dışı harflerde veritabanı yerel ayarına bağlıdır).
        foreach (var term in new[] { "yılmaz", "tekstil", "DENIZ@MUSTERI" })
        {
            var hits = await owner.Client.GetAsync($"/api/t/{tenant}/customers?q={Uri.EscapeDataString(term)}").ShouldBeAsync(HttpStatusCode.OK);
            Assert.Single(hits["items"]!.AsArray());
        }
        var miss = await owner.Client.GetAsync($"/api/t/{tenant}/customers?q=bulunmayan").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Empty(miss["items"]!.AsArray());
        // LIKE joker karakterleri arama terimi olarak özel anlam taşımaz.
        var wildcard = await owner.Client.GetAsync($"/api/t/{tenant}/customers?q=%25").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Empty(wildcard["items"]!.AsArray());
    }

    [Fact]
    public async Task Validation_ReturnsFieldErrors()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var problem = await owner.Client.PostAsync($"/api/t/{tenant}/customers", new { name = "  ", email = "e-posta-degil", phone = "abc" })
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        var errors = problem["errors"]!.AsObject();
        Assert.True(errors.ContainsKey("name") && errors.ContainsKey("email") && errors.ContainsKey("phone"));
    }

    [Fact]
    public async Task Viewer_SeesMaskedEmailAndPhone_AndCannotSearchByEmail()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var viewer = await World.CreateMemberAsync(tenant, TenantRole.Viewer);
        var customer = await World.CreateCustomerAsync(owner.Client, tenant, "Gizli Müşteri", "gizli.kisi@musteri.test", "+90 555 123 45 67", notes: "Özel not CANARY-PII");
        var id = customer["id"].Guid();

        var detail = await (await viewer.Client.GetAsync($"/api/t/{tenant}/customers/{id}")).Content.ReadAsStringAsync();
        var list = await (await viewer.Client.GetAsync($"/api/t/{tenant}/customers")).Content.ReadAsStringAsync();
        foreach (var body in new[] { detail, list })
        {
            Assert.DoesNotContain("gizli.kisi", body);
            Assert.DoesNotContain("123 45", body);
            Assert.DoesNotContain("CANARY-PII", body);
            Assert.Contains("g***@musteri.test", body);
            Assert.Contains("\"piiMasked\":true", body);
        }

        // Maskelenen alan arama ile tahmin edilemez: Viewer e-postadan arayınca eşleşme olmaz, addan arayınca olur.
        var byEmail = await viewer.Client.GetAsync($"/api/t/{tenant}/customers?q=gizli.kisi").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Empty(byEmail["items"]!.AsArray());
        var byName = await viewer.Client.GetAsync($"/api/t/{tenant}/customers?q=gizli%20m").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Single(byName["items"]!.AsArray());
        var ownerByEmail = await owner.Client.GetAsync($"/api/t/{tenant}/customers?q=gizli.kisi").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Single(ownerByEmail["items"]!.AsArray());

        // Viewer yazamaz.
        await viewer.Client.PostAsync($"/api/t/{tenant}/customers", new { name = "Viewer Müşterisi" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await viewer.Client.PutAsync($"/api/t/{tenant}/customers/{id}", new { name = "Değişti" }, ("If-Match", "\"1\"")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await viewer.Client.DeleteAsync($"/api/t/{tenant}/customers/{id}").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Email_UniquePerTenant_NotGlobal()
    {
        var ownerA = await World.CreateUserAsync();
        var ownerB = await World.CreateUserAsync();
        var tenantA = await World.CreateTenantAsync(ownerA);
        var tenantB = await World.CreateTenantAsync(ownerB);

        await World.CreateCustomerAsync(ownerA.Client, tenantA, "Ortak Müşteri", "ortak@musteri.test");
        await ownerA.Client.PostAsync($"/api/t/{tenantA}/customers", new { name = "Aynı E-posta", email = "ORTAK@Musteri.Test" })
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "customer_email_exists");
        // Başka şirket aynı adresi kendi kaydında tutabilir; A'daki varlığı B'ye sızmaz.
        await World.CreateCustomerAsync(ownerB.Client, tenantB, "B'nin Müşterisi", "ortak@musteri.test");
        // E-postasız müşteriler birbirini engellemez.
        await World.CreateCustomerAsync(ownerA.Client, tenantA, "E-postasız 1");
        await World.CreateCustomerAsync(ownerA.Client, tenantA, "E-postasız 2");
    }

    [Fact]
    public async Task Delete_OwnerOrAdminOnly_AndBlockedWhenTicketsExist()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        var agent = await World.CreateMemberAsync(tenant, TenantRole.Agent);
        var spare = (await World.CreateCustomerAsync(owner.Client, tenant, "Silinecek"))["id"].Guid();

        await agent.Client.DeleteAsync($"/api/t/{tenant}/customers/{spare}").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await owner.Client.DeleteAsync($"/api/t/{tenant}/customers/{spare}").ShouldBeAsync(HttpStatusCode.NoContent);
        await owner.Client.GetAsync($"/api/t/{tenant}/customers/{spare}").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");

        await World.CreateTicketAsync(owner.Client, tenant, customerId);
        await owner.Client.DeleteAsync($"/api/t/{tenant}/customers/{customerId}").ShouldBeProblemAsync(HttpStatusCode.Conflict, "customer_has_tickets");
        await owner.Client.GetAsync($"/api/t/{tenant}/customers/{customerId}").ShouldBeAsync(HttpStatusCode.OK);
    }
}
