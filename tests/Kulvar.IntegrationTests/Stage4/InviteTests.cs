using System.Net;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Platform.Security;
using Kulvar.Platform.Tenancy;

namespace Kulvar.IntegrationTests.Stage4;

public sealed class InviteTests(TestEnvironment env)
{
    private World World => env.World;

    private Task<long> ActiveMembersAsync(Guid tenant) =>
        env.AdminScalarAsync<long>("SELECT count(*) FROM app.memberships WHERE tenant_id = $1 AND status = 'Active'", tenant);

    [Fact]
    public async Task Invite_Accept_Join_HappyPath()
    {
        var owner = await World.CreateUserAsync("Davet Eden");
        var tenant = await World.CreateTenantAsync(owner, "Davet Şirketi");
        var invitee = await World.CreateUserAsync("Davetli Kişi");

        var inviteId = await World.InviteAsync(owner, tenant, invitee.Email.ToUpperInvariant(), TenantRole.Agent);
        var listed = await owner.Client.GetAsync($"/api/t/{tenant}/invites").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Sending", listed["items"]![0]!["state"].Str()); // Posta üretilene kadar kabul edilemez.

        var token = await World.InviteTokenAsync(invitee);
        listed = await owner.Client.GetAsync($"/api/t/{tenant}/invites").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Pending", listed["items"]![0]!["state"].Str());
        var summary = await owner.Client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(1, summary["usage"]!["pendingInvites"].Int());

        var preview = await invitee.Client.PostAsync("/api/invites/lookup", new { token }).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Davet Şirketi", preview["tenantName"].Str());
        Assert.Equal("Agent", preview["role"].Str());
        Assert.True(preview["emailMatches"]!.GetValue<bool>());

        var accepted = await invitee.Client.PostAsync("/api/invites/accept", new { token }).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(tenant, accepted["tenantId"].Guid());
        var workspace = await invitee.Client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Agent", workspace["role"].Str());
        Assert.Equal(2L, await ActiveMembersAsync(tenant));

        // Tek kullanımlık.
        await invitee.Client.PostAsync("/api/invites/accept", new { token }).ShouldBeProblemAsync(HttpStatusCode.Conflict, "invite_not_pending");

        // Davet edene bildirim iletisi.
        await env.App.DrainOutboxAsync();
        var mailbox = await owner.Client.GetAsync("/api/me/mailbox").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Contains(mailbox["items"]!.AsArray(), m => m!["kind"].Str() == "invite_accepted");
        Assert.Equal("Accepted", await env.AdminScalarAsync<string>("SELECT status FROM app.invites WHERE id = $1", inviteId));
    }

    [Fact]
    public async Task Token_StoredOnlyAsHash_NeverReturnedByApi()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin);
        var invitee = await World.CreateUserAsync();
        var inviteId = await World.InviteAsync(owner, tenant, invitee.Email, TenantRole.Viewer);
        var token = await World.InviteTokenAsync(invitee);

        // Veritabanı: davet satırında yalnız SHA-256 hash'i; ham token ne davet, ne kuyruk, ne denetim kaydında.
        Assert.Equal(Tokens.HashHex(token), await env.AdminScalarAsync<string>("SELECT encode(token_hash, 'hex') FROM app.invites WHERE id = $1", inviteId));
        foreach (var probe in new[]
                 {
                     "SELECT count(*) FROM app.invites WHERE invites::text LIKE '%' || $1 || '%'",
                     "SELECT count(*) FROM app.outbox_messages WHERE payload::text LIKE '%' || $1 || '%'",
                     "SELECT count(*) FROM app.audit_events WHERE data::text LIKE '%' || $1 || '%'",
                 })
        {
            Assert.Equal(0L, await env.AdminScalarAsync<long>(probe, token));
        }
        // Ham token yalnız alıcının posta kutusu iletisinde.
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.sink_messages WHERE action_path LIKE '%' || $1", token));

        // API: Owner ve Admin davet listesinde token veya hash göremez.
        foreach (var manager in new[] { owner, admin })
        {
            var body = await (await manager.Client.GetAsync($"/api/t/{tenant}/invites")).Content.ReadAsStringAsync();
            Assert.DoesNotContain(token, body);
            Assert.DoesNotContain(Tokens.HashHex(token), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Accept_WithDifferentEmail_Rejected()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var invitee = await World.CreateUserAsync("Asıl Davetli");
        var thief = await World.CreateUserAsync("Bağlantıyı Ele Geçiren");
        await World.InviteAsync(owner, tenant, invitee.Email, TenantRole.Admin);
        var token = await World.InviteTokenAsync(invitee);

        // Bağlantı sızmış olsa bile başka (doğrulanmış) hesapla kabul edilemez.
        var preview = await thief.Client.PostAsync("/api/invites/lookup", new { token }).ShouldBeAsync(HttpStatusCode.OK);
        Assert.False(preview["emailMatches"]!.GetValue<bool>());
        Assert.DoesNotContain(invitee.Email, preview.ToJsonString());
        await thief.Client.PostAsync("/api/invites/accept", new { token }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "invite_email_mismatch");
        await thief.Client.GetAsync($"/api/t/{tenant}/").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        Assert.Equal(1L, await ActiveMembersAsync(tenant));

        // Davet hâlâ geçerli: asıl davetli kabul edebilir.
        await invitee.Client.PostAsync("/api/invites/accept", new { token }).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Accept_UnverifiedEmail_Rejected()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var email = World.UniqueEmail("dogrulanmamis");
        await World.InviteAsync(owner, tenant, email, TenantRole.Agent);
        var invitee = await World.CreateUnverifiedUserAsync("Doğrulanmamış", email);
        var token = await World.InviteTokenAsync(invitee);

        await invitee.Client.PostAsync("/api/invites/accept", new { token }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "email_not_verified");
        Assert.Equal(1L, await ActiveMembersAsync(tenant));

        var verification = await World.LatestMailTokenAsync(invitee.Client, "email_verification");
        await invitee.Client.PostAsync("/api/auth/verify-email", new { token = verification }).ShouldBeAsync(HttpStatusCode.NoContent);
        await invitee.Client.PostAsync("/api/invites/accept", new { token }).ShouldBeAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoked_Or_Expired_Token_Rejected()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);

        // İptal
        var revokedInvitee = await World.CreateUserAsync();
        var revokedId = await World.InviteAsync(owner, tenant, revokedInvitee.Email, TenantRole.Agent);
        var revokedToken = await World.InviteTokenAsync(revokedInvitee);
        await owner.Client.DeleteAsync($"/api/t/{tenant}/invites/{revokedId}").ShouldBeAsync(HttpStatusCode.NoContent);
        await revokedInvitee.Client.PostAsync("/api/invites/accept", new { token = revokedToken }).ShouldBeProblemAsync(HttpStatusCode.Conflict, "invite_not_pending");
        await owner.Client.DeleteAsync($"/api/t/{tenant}/invites/{revokedId}").ShouldBeProblemAsync(HttpStatusCode.Conflict, "invite_not_pending");

        // Süre
        var lateInvitee = await World.CreateUserAsync();
        await World.InviteAsync(owner, tenant, lateInvitee.Email, TenantRole.Agent);
        var lateToken = await World.InviteTokenAsync(lateInvitee);
        env.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        var relogin = env.App.NewClient();
        await relogin.LoginAsync(lateInvitee.Email, lateInvitee.Password).ShouldBeAsync(HttpStatusCode.OK);
        var preview = await relogin.PostAsync("/api/invites/lookup", new { token = lateToken }).ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Expired", preview["state"].Str());
        await relogin.PostAsync("/api/invites/accept", new { token = lateToken }).ShouldBeProblemAsync(HttpStatusCode.Gone, "invite_expired");

        // Uydurma / bozuk token
        await relogin.PostAsync("/api/invites/accept", new { token = Tokens.NewToken() }).ShouldBeProblemAsync(HttpStatusCode.NotFound, "invite_not_found");
        await relogin.PostAsync("/api/invites/accept", new { token = "../../etc/passwd" }).ShouldBeProblemAsync(HttpStatusCode.NotFound, "invite_not_found");
        Assert.Equal(1L, await ActiveMembersAsync(tenant));

        // Süresi dolan davetin yerine yenisi verilebilir (eski kayıt bekleyen davet sayılmaz).
        var freshOwner = env.App.NewClient();
        await freshOwner.LoginAsync(owner.Email, owner.Password).ShouldBeAsync(HttpStatusCode.OK);
        await freshOwner.PostAsync($"/api/t/{tenant}/invites", new { email = lateInvitee.Email, role = "Agent" }).ShouldBeAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ParallelAccept_SingleMembership()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var invitee = await World.CreateUserAsync();
        await World.InviteAsync(owner, tenant, invitee.Email, TenantRole.Agent);
        var token = await World.InviteTokenAsync(invitee);
        const int Attempts = 8;

        using var gate = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, Attempts).Select(async _ =>
        {
            var client = invitee.Client.Clone();
            await gate.WaitAsync();
            return (await client.PostAsync("/api/invites/accept", new { token })).StatusCode;
        }).ToList();
        gate.Release(Attempts);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(Attempts - 1, results.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.memberships WHERE tenant_id = $1 AND user_id = $2", tenant, invitee.Id));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'invite.accepted'", tenant));
    }

    [Fact]
    public async Task Accept_RejectsRoleOrTenantInBody_RoleComesFromInvite()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var otherTenant = await World.CreateTenantAsync(owner, "Başka Şirket");
        var invitee = await World.CreateUserAsync();
        await World.InviteAsync(owner, tenant, invitee.Email, TenantRole.Viewer);
        var token = await World.InviteTokenAsync(invitee);

        await invitee.Client.PostAsync("/api/invites/accept", new { token, role = "Owner" }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "bad_request");
        await invitee.Client.PostAsync("/api/invites/accept", new { token, tenantId = otherTenant }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "bad_request");
        await invitee.Client.PostAsync("/api/invites/accept", new { token }).ShouldBeAsync(HttpStatusCode.OK);

        Assert.Equal("Viewer", await env.AdminScalarAsync<string>("SELECT role FROM app.memberships WHERE tenant_id = $1 AND user_id = $2", tenant, invitee.Id));
        // Token yalnız kendi şirketine üyelik verir.
        await invitee.Client.GetAsync($"/api/t/{otherTenant}/").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
    }

    [Fact]
    public async Task InviteRules_RoleLimits_Duplicates_AndCrossTenant()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin);
        var otherOwner = await World.CreateUserAsync();
        var otherTenant = await World.CreateTenantAsync(otherOwner);
        var agentElsewhere = await World.CreateMemberAsync(otherTenant, TenantRole.Agent);

        // Rol sınırları: Owner davetle verilemez; Admin yalnız Agent/Viewer davet eder; Agent hiç davet edemez.
        await owner.Client.PostAsync($"/api/t/{tenant}/invites", new { email = World.UniqueEmail(), role = "Owner" }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "owner_requires_offer");
        await admin.Client.PostAsync($"/api/t/{tenant}/invites", new { email = World.UniqueEmail(), role = "Admin" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await agentElsewhere.Client.PostAsync($"/api/t/{otherTenant}/invites", new { email = World.UniqueEmail(), role = "Viewer" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await agentElsewhere.Client.GetAsync($"/api/t/{otherTenant}/invites").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // Yinelenen ve zaten üye
        var email = World.UniqueEmail("tekrar");
        var first = await owner.Client.PostAsync($"/api/t/{tenant}/invites", new { email, role = "Admin" }).ShouldBeAsync(HttpStatusCode.Created);
        await owner.Client.PostAsync($"/api/t/{tenant}/invites", new { email = email.ToUpperInvariant(), role = "Viewer" }).ShouldBeProblemAsync(HttpStatusCode.Conflict, "invite_already_pending");
        await owner.Client.PostAsync($"/api/t/{tenant}/invites", new { email = admin.Email, role = "Viewer" }).ShouldBeProblemAsync(HttpStatusCode.Conflict, "already_member");

        // Admin, Admin rolündeki daveti iptal edemez; başka şirketin yöneticisi daveti göremez/iptal edemez.
        var inviteId = first["id"].Guid();
        await admin.Client.DeleteAsync($"/api/t/{tenant}/invites/{inviteId}").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        await otherOwner.Client.DeleteAsync($"/api/t/{otherTenant}/invites/{inviteId}").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await otherOwner.Client.DeleteAsync($"/api/t/{tenant}/invites/{inviteId}").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        var foreignList = await (await otherOwner.Client.GetAsync($"/api/t/{otherTenant}/invites")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(email, foreignList);
        await owner.Client.DeleteAsync($"/api/t/{tenant}/invites/{inviteId}").ShouldBeAsync(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ParallelInvites_RespectSeatLimit()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var summary = await owner.Client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
        var seatLimit = summary["entitlements"]!["seatLimit"].Int();
        var freeSeats = seatLimit - 1; // Yalnız Owner var.
        const int Attempts = 14;
        Assert.True(Attempts > freeSeats);

        using var gate = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, Attempts).Select(async i =>
        {
            var client = owner.Client.Clone();
            var email = World.UniqueEmail($"koltuk{i}");
            await gate.WaitAsync();
            var response = await client.PostAsync($"/api/t/{tenant}/invites", new { email, role = "Agent" });
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
        }).ToList();
        gate.Release(Attempts);
        var results = await Task.WhenAll(tasks);

        // Bekleyen davet koltuk ayırır: boş koltuk kadar davet verilir, gerisi kota yanıtı alır (hız sınırı 429 değil).
        Assert.Equal(freeSeats, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        var rejected = results.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        Assert.Equal(Attempts - freeSeats, rejected.Count);
        Assert.All(rejected, r =>
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Contains("\"code\":\"quota_exceeded\"", r.Body);
            Assert.Contains("\"metric\":\"seats\"", r.Body);
        });
        Assert.Equal((long)freeSeats, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.invites WHERE tenant_id = $1 AND status = 'Pending'", tenant));
    }

    [Fact]
    public async Task Accept_RechecksSeatLimit()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner);
        var invitee = await World.CreateUserAsync();
        await World.InviteAsync(owner, tenant, invitee.Email, TenantRole.Agent);
        var token = await World.InviteTokenAsync(invitee);

        // Davet verildikten sonra şirket limit üstüne çıkmış olsun (ör. plan düşürme): kabul anında yeniden kontrol edilir.
        var limit = (await owner.Client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK))["entitlements"]!["seatLimit"].Int();
        for (var i = 1; i < limit; i++)
        {
            await World.CreateMemberAsync(tenant, TenantRole.Viewer);
        }

        var problem = await invitee.Client.PostAsync("/api/invites/accept", new { token }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "quota_exceeded");
        Assert.Equal("seats", problem["metric"].Str());
        Assert.Equal(limit, problem["limit"].Int());
        Assert.Equal((long)limit, await ActiveMembersAsync(tenant));
        // Davet tüketilmedi: yer açılınca kabul edilebilir.
        Assert.Equal("Pending", await env.AdminScalarAsync<string>("SELECT status FROM app.invites WHERE tenant_id = $1", tenant));
    }
}
