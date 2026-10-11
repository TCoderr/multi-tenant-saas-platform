using System.Net;
using System.Text.Json.Nodes;

namespace Kulvar.Api.Seeding;

public sealed record DemoUser(string Key, string Email, string DisplayName);

public sealed record DemoMembership(string UserKey, string Role);

public sealed record DemoCustomer(string Name, string? CompanyTitle, string? Email, string? Phone, string? Notes = null);

public sealed record DemoTicket(int CustomerIndex, string AuthorKey, string Title, string Description, string Priority, string[] Actions, string? Comment = null);

public sealed record DemoTenant(string Key, string Name, string OwnerKey, string? PlanCode, DemoMembership[] Members, DemoCustomer[] Customers, DemoTicket[] Tickets);

/// <summary>Demo verisinin tanımı. Tümü sentetiktir: kişiler ve şirketler gerçek değildir, alan adları ayrılmış <c>.test</c> uzantısındadır.</summary>
public static class DemoData
{
    public const string OperatorKey = "operator";

    public static readonly DemoUser[] Users =
    [
        new("ayse.owner", "ayse.owner@akasya.test", "Ayşe Yıldız"),
        new("ali.admin", "ali.admin@akasya.test", "Ali Demir"),
        new("asli.agent", "asli.agent@akasya.test", "Aslı Kaya"),
        new("arda.viewer", "arda.viewer@akasya.test", "Arda Şahin"),
        new("burak.owner", "burak.owner@boreas.test", "Burak Aydın"),
        new("belma.agent", "belma.agent@boreas.test", "Belma Çelik"),
        new("cem.ortak", "cem.ortak@serbest.test", "Cem Arslan"),
        new(OperatorKey, "operator@kulvar.test", "Platform Operatörü"),
    ];

    public static readonly DemoTenant[] Tenants =
    [
        // A: Team planında (5 üye); B: denemede (3 üye, koltuk limiti dolu). Cem iki şirkette farklı rolle çalışır.
        new("akasya", "Akasya Mobilya", "ayse.owner", "team",
        [
            new("ali.admin", "Admin"), new("asli.agent", "Agent"), new("arda.viewer", "Viewer"), new("cem.ortak", "Agent"),
        ],
        [
            new("Deniz Arslan", "Arslan Mimarlık Ltd. Şti.", "deniz.arslan@musteri-a.test", "+90 555 010 01 01", "Kurumsal müşteri; teslimatlar hafta içi."),
            new("Elif Korkmaz", null, "elif.korkmaz@musteri-a.test", "+90 555 010 01 02"),
            new("Fırat Öztürk", "Öztürk Otelcilik A.Ş.", "firat.ozturk@musteri-a.test", "+90 555 010 01 03", "Toplu sipariş verir."),
            new("Gül Şimşek", null, "gul.simsek@musteri-a.test", null),
            new("Hakan Çetin", "Çetin Ofis Mobilyaları", "hakan.cetin@musteri-a.test", "+90 555 010 01 05"),
            new("İpek Yalçın", null, null, "+90 555 010 01 06", "E-posta adresi yok; telefonla aranır."),
        ],
        [
            new(0, "asli.agent", "Yemek masası teslimatı gecikti", "Sipariş üç gündür kargoda görünüyor; müşteri teslim tarihi istiyor.", "High", ["Start"], "Kargo firmasıyla görüşüldü, yarın teslim edilecek."),
            new(2, "ali.admin", "Otel lobisi için 12 koltukluk teklif", "Aynı modelden 12 adet için fiyat ve teslim süresi soruluyor.", "Normal", []),
            new(1, "asli.agent", "Sandalye ayağı kırık geldi", "Kutu hasarsız ama ayak çatlak. Değişim talep ediyor.", "Urgent", ["Start", "Resolve"], "Yeni ürün gönderildi, eski ürün kargo ile alınacak."),
            new(3, "cem.ortak", "Fatura adresi düzeltilecek", "Faturadaki ilçe adı yanlış yazılmış.", "Low", ["Resolve", "Close"]),
            new(4, "cem.ortak", "Çalışma masası montaj kılavuzu eksik", "Kutudan kılavuz çıkmadı; PDF isteniyor.", "Normal", ["Start"]),
            new(0, "ayse.owner", "Kumaş numunesi talebi", "Üç farklı renk için numune gönderimi isteniyor.", "Low", []),
            new(5, "asli.agent", "Teslimat saati değişikliği", "Müşteri teslimatı öğleden sonraya almak istiyor.", "Normal", ["Resolve"], "Dağıtım ekibine bildirildi."),
        ]),
        new("boreas", "Boreas Yazılım", "burak.owner", null,
        [
            new("belma.agent", "Agent"), new("cem.ortak", "Admin"),
        ],
        [
            new("Kerem Aksoy", "Aksoy Lojistik A.Ş.", "kerem.aksoy@musteri-b.test", "+90 555 020 02 01", "Yıllık bakım sözleşmesi var."),
            new("Lale Doğan", null, "lale.dogan@musteri-b.test", "+90 555 020 02 02"),
            new("Mert Kaplan", "Kaplan Eczanesi", "mert.kaplan@musteri-b.test", null),
        ],
        [
            new(0, "belma.agent", "Raporlar sayfası açılmıyor", "Aylık rapor ekranı beyaz sayfa veriyor.", "High", ["Start"], "Tarayıcı önbelleği temizlenince düzeldi mi diye soruldu."),
            new(1, "cem.ortak", "Yeni kullanıcı ekleme isteği", "Muhasebe ekibi için iki kullanıcı daha tanımlanacak.", "Normal", []),
            new(2, "belma.agent", "Barkod okuyucu tanınmıyor", "Güncellemeden sonra okuyucu çalışmıyor.", "Urgent", ["Start", "Resolve"]),
            new(0, "burak.owner", "Sözleşme yenileme hatırlatması", "Bakım sözleşmesi önümüzdeki ay bitiyor.", "Low", []),
        ]),
    ];
}

public sealed class SeedException(string message) : Exception(message);

/// <summary>
/// Demo seed: yalnız Development/Testing ortamında, açık opt-in ile çalışır. Uygulamanın kendi HTTP uçlarını kullanır ve
/// yinelenebilir (idempotent): var olan kullanıcı/şirket/üyelik yeniden oluşturulmaz. Parolalar çağırandan gelir; loglanmaz.
/// </summary>
public sealed class DemoSeeder(Func<HttpClient> clientFactory, Func<string, string?> passwordFor, ILogger log, Func<CancellationToken, Task>? pump = null)
{
    private readonly Dictionary<string, SeedHttpSession> _sessions = [];
    private readonly Dictionary<string, Guid> _userIds = [];

    public IReadOnlyDictionary<string, Guid> TenantIds => _tenantIds;
    private readonly Dictionary<string, Guid> _tenantIds = [];

    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var user in DemoData.Users)
        {
            await EnsureUserAsync(user, ct);
        }
        foreach (var tenant in DemoData.Tenants)
        {
            var tenantId = _tenantIds[tenant.Key] = await EnsureTenantAsync(tenant, ct);
            if (tenant.PlanCode is not null)
            {
                await EnsurePlanAsync(tenant, tenantId, ct);
            }
            foreach (var member in tenant.Members)
            {
                await EnsureMemberAsync(tenant, tenantId, member, ct);
            }
            await EnsureContentAsync(tenant, tenantId, ct);
        }
        log.LogInformation("Demo seed tamam: {Users} kullanıcı, {Tenants} şirket", DemoData.Users.Length, DemoData.Tenants.Length);
    }

    /// <summary>Plan, gerçek akışla değişir: Owner ister, simüle sağlayıcı onaylar, worker uygular.</summary>
    private async Task EnsurePlanAsync(DemoTenant tenant, Guid tenantId, CancellationToken ct)
    {
        var owner = _sessions[tenant.OwnerKey];
        var (_, workspace) = await owner.GetAsync($"/api/t/{tenantId}/", ct);
        if (workspace["subscription"]!["planCode"]!.GetValue<string>() == tenant.PlanCode)
        {
            return;
        }
        var (status, _) = await owner.PostAsync($"/api/t/{tenantId}/billing/plan-change", new { planCode = tenant.PlanCode }, ct, ("Idempotency-Key", $"seed-plan-{tenant.Key}-{tenant.PlanCode}"));
        if (status is not (HttpStatusCode.Accepted or HttpStatusCode.Conflict))
        {
            throw new SeedException($"'{tenant.Key}' için plan değişikliği istenemedi ({(int)status}).");
        }
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (pump is not null)
            {
                await pump(ct);
            }
            (_, workspace) = await owner.GetAsync($"/api/t/{tenantId}/", ct);
            if (workspace["subscription"]!["planCode"]!.GetValue<string>() == tenant.PlanCode)
            {
                return;
            }
            if (pump is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        throw new SeedException($"'{tenant.Key}' planı uygulanmadı. Worker çalışıyor mu?");
    }

    /// <summary>Üyelik gerçek davet akışıyla kurulur: davet → demo posta kutusu → kabul.</summary>
    private async Task EnsureMemberAsync(DemoTenant tenant, Guid tenantId, DemoMembership membership, CancellationToken ct)
    {
        var owner = _sessions[tenant.OwnerKey];
        var invitee = _sessions[membership.UserKey];
        var email = DemoData.Users.Single(u => u.Key == membership.UserKey).Email;
        var (_, members) = await owner.GetAsync($"/api/t/{tenantId}/members", ct);
        if (members["items"]!.AsArray().Any(m => m!["userId"]!.GetValue<string>() == _userIds[membership.UserKey].ToString()))
        {
            return;
        }
        var (inviteStatus, problem) = await owner.PostAsync($"/api/t/{tenantId}/invites", new { email, role = membership.Role }, ct);
        var alreadyPending = inviteStatus == HttpStatusCode.Conflict && problem["code"]?.GetValue<string>() == "invite_already_pending";
        if (inviteStatus != HttpStatusCode.Created && !alreadyPending)
        {
            throw new SeedException($"'{membership.UserKey}' '{tenant.Key}' şirketine davet edilemedi ({(int)inviteStatus}).");
        }
        // Posta kutusundaki en yeni davetlerden bu şirkete ait olanı bulunur (kullanıcının başka şirketlerden eski davetleri olabilir).
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (pump is not null)
            {
                await pump(ct);
            }
            var (_, list) = await invitee.GetAsync("/api/me/mailbox", ct);
            foreach (var item in list["items"]!.AsArray().Where(i => i!["kind"]!.GetValue<string>() == "invite"))
            {
                var (_, message) = await invitee.GetAsync($"/api/me/mailbox/{item!["id"]!.GetValue<string>()}", ct);
                var path = message["actionPath"]!.GetValue<string>();
                var token = path[(path.IndexOf("#token=", StringComparison.Ordinal) + 7)..];
                var (acceptStatus, accepted) = await invitee.PostAsync("/api/invites/accept", new { token }, ct);
                if (acceptStatus == HttpStatusCode.OK && accepted["tenantId"]!.GetValue<string>() == tenantId.ToString())
                {
                    return;
                }
            }
            if (pump is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        throw new SeedException($"'{membership.UserKey}' '{tenant.Key}' daveti kabul edilemedi. Worker çalışıyor mu?");
    }

    /// <summary>Sentetik müşteri ve talepler. Şirkette müşteri varsa dokunulmaz (yinelenebilir).</summary>
    private async Task EnsureContentAsync(DemoTenant tenant, Guid tenantId, CancellationToken ct)
    {
        var owner = _sessions[tenant.OwnerKey];
        var (_, existing) = await owner.GetAsync($"/api/t/{tenantId}/customers?limit=1", ct);
        if (existing["items"]!.AsArray().Count > 0)
        {
            return;
        }
        var customerIds = new List<string>();
        foreach (var customer in tenant.Customers)
        {
            var (status, created) = await owner.PostAsync($"/api/t/{tenantId}/customers",
                new { name = customer.Name, companyTitle = customer.CompanyTitle, email = customer.Email, phone = customer.Phone, notes = customer.Notes }, ct);
            if (status != HttpStatusCode.Created)
            {
                throw new SeedException($"'{tenant.Key}' demo müşterisi oluşturulamadı ({(int)status}).");
            }
            customerIds.Add(created["id"]!.GetValue<string>());
        }
        for (var i = 0; i < tenant.Tickets.Length; i++)
        {
            var ticket = tenant.Tickets[i];
            var author = _sessions[ticket.AuthorKey];
            var (status, created) = await author.PostAsync($"/api/t/{tenantId}/tickets",
                new { customerId = customerIds[ticket.CustomerIndex], title = ticket.Title, description = ticket.Description, priority = ticket.Priority },
                ct, ("Idempotency-Key", $"seed-{tenant.Key}-ticket-{i}"));
            if (status != HttpStatusCode.Created)
            {
                throw new SeedException($"'{tenant.Key}' demo talebi oluşturulamadı ({(int)status}).");
            }
            var ticketId = created["id"]!.GetValue<string>();
            var version = created["version"]!.GetValue<int>();
            foreach (var action in ticket.Actions)
            {
                var (transitionStatus, transitioned) = await author.PostAsync($"/api/t/{tenantId}/tickets/{ticketId}/transition", new { action }, ct, ("If-Match", $"\"{version}\""));
                if (transitionStatus != HttpStatusCode.OK)
                {
                    throw new SeedException($"'{tenant.Key}' demo talebi '{action}' geçişi başarısız ({(int)transitionStatus}).");
                }
                version = transitioned["version"]!.GetValue<int>();
            }
            if (ticket.Comment is not null && !ticket.Actions.Contains("Close"))
            {
                await author.PostAsync($"/api/t/{tenantId}/tickets/{ticketId}/comments", new { body = ticket.Comment }, ct);
            }
        }
    }

    public SeedHttpSession SessionOf(string userKey) => _sessions[userKey];

    private async Task EnsureUserAsync(DemoUser user, CancellationToken ct)
    {
        var password = passwordFor(user.Key);
        if (string.IsNullOrEmpty(password))
        {
            throw new SeedException($"Demo kullanıcısı '{user.Key}' için parola dosyası yok.");
        }

        var session = new SeedHttpSession(clientFactory());
        if (!await session.LoginAsync(user.Email, password, ct))
        {
            var (signupStatus, _) = await session.PostAsync("/api/auth/signup", new { email = user.Email, displayName = user.DisplayName, password }, ct);
            if (signupStatus != HttpStatusCode.Accepted || !await session.LoginAsync(user.Email, password, ct))
            {
                // Hesap başka parolayla zaten var olabilir; yanıt genel olduğundan ayırt edilemez.
                throw new SeedException($"Demo kullanıcısı '{user.Key}' oluşturulamadı veya mevcut hesabın parolası farklı.");
            }
        }

        var (_, me) = await session.GetAsync("/api/me", ct);
        if (!me["emailVerified"]!.GetValue<bool>())
        {
            var token = await WaitForMailTokenAsync(session, "email_verification", ct, resendPath: "/api/auth/verify-email/resend");
            var (verifyStatus, _) = await session.PostAsync("/api/auth/verify-email", new { token }, ct);
            if (verifyStatus != HttpStatusCode.NoContent)
            {
                throw new SeedException($"Demo kullanıcısı '{user.Key}' doğrulanamadı ({(int)verifyStatus}).");
            }
        }
        _sessions[user.Key] = session;
        _userIds[user.Key] = Guid.Parse(me["id"]!.GetValue<string>());
    }

    private async Task<Guid> EnsureTenantAsync(DemoTenant tenant, CancellationToken ct)
    {
        var owner = _sessions[tenant.OwnerKey];
        var (_, memberships) = await owner.GetAsync("/api/me/memberships", ct);
        var existing = memberships["items"]!.AsArray().FirstOrDefault(m => m!["tenantName"]!.GetValue<string>() == tenant.Name && m["role"]!.GetValue<string>() == "Owner");
        if (existing is not null)
        {
            return Guid.Parse(existing["tenantId"]!.GetValue<string>());
        }
        var (status, created) = await owner.PostAsync("/api/tenants", new { name = tenant.Name }, ct);
        if (status != HttpStatusCode.Created)
        {
            throw new SeedException($"Demo şirketi '{tenant.Key}' oluşturulamadı ({(int)status}).");
        }
        return Guid.Parse(created["tenantId"]!.GetValue<string>());
    }

    /// <summary>Demo posta kutusunda ilgili iletiyi bekler (worker üretir). Sınırlı deneme; gerekirse yeniden gönderim ister.</summary>
    private async Task<string> WaitForMailTokenAsync(SeedHttpSession session, string kind, CancellationToken ct, string? resendPath = null, Func<JsonNode, bool>? match = null)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (pump is not null)
            {
                await pump(ct);
            }
            var (_, list) = await session.GetAsync("/api/me/mailbox", ct);
            var item = list["items"]!.AsArray().FirstOrDefault(i => i!["kind"]!.GetValue<string>() == kind && (match is null || match(i)));
            if (item is not null)
            {
                var (_, message) = await session.GetAsync($"/api/me/mailbox/{item["id"]!.GetValue<string>()}", ct);
                var path = message["actionPath"]!.GetValue<string>();
                return path[(path.IndexOf("#token=", StringComparison.Ordinal) + 7)..];
            }
            if (attempt == 10 && resendPath is not null)
            {
                await session.PostAsync(resendPath, null, ct);
            }
            if (pump is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        throw new SeedException($"Demo posta kutusunda '{kind}' iletisi görünmedi. Worker çalışıyor mu?");
    }
}
