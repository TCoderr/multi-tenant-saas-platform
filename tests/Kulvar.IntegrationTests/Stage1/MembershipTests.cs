using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Platform.Tenancy;

namespace Kulvar.IntegrationTests.Stage1;

public sealed class MembershipTests(TestEnvironment env)
{
    private World World => env.World;

    private static async Task<string> MemberETagAsync(ApiClient client, Guid tenantId, Guid userId)
    {
        var members = await client.GetAsync($"/api/t/{tenantId}/members").ShouldBeAsync(HttpStatusCode.OK);
        var version = members["items"]!.AsArray().Single(m => m!["userId"].Guid() == userId)!["version"].Int();
        return $"\"{version}\"";
    }

    private Task<long> OwnerCountAsync(Guid tenantId) =>
        env.AdminScalarAsync<long>("SELECT count(*) FROM app.memberships WHERE tenant_id = $1 AND role = 'Owner' AND status = 'Active'", tenantId);

    [Fact]
    public async Task LastOwner_CannotLeave_Demote_OrBeRemoved()
    {
        var owner = await World.CreateUserAsync("Tek Owner");
        var tenant = await World.CreateTenantAsync(owner);
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin);

        await owner.Client.PostAsync($"/api/t/{tenant}/members/leave").ShouldBeProblemAsync(HttpStatusCode.Conflict, "last_owner");
        var etag = await MemberETagAsync(owner.Client, tenant, owner.Id);
        await owner.Client.PatchAsync($"/api/t/{tenant}/members/{owner.Id}", new { role = "Admin" }, ("If-Match", etag))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, "last_owner");
        // Admin Owner'ı kaldıramaz (yetki); kendini kaldırma yolu da "ayrıl"dır.
        await admin.Client.DeleteAsync($"/api/t/{tenant}/members/{owner.Id}").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await owner.Client.DeleteAsync($"/api/t/{tenant}/members/{owner.Id}").ShouldBeProblemAsync(HttpStatusCode.BadRequest, "use_leave");

        Assert.Equal(1L, await OwnerCountAsync(tenant));
        await owner.Client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SecondOwner_AllowsFirstToLeave_ButNotBoth()
    {
        var first = await World.CreateUserAsync("Birinci Owner");
        var tenant = await World.CreateTenantAsync(first);
        var second = await World.CreateMemberAsync(tenant, TenantRole.Owner, "İkinci Owner");

        await first.Client.PostAsync($"/api/t/{tenant}/members/leave").ShouldBeAsync(HttpStatusCode.NoContent);
        await second.Client.PostAsync($"/api/t/{tenant}/members/leave").ShouldBeProblemAsync(HttpStatusCode.Conflict, "last_owner");
        Assert.Equal(1L, await OwnerCountAsync(tenant));
    }

    [Fact]
    public async Task Admin_CannotEscalate_OrManageOwnerOrAdmin()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin, "Admin Bir");
        var otherAdmin = await World.CreateMemberAsync(tenant, TenantRole.Admin, "Admin İki");
        var agent = await World.CreateMemberAsync(tenant, TenantRole.Agent);

        // Kendini yükseltme
        var selfTag = await MemberETagAsync(admin.Client, tenant, admin.Id);
        await admin.Client.PatchAsync($"/api/t/{tenant}/members/{admin.Id}", new { role = "Owner" }, ("If-Match", selfTag))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner_requires_offer");
        // Başkasını Admin yapma
        var agentTag = await MemberETagAsync(admin.Client, tenant, agent.Id);
        await admin.Client.PatchAsync($"/api/t/{tenant}/members/{agent.Id}", new { role = "Admin" }, ("If-Match", agentTag))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        // Owner'ı ve diğer Admin'i düşürme/kaldırma
        var ownerTag = await MemberETagAsync(admin.Client, tenant, owner.Id);
        await admin.Client.PatchAsync($"/api/t/{tenant}/members/{owner.Id}", new { role = "Viewer" }, ("If-Match", ownerTag))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        var otherTag = await MemberETagAsync(admin.Client, tenant, otherAdmin.Id);
        await admin.Client.PatchAsync($"/api/t/{tenant}/members/{otherAdmin.Id}", new { role = "Agent" }, ("If-Match", otherTag))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await admin.Client.DeleteAsync($"/api/t/{tenant}/members/{otherAdmin.Id}").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // İzinli olan: Agent ↔ Viewer
        await admin.Client.PatchAsync($"/api/t/{tenant}/members/{agent.Id}", new { role = "Viewer" }, ("If-Match", agentTag)).ShouldBeAsync(HttpStatusCode.OK);

        // Agent ve Viewer hiçbir üyelik yönetimi yapamaz.
        var agentSelfTag = await MemberETagAsync(owner.Client, tenant, agent.Id);
        await agent.Client.PatchAsync($"/api/t/{tenant}/members/{agent.Id}", new { role = "Agent" }, ("If-Match", agentSelfTag))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task RoleChange_ToOwner_Rejected_EvenForOwner()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin);

        var etag = await MemberETagAsync(owner.Client, tenant, admin.Id);
        await owner.Client.PatchAsync($"/api/t/{tenant}/members/{admin.Id}", new { role = "Owner" }, ("If-Match", etag))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner_requires_offer");
        Assert.Equal(1L, await OwnerCountAsync(tenant));
    }

    [Fact]
    public async Task RoleChange_RequiresIfMatch_AndRejectsStaleVersion()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var agent = await World.CreateMemberAsync(tenant, TenantRole.Agent);

        await owner.Client.PatchAsync($"/api/t/{tenant}/members/{agent.Id}", new { role = "Viewer" }).ShouldBeProblemAsync((HttpStatusCode)428, "precondition_required");
        var etag = await MemberETagAsync(owner.Client, tenant, agent.Id);
        await owner.Client.PatchAsync($"/api/t/{tenant}/members/{agent.Id}", new { role = "Viewer" }, ("If-Match", etag)).ShouldBeAsync(HttpStatusCode.OK);
        await owner.Client.PatchAsync($"/api/t/{tenant}/members/{agent.Id}", new { role = "Admin" }, ("If-Match", etag))
            .ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
    }

    [Fact]
    public async Task Members_EmailVisibleOnlyToOwnerAndAdmin()
    {
        var owner = await World.CreateUserAsync("Owner");
        var tenant = await World.CreateTenantAsync(owner);
        var viewer = await World.CreateMemberAsync(tenant, TenantRole.Viewer, "Viewer");

        var asViewer = await viewer.Client.GetAsync($"/api/t/{tenant}/members").ShouldBeAsync(HttpStatusCode.OK);
        var ownerRow = asViewer["items"]!.AsArray().Single(m => m!["userId"].Guid() == owner.Id)!;
        Assert.Null(ownerRow["email"]);
        Assert.DoesNotContain(owner.Email, asViewer.ToJsonString());

        var asOwner = await owner.Client.GetAsync($"/api/t/{tenant}/members").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Contains(viewer.Email, asOwner.ToJsonString());
    }

    [Fact]
    public async Task TenantSettings_OwnerOnly_WithOptimisticConcurrency()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner, "Eski Ad");
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin);

        var workspace = await owner.Client.GetAsync($"/api/t/{tenant}/");
        var etag = workspace.ETag();
        await admin.Client.PatchAsync($"/api/t/{tenant}/settings", new { name = "Admin Değiştirdi" }, ("If-Match", etag)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await owner.Client.PatchAsync($"/api/t/{tenant}/settings", new { name = "Yeni Ad" }, ("If-Match", etag)).ShouldBeAsync(HttpStatusCode.OK);
        await owner.Client.PatchAsync($"/api/t/{tenant}/settings", new { name = "Geç Kalan" }, ("If-Match", etag)).ShouldBeProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");

        var after = await owner.Client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Yeni Ad", after["tenant"]!["name"].Str());
    }
}
