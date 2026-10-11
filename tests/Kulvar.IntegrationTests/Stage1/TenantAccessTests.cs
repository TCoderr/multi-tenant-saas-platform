using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Platform.Tenancy;

namespace Kulvar.IntegrationTests.Stage1;

public sealed class TenantAccessTests(TestEnvironment env)
{
    private World World => env.World;

    [Fact]
    public async Task UserInTwoTenants_HasIndependentRoles()
    {
        var ownerA = await World.CreateUserAsync("Ayşe Owner");
        var ownerB = await World.CreateUserAsync("Burak Owner");
        var tenantA = await World.CreateTenantAsync(ownerA, "Akasya Test");
        var tenantB = await World.CreateTenantAsync(ownerB, "Boreas Test");
        var shared = await World.CreateUserAsync("Cem Ortak");
        await World.AddMemberDirectAsync(tenantA, shared, TenantRole.Agent);
        await World.AddMemberDirectAsync(tenantB, shared, TenantRole.Admin);

        var inA = await shared.Client.GetAsync($"/api/t/{tenantA}/").ShouldBeAsync(HttpStatusCode.OK);
        var inB = await shared.Client.GetAsync($"/api/t/{tenantB}/").ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal("Agent", inA["role"].Str());
        Assert.Equal("Admin", inB["role"].Str());
        Assert.Equal("Akasya Test", inA["tenant"]!["name"].Str());
        Assert.Equal("Boreas Test", inB["tenant"]!["name"].Str());
        var permissionsA = inA["permissions"]!.AsArray().Select(p => p.Str()).ToList();
        var permissionsB = inB["permissions"]!.AsArray().Select(p => p.Str()).ToList();
        Assert.DoesNotContain("members.manage", permissionsA);
        Assert.Contains("members.manage", permissionsB);

        // Rol şirkete özgüdür: A'daki Agent, B'deki Admin yetkisini A'ya taşıyamaz.
        var memberA = await World.CreateMemberAsync(tenantA, TenantRole.Viewer);
        await shared.Client.DeleteAsync($"/api/t/{tenantA}/members/{memberA.Id}").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        var memberships = await shared.Client.GetAsync("/api/me/memberships").ShouldBeAsync(HttpStatusCode.OK);
        var ids = memberships["items"]!.AsArray().Select(i => i!["tenantId"].Guid()).ToHashSet();
        Assert.Equal(new HashSet<Guid> { tenantA, tenantB }, ids);
    }

    [Fact]
    public async Task TwoUsers_TwoTenants_CannotSeeEachOther()
    {
        var ownerA = await World.CreateUserAsync("Ayşe");
        var ownerB = await World.CreateUserAsync("Burak");
        var tenantA = await World.CreateTenantAsync(ownerA, "CANARY-A-SIRKET");
        var tenantB = await World.CreateTenantAsync(ownerB, "CANARY-B-SIRKET");

        await ownerA.Client.GetAsync($"/api/t/{tenantB}/").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        await ownerA.Client.GetAsync($"/api/t/{tenantB}/members").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        await ownerB.Client.GetAsync($"/api/t/{tenantA}/members").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");

        var mine = await (await ownerA.Client.GetAsync("/api/me/memberships")).Content.ReadAsStringAsync();
        Assert.Contains("CANARY-A-SIRKET", mine);
        Assert.DoesNotContain("CANARY-B-SIRKET", mine);
        Assert.DoesNotContain(tenantB.ToString(), mine);

        var members = await (await ownerA.Client.GetAsync($"/api/t/{tenantA}/members")).Content.ReadAsStringAsync();
        Assert.Contains(ownerA.Email, members);
        Assert.DoesNotContain(ownerB.Email, members);
    }

    [Fact]
    public async Task NonMember_And_UnknownTenant_SameNotFound()
    {
        var owner = await World.CreateUserAsync();
        var outsider = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);

        var existing = await outsider.Client.GetAsync($"/api/t/{tenant}/").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        var unknown = await outsider.Client.GetAsync($"/api/t/{Guid.NewGuid()}/").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        existing.Remove("traceId");
        unknown.Remove("traceId");
        Assert.Equal(existing.ToJsonString(), unknown.ToJsonString());

        // Bozuk kimlik: varsayılan şirkete veya "tümü"ne düşmez.
        await outsider.Client.GetAsync("/api/t/not-a-guid/").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await outsider.Client.GetAsync($"/api/t/{Guid.Empty}/").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
    }

    [Fact]
    public async Task TenantId_FromHeaderOrQuery_IsNotAuthority()
    {
        var ownerA = await World.CreateUserAsync();
        var ownerB = await World.CreateUserAsync();
        var tenantA = await World.CreateTenantAsync(ownerA, "Başlık A");
        var tenantB = await World.CreateTenantAsync(ownerB, "Başlık B");

        // Yoldaki şirket A; başlık/sorgu B'yi gösterse de yanıt A'dır ve B'ye erişim açılmaz.
        var response = await ownerA.Client.GetAsync($"/api/t/{tenantA}/?tenantId={tenantB}", ("X-Tenant-Id", tenantB.ToString())).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(tenantA, response["tenant"]!["id"].Guid());
        await ownerA.Client.GetAsync($"/api/t/{tenantB}/?tenantId={tenantA}", ("X-Tenant-Id", tenantA.ToString()))
            .ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
    }

    [Fact]
    public async Task RemovedMember_OldSession_Gets404()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var agent = await World.CreateMemberAsync(tenant, TenantRole.Agent);
        await agent.Client.GetAsync($"/api/t/{tenant}/members").ShouldBeAsync(HttpStatusCode.OK);

        await owner.Client.DeleteAsync($"/api/t/{tenant}/members/{agent.Id}").ShouldBeAsync(HttpStatusCode.NoContent);

        // Aynı (hâlâ geçerli) oturum cookie'si: üyelik her istekte okunduğu için erişim hemen kapanır.
        await agent.Client.GetAsync("/api/me").ShouldBeAsync(HttpStatusCode.OK);
        await agent.Client.GetAsync($"/api/t/{tenant}/members").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        var memberships = await agent.Client.GetAsync("/api/me/memberships").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Empty(memberships["items"]!.AsArray());
    }

    [Fact]
    public async Task RoleDowngrade_TakesEffectOnNextRequest()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin);
        var viewer = await World.CreateMemberAsync(tenant, TenantRole.Viewer);

        var members = await owner.Client.GetAsync($"/api/t/{tenant}/members").ShouldBeAsync(HttpStatusCode.OK);
        var adminVersion = members["items"]!.AsArray().Single(m => m!["userId"].Guid() == admin.Id)!["version"].Int();
        await owner.Client.PatchAsync($"/api/t/{tenant}/members/{admin.Id}", new { role = "Viewer" }, ("If-Match", $"\"{adminVersion}\"")).ShouldBeAsync(HttpStatusCode.OK);

        await admin.Client.DeleteAsync($"/api/t/{tenant}/members/{viewer.Id}").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }
}
