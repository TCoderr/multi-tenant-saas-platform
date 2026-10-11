using System.Net;
using Kulvar.Platform.Security;
using Kulvar.Platform.Tenancy;

namespace Kulvar.IntegrationTests.Infrastructure;

public sealed record TestUser(Guid Id, string Email, string DisplayName, string Password, ApiClient Client);

/// <summary>
/// Test senaryolarının kurucusu. Kullanıcılar gerçek akışla oluşur (signup → outbox → demo posta kutusu → doğrulama → login).
/// Adlar/e-postalar her çağrıda benzersizdir; testler aynı veritabanını paylaşsa da birbirinin verisine dokunmaz.
/// </summary>
public sealed class World(TestEnvironment env)
{
    private int _sequence;

    public const string DefaultPassword = "Kulvar-Test-Parola-1!";

    public KulvarApp App => env.App;

    public string UniqueEmail(string prefix = "user") =>
        $"{prefix}.{Interlocked.Increment(ref _sequence)}.{Guid.NewGuid():N}@kulvar-test.example";

    /// <summary>Signup + giriş. E-posta doğrulanmamıştır.</summary>
    public async Task<TestUser> CreateUnverifiedUserAsync(string displayName = "Test Kullanıcı", string? email = null, KulvarApp? app = null)
    {
        app ??= env.App;
        email ??= UniqueEmail();
        var client = app.NewClient();
        await client.PostAsync("/api/auth/signup", new { email, displayName, password = DefaultPassword }).ShouldBeAsync(HttpStatusCode.Accepted);
        await client.LoginAsync(email, DefaultPassword).ShouldBeAsync(HttpStatusCode.OK);
        var me = await client.GetAsync("/api/me").ShouldBeAsync(HttpStatusCode.OK);
        return new TestUser(me["id"].Guid(), email, displayName, DefaultPassword, client);
    }

    /// <summary>Signup + doğrulama bağlantısını demo posta kutusundan alıp doğrulama + giriş.</summary>
    public async Task<TestUser> CreateUserAsync(string displayName = "Test Kullanıcı", string? email = null, KulvarApp? app = null)
    {
        app ??= env.App;
        var user = await CreateUnverifiedUserAsync(displayName, email, app);
        await app.DrainOutboxAsync();
        var token = await LatestMailTokenAsync(user.Client, "email_verification");
        await user.Client.PostAsync("/api/auth/verify-email", new { token }).ShouldBeAsync(HttpStatusCode.NoContent);
        return user;
    }

    public async Task<Guid> CreateTenantAsync(TestUser owner, string? name = null)
    {
        var body = await owner.Client.PostAsync("/api/tenants", new { name = name ?? $"Şirket {Guid.NewGuid():N}"[..20] }).ShouldBeAsync(HttpStatusCode.Created);
        return body["tenantId"].Guid();
    }

    /// <summary>Demo posta kutusundaki en yeni iletinin bağlantısından token'ı çıkarır (kullanıcının kendi oturumuyla, API üzerinden).</summary>
    public static async Task<string> LatestMailTokenAsync(ApiClient client, string kind)
    {
        var list = await client.GetAsync("/api/me/mailbox").ShouldBeAsync(HttpStatusCode.OK);
        var item = list["items"]!.AsArray().FirstOrDefault(i => i!["kind"].Str() == kind)
            ?? throw new InvalidOperationException($"Posta kutusunda '{kind}' iletisi yok.");
        var message = await client.GetAsync($"/api/me/mailbox/{item["id"].Str()}").ShouldBeAsync(HttpStatusCode.OK);
        var path = message["actionPath"].Str();
        return path[(path.IndexOf("#token=", StringComparison.Ordinal) + 7)..];
    }

    /// <summary>
    /// Yalnız test kurulumu: üyeliği superuser ile doğrudan ekler (RLS'i atlar). Davet akışının kendisini sınamayan testlerde
    /// kurulum maliyetini düşürür; davet/kabul akışı kendi testlerinde gerçek uçlarla sınanır.
    /// </summary>
    public async Task AddMemberDirectAsync(Guid tenantId, TestUser user, TenantRole role)
    {
        await env.AdminExecuteAsync(
            """
            INSERT INTO app.memberships (tenant_id, user_id, role, status, created_at, version)
            VALUES ($1, $2, $3, 'Active', $4, 1)
            ON CONFLICT (tenant_id, user_id) DO UPDATE SET role = EXCLUDED.role, status = 'Active', removed_at = NULL, version = app.memberships.version + 1
            """,
            tenantId, user.Id, role.ToString(), env.Clock.GetUtcNow());
    }

    public async Task<TestUser> CreateMemberAsync(Guid tenantId, TenantRole role, string displayName = "Üye")
    {
        var user = await CreateUserAsync(displayName);
        await AddMemberDirectAsync(tenantId, user, role);
        return user;
    }

    /// <summary>
    /// Yalnız test kurulumu: şirketin planını superuser ile doğrudan değiştirir (ödeme akışını sınamayan testler için).
    /// Gerçek plan değişikliği (işlem + sağlayıcı + webhook) Aşama 6 testlerinde uçlar üzerinden sınanır.
    /// </summary>
    public Task SetPlanDirectAsync(Guid tenantId, string planCode, string status = "Active") => env.AdminExecuteAsync(
        """
        UPDATE app.subscriptions
        SET plan_version_id = (SELECT id FROM app.plan_versions WHERE plan_code = $2 AND is_current), status = $3,
            trial_ends_at = NULL, grace_ends_at = NULL, version = version + 1
        WHERE tenant_id = $1
        """, tenantId, planCode, status);

    public Task<int> TicketUsageAsync(Guid tenantId) => env.AdminScalarAsync<int>(
        "SELECT coalesce(sum(used), 0)::int FROM app.usage_counters WHERE tenant_id = $1 AND metric = 'tickets_created'", tenantId);

    /// <summary>Gerçek davet akışı: davet → worker iletiyi üretir → davetli kendi posta kutusundaki bağlantıyla kabul eder.</summary>
    public async Task<Guid> InviteAsync(TestUser inviter, Guid tenantId, string email, TenantRole role)
    {
        var body = await inviter.Client.PostAsync($"/api/t/{tenantId}/invites", new { email, role = role.ToString() }).ShouldBeAsync(HttpStatusCode.Created);
        return body["id"].Guid();
    }

    public async Task<string> InviteTokenAsync(TestUser invitee)
    {
        await env.App.DrainOutboxAsync();
        return await LatestMailTokenAsync(invitee.Client, "invite");
    }

    public async Task<TestUser> InviteMemberAsync(TestUser inviter, Guid tenantId, TenantRole role, string displayName = "Davetli")
    {
        var invitee = await CreateUserAsync(displayName);
        await InviteAsync(inviter, tenantId, invitee.Email, role);
        var token = await InviteTokenAsync(invitee);
        await invitee.Client.PostAsync("/api/invites/accept", new { token }).ShouldBeAsync(HttpStatusCode.OK);
        return invitee;
    }

    public static async Task<System.Text.Json.Nodes.JsonObject> CreateCustomerAsync(ApiClient client, Guid tenantId, string name, string? email = null, string? phone = null, string? companyTitle = null, string? notes = null) =>
        await client.PostAsync($"/api/t/{tenantId}/customers", new { name, email, phone, companyTitle, notes }).ShouldBeAsync(HttpStatusCode.Created);

    public static async Task<System.Text.Json.Nodes.JsonObject> CreateTicketAsync(ApiClient client, Guid tenantId, Guid customerId, string title = "Test talebi", string description = "Açıklama", string priority = "Normal", Guid? assigneeUserId = null, string? idempotencyKey = null) =>
        await client.PostAsync($"/api/t/{tenantId}/tickets", new { customerId, title, description, priority, assigneeUserId }, ("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N")))
            .ShouldBeAsync(HttpStatusCode.Created);

    /// <summary>Bir sahip, bir şirket ve bir müşteri: çoğu talep testi için başlangıç.</summary>
    public async Task<(TestUser Owner, Guid TenantId, Guid CustomerId)> CreateTenantWithCustomerAsync(string tenantName = "Talep Şirketi", string customerName = "Deniz Arslan")
    {
        var owner = await CreateUserAsync("Sahip");
        var tenantId = await CreateTenantAsync(owner, tenantName);
        var customer = await CreateCustomerAsync(owner.Client, tenantId, customerName);
        return (owner, tenantId, customer["id"].Guid());
    }
}
