using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Kulvar.Api.Hosting;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Modules.Operations;
using Kulvar.Platform.Observability;
using Kulvar.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Kulvar.IntegrationTests.Stage9;

/// <summary>Kişisel veri ve sırların log, iz ve metriklere sızmadığının doğrulanması: gerçek akışlar kanarya değerlerle koşturulur.</summary>
public sealed class ObservabilityTests(TestEnvironment env)
{
    private World World => env.World;

    private sealed record Canaries(List<string> Values)
    {
        public void AssertAbsent(string surface, IEnumerable<string> lines)
        {
            var text = string.Join("\n", lines);
            foreach (var canary in Values.Where(v => v.Length >= 6))
            {
                Assert.False(text.Contains(canary, StringComparison.OrdinalIgnoreCase), $"{surface} içinde kişisel veri/sır: '{canary[..Math.Min(12, canary.Length)]}…'");
            }
        }
    }

    /// <summary>Kimlik, müşteri, talep, davet, export, faturalama ve hata yollarını kanarya değerlerle dolaşır; kullanılan gizli/kişisel değerleri döner.</summary>
    private async Task<Canaries> RunCanaryJourneyAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var values = new List<string>();
        string C(string value)
        {
            values.Add(value);
            return value;
        }

        var email = C($"piicanary.{tag}@kulvar-test.example");
        var password = C($"PIICANARY-Parola-{tag}!");
        var client = env.App.NewClient();
        await client.PostAsync("/api/auth/signup", new { email, displayName = C($"PIICANARY Kişi {tag}"), password }).ShouldBeAsync(HttpStatusCode.Accepted);
        await client.LoginAsync(email, C($"PIICANARY-Yanlis-{tag}!")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "invalid_credentials");
        await client.LoginAsync(email, password).ShouldBeAsync(HttpStatusCode.OK);
        values.AddRange(client.Cookies.Values);
        values.Add(client.CsrfToken!);
        await env.App.DrainOutboxAsync();
        var verification = C(await World.LatestMailTokenAsync(client, "email_verification"));
        await client.PostAsync("/api/auth/verify-email", new { token = verification }).ShouldBeAsync(HttpStatusCode.NoContent);
        var userId = (await client.GetAsync("/api/me").ShouldBeAsync(HttpStatusCode.OK))["id"].Guid();
        var owner = new TestUser(userId, email, "x", password, client);

        var tenant = await World.CreateTenantAsync(owner, $"Gözlem Şirketi {tag}");
        await World.SetPlanDirectAsync(tenant, "team");
        var customer = await World.CreateCustomerAsync(client, tenant, C($"PIICANARY Müşteri {tag}"), C($"piicanary.musteri.{tag}@ornek.test"), C($"+90 555 7{tag[..2].Select(ch => (char)('0' + ch % 10)).Aggregate(string.Empty, (a, b) => a + b)}3 91 77"), notes: C($"PIICANARY not {tag}"));
        var customerId = customer["id"].Guid();
        await client.GetAsync($"/api/t/{tenant}/customers?q={Uri.EscapeDataString(C($"piicanary arama {tag}"))}").ShouldBeAsync(HttpStatusCode.OK);
        // Çakışma (aynı e-posta) ve doğrulama hataları: hata yolları da kişisel veri loglamamalı.
        await client.PostAsync($"/api/t/{tenant}/customers", new { name = "İkinci", email = values[^4] }).ShouldBeProblemAsync(HttpStatusCode.Conflict, "customer_email_exists");
        await client.PostAsync($"/api/t/{tenant}/customers", new { name = "", email = C($"piicanary-bozuk-{tag}") }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");

        var ticket = await World.CreateTicketAsync(client, tenant, customerId, C($"PIICANARY başlık {tag}"), C($"PIICANARY açıklama {tag}"));
        await client.PostAsync($"/api/t/{tenant}/tickets/{ticket["id"].Str()}/comments", new { body = C($"PIICANARY yorum {tag}") }).ShouldBeAsync(HttpStatusCode.Created);

        var inviteEmail = C($"piicanary.davetli.{tag}@kulvar-test.example");
        await World.InviteAsync(owner, tenant, inviteEmail, TenantRole.Agent);
        var invitee = await World.CreateUserAsync("Davetli", inviteEmail);
        var inviteToken = C(await World.InviteTokenAsync(invitee));
        await invitee.Client.PostAsync("/api/invites/accept", new { token = inviteToken }).ShouldBeAsync(HttpStatusCode.OK);

        // Export + indirme token'ı (sorgu dizesinde taşınır).
        var export = (await client.PostAsync($"/api/t/{tenant}/exports").ShouldBeAsync(HttpStatusCode.Accepted))["id"].Guid();
        await env.App.Services.GetRequiredService<ExportRunner>().RunOnceAsync(CancellationToken.None);
        var url = (await client.PostAsync($"/api/t/{tenant}/exports/{export}/download-link").ShouldBeAsync(HttpStatusCode.OK))["url"].Str();
        C(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);

        // Faturalama + webhook (geçersiz imza dahil) + sunucu hatası yolu.
        var billing = new Stage6.BillingHarness(env);
        await billing.RequestAsync(client, tenant, "starter");
        await billing.SubmitAsync();
        await billing.SendWebhookAsync(Stage6.BillingHarness.EventJson("subscription.renewed", C($"psub_piicanary_{tag}"), 1), secret: C($"piicanary-yanlis-sir-{tag}-0123456789abcdef")).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "invalid_signature");
        await env.App.DrainOutboxAsync();
        await client.LogoutAsync().ShouldBeAsync(HttpStatusCode.NoContent);
        values.Add(KulvarApp.WebhookSecret);
        return new Canaries(values);
    }

    [Fact]
    public async Task Logs_DoNotContain_PiiCanaries()
    {
        env.Logs.Clear();
        var canaries = await RunCanaryJourneyAsync();
        var lines = env.Logs.Snapshot();

        // Test boş geçmesin: istek logları gerçekten yazılmış olmalı ve kimlik/şablon içermeli.
        Assert.Contains(lines, l => l.Contains("Kulvar.Request") && l.Contains("/api/t/{tenantId:guid}/customers") && l.Contains("201"));
        Assert.Contains(lines, l => l.Contains("Kulvar.Request") && l.Contains("/api/auth/login") && l.Contains("401"));
        // Hata yolunda da şablon ve şirket kimliği korunur (hata işleyici rota bilgisini istekten siler).
        Assert.Contains(lines, l => l.Contains("Kulvar.Request") && l.Contains("/api/t/{tenantId:guid}/customers") && l.Contains("409") && !l.Contains("tenant=(null)") && !l.EndsWith("tenant=", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("Kulvar.Request") && l.Contains("(eşleşmedi)"));
        canaries.AssertAbsent("log", lines);
        // Ham sorgu dizesi veya istek gövdesi loglanmaz.
        Assert.DoesNotContain(lines, l => l.Contains("?q=", StringComparison.Ordinal) || l.Contains("token=", StringComparison.Ordinal) || l.Contains("\"password\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Activities_DoNotContain_PiiCanaries()
    {
        var collected = new ConcurrentBag<string>();
        var sources = new ConcurrentDictionary<string, int>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true, // Kulvar, ASP.NET Core, Npgsql, HttpClient: hepsi.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                sources.AddOrUpdate(activity.Source.Name, 1, (_, n) => n + 1);
                collected.Add(activity.DisplayName);
                foreach (var tag in activity.TagObjects)
                {
                    collected.Add($"{tag.Key}={tag.Value}");
                }
                foreach (var e in activity.Events)
                {
                    collected.Add(e.Name + " " + string.Join(" ", e.Tags.Select(t => $"{t.Key}={t.Value}")));
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var canaries = await RunCanaryJourneyAsync();

        Assert.True(sources.ContainsKey(Telemetry.SourceName), "Kulvar izleri dinlenmedi.");
        Assert.True(collected.Count > 50);
        canaries.AssertAbsent("iz", collected);
    }

    [Fact]
    public async Task MetricTags_AreFromFixedSets()
    {
        var tags = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>();
        var instruments = new ConcurrentDictionary<string, byte>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Telemetry.SourceName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, measurementTags, _) =>
        {
            instruments[instrument.Name] = 1;
            foreach (var tag in measurementTags)
            {
                tags.GetOrAdd(tag.Key, _ => new ConcurrentDictionary<string, byte>())[tag.Value?.ToString() ?? string.Empty] = 1;
            }
        });
        listener.Start();

        await RunCanaryJourneyAsync();
        // Kota reddi ve üyesi olunmayan şirket erişimi de ölçülsün.
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync();
        await env.AdminExecuteAsync("INSERT INTO app.usage_counters (tenant_id, metric, period_start, used) VALUES ($1, 'tickets_created', $2, 20)",
            tenant, new DateOnly(env.Clock.GetUtcNow().Year, env.Clock.GetUtcNow().Month, 1));
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets", new { customerId, title = "x", description = "x" }, ("Idempotency-Key", "metric")).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "quota_exceeded");
        await owner.Client.GetAsync($"/api/t/{Guid.NewGuid()}/").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");

        Assert.Contains("kulvar.outbox.processed", instruments.Keys);
        Assert.Contains("kulvar.quota.rejections", instruments.Keys);
        Assert.Contains("kulvar.tenant.access_denied", instruments.Keys);
        Assert.Contains("kulvar.auth.login_attempts", instruments.Keys);

        // Etiket adları sabit küme; şirket/kullanıcı/müşteri kimliği etiket değildir.
        var allowedKeys = new HashSet<string> { "type", "outcome", "metric", "reason" };
        Assert.Subset(allowedKeys, tags.Keys.ToHashSet());
        foreach (var (key, values) in tags)
        {
            Assert.True(values.Count <= 25, $"'{key}' etiketi yüksek kardinaliteli: {values.Count} değer.");
            foreach (var value in values.Keys)
            {
                Assert.False(Guid.TryParse(value, out _), $"'{key}' etiketinde kimlik: {value}");
                Assert.DoesNotContain("@", value);
                Assert.Matches("^[A-Za-z0-9_.:]{1,60}$", value);
            }
        }
    }
}

/// <summary>Platform operatörü: yalnız metadata; şirket içeriğine ne uçlardan ne veritabanı rolünden erişebilir.</summary>
public sealed class PlatformTests(TestEnvironment env)
{
    private World World => env.World;

    private async Task<TestUser> OperatorAsync()
    {
        var user = await World.CreateUserAsync("Platform Operatörü Test");
        await using var owner = NpgsqlDataSource.Create(env.Postgres.Owner());
        Assert.True(await OperatorSetup.GrantAsync(owner, user.Email, env.Clock.GetUtcNow(), CancellationToken.None));
        return user;
    }

    [Fact]
    public async Task NonOperator_Gets404_OnEveryPlatformEndpoint()
    {
        var user = await World.CreateUserAsync();
        var anyId = Guid.NewGuid();
        foreach (var path in new[] { "/api/platform/tenants", "/api/platform/billing/operations", "/api/platform/jobs/dead", "/api/platform/audit" })
        {
            await user.Client.GetAsync(path).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
            await env.App.NewClient().GetAsync(path).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        }
        await user.Client.PostAsync($"/api/platform/tenants/{anyId}/suspend", new { reason = "yetkisiz deneme" }).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await user.Client.PostAsync($"/api/platform/jobs/{anyId}/replay").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await user.Client.PostAsync("/api/platform/simulation/provider-event", new { tenantId = anyId, kind = "renewed" }).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Operator_SeesMetadataOnly_NoContentCanaries()
    {
        const string Canary = "CANARYOPS";
        var owner = await World.CreateUserAsync($"{Canary} Sahip");
        var tenant = await World.CreateTenantAsync(owner, "Operatörün Göreceği Ad");
        var customer = (await World.CreateCustomerAsync(owner.Client, tenant, $"{Canary} Müşteri", "canaryops@ornek.test", "+90 555 444 33 22", notes: $"{Canary} not"))["id"].Guid();
        var ticket = (await World.CreateTicketAsync(owner.Client, tenant, customer, $"{Canary} talep", $"{Canary} açıklama"))["id"].Guid();
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets/{ticket}/comments", new { body = $"{Canary} yorum" }).ShouldBeAsync(HttpStatusCode.Created);
        await World.InviteAsync(owner, tenant, "canaryops.davetli@kulvar-test.example", TenantRole.Agent);
        await env.App.DrainOutboxAsync();
        var op = await OperatorAsync();

        // Platform yüzeyleri: şirket adı/durumu/planı görünür; içerik ve kişisel veri görünmez.
        var tenants = await op.Client.GetAsync("/api/platform/tenants?limit=100").ShouldBeAsync(HttpStatusCode.OK);
        var listed = tenants["items"]!.AsArray().Single(t => t!["id"].Guid() == tenant)!;
        Assert.Equal("Operatörün Göreceği Ad", listed["name"].Str());
        Assert.Equal("Active", listed["status"].Str());
        Assert.Equal("trial", listed["planCode"].Str());
        foreach (var path in new[] { "/api/platform/tenants?limit=100", "/api/platform/billing/operations", "/api/platform/jobs/dead", "/api/platform/audit?limit=100", "/api/platform/billing/operations?status=Succeeded" })
        {
            var body = await (await op.Client.GetAsync(path)).Content.ReadAsStringAsync();
            Assert.False(body.Contains(Canary, StringComparison.OrdinalIgnoreCase), $"{path} içerik sızdırıyor");
            Assert.DoesNotContain("canaryops", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(owner.Email, body);
            Assert.DoesNotContain("555 444", body);
        }

        // Operatör şirketin üyesi değildir: şirket uçları onun için "yok". Impersonation ucu da yoktur.
        foreach (var path in new[] { "", "customers", $"customers/{customer}", "tickets", $"tickets/{ticket}/timeline", "members", "invites", "audit", "billing", "exports" })
        {
            await op.Client.GetAsync($"/api/t/{tenant}/{path}").ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");
        }
        await op.Client.PostAsync($"/api/platform/tenants/{tenant}/impersonate").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        Assert.Empty((await op.Client.GetAsync("/api/me/memberships").ShouldBeAsync(HttpStatusCode.OK))["items"]!.AsArray());

        // Uçların arkasındaki veritabanı rolü de içerik tablolarına erişemez (kod hatası olsa bile).
        await using var platform = await RoleSession.OpenAsync(env.Postgres.Platform());
        foreach (var table in Stage2.SchemaCatalog.PlatformForbidden)
        {
            Assert.Equal("42501", (await platform.TryExecAsync($"SELECT 1 FROM app.{table} LIMIT 1")).SqlState);
        }
    }

    [Fact]
    public async Task Suspend_LocksContent_MembersSeeStatusOnly_ReactivateRestores_Audited()
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync("Askıya Alınacak");
        var op = await OperatorAsync();

        await op.Client.PostAsync($"/api/platform/tenants/{tenant}/suspend", new { reason = "kısa" }).ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        await op.Client.PostAsync($"/api/platform/tenants/{tenant}/reactivate", new { reason = "Askıda olmayan şirketi açma denemesi" }).ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid_tenant_state");
        await op.Client.PostAsync($"/api/platform/tenants/{tenant}/suspend", new { reason = "Kötüye kullanım incelemesi (test)" }).ShouldBeAsync(HttpStatusCode.OK);

        // Üyeler yalnız durum özetini görür; içerik (okuma dahil) kapalıdır. Giriş ve diğer şirketleri etkilenmez.
        var summary = await owner.Client.GetAsync($"/api/t/{tenant}/").ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal("Locked", summary["accessMode"].Str());
        Assert.Equal("Suspended", summary["tenant"]!["status"].Str());
        Assert.Equal(["members.leave", "workspace.read"], summary["permissions"]!.AsArray().Select(p => p.Str()).ToList());
        await owner.Client.GetAsync($"/api/t/{tenant}/customers").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "access_mode_restricted");
        await owner.Client.GetAsync($"/api/t/{tenant}/tickets").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "access_mode_restricted");
        await owner.Client.PostAsync($"/api/t/{tenant}/customers", new { name = "Askıdayken" }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "access_mode_restricted");
        await owner.Client.PostAsync($"/api/t/{tenant}/deletion", new { confirmName = "Askıya Alınacak", password = owner.Password }).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "access_mode_restricted");
        await owner.Client.GetAsync("/api/me").ShouldBeAsync(HttpStatusCode.OK);
        var other = await World.CreateTenantAsync(owner, "Etkilenmeyen Şirket");
        await World.CreateCustomerAsync(owner.Client, other, "Diğer şirkette çalışmaya devam");

        await op.Client.PostAsync($"/api/platform/tenants/{tenant}/suspend", new { reason = "İkinci kez askıya alma denemesi" }).ShouldBeProblemAsync(HttpStatusCode.Conflict, "invalid_tenant_state");
        await op.Client.PostAsync($"/api/platform/tenants/{tenant}/reactivate", new { reason = "İnceleme tamamlandı (test)" }).ShouldBeAsync(HttpStatusCode.OK);
        await owner.Client.GetAsync($"/api/t/{tenant}/customers/{customerId}").ShouldBeAsync(HttpStatusCode.OK);

        var audit = await op.Client.GetAsync("/api/platform/audit?limit=100").ShouldBeAsync(HttpStatusCode.OK);
        var actions = audit["items"]!.AsArray().Where(a => a!["tenantId"]?.GetValue<string>() == tenant.ToString()).Select(a => a!["action"].Str()).ToList();
        Assert.Contains("tenant.suspended", actions);
        Assert.Contains("tenant.reactivated", actions);
    }
}

public sealed class OutageAndTransportTests(TestEnvironment env)
{
    [Fact]
    public async Task DatabaseUnreachable_Returns503_NeverSuccess()
    {
        // Var olmayan bir veritabanı uç noktasına bağlı API: her veritabanı işlemi hemen başarısız olur.
        // (Gerçek kesinti ve toparlanma — 503 → 200 — Docker yığın duman testinde doğrulanır.)
        var unreachable = "Host=127.0.0.1;Port=1;Database=kulvar;Username=kulvar_app;Password=x;Timeout=1;Command Timeout=2";
        await using var app = env.NewApp(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{Composition.AppConnection}"] = unreachable,
            [$"ConnectionStrings:{Composition.PlatformConnection}"] = unreachable,
        });
        var client = app.NewClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        await client.GetAsync("/health/ready").ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
        // Kimlik: ne başarı (200/202) ne de yanlış "hatalı parola" (401) — bağımlılık hatası.
        await client.PostAsync("/api/auth/login", new { email = "biri@kulvar-test.example", password = "Herhangi-Bir-Parola-1!" }).ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
        await client.PostAsync("/api/auth/signup", new { email = "yeni@kulvar-test.example", displayName = "Yeni", password = "Herhangi-Bir-Parola-1!" }).ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
        await client.GetAsync("/api/plans").ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
        // Webhook: imza geçerli olsa da olay yazılamadıysa 2xx dönmez (sağlayıcı yeniden dener).
        var body = System.Text.Encoding.UTF8.GetBytes(Stage6.BillingHarness.EventJson("subscription.renewed", "psub_x", 1));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/billing") { Content = new ByteArrayContent(body) };
        request.Headers.TryAddWithoutValidation(Kulvar.Modules.Billing.WebhookSignature.HeaderName, Kulvar.Modules.Billing.WebhookSignature.Sign(KulvarApp.WebhookSecret, env.Clock.GetUtcNow().ToUnixTimeSeconds(), body));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.Http.SendAsync(request)).StatusCode);

        // Geçerli bir oturum cookie'si olan istek de 503 alır (oturum doğrulanamadı diye 401 veya sahte 200 değil).
        var user = await env.World.CreateUnverifiedUserAsync();
        var withSession = app.NewClient();
        foreach (var (name, value) in user.Client.Cookies)
        {
            withSession.Cookies[name] = value;
        }
        await withSession.GetAsync("/api/me").ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
        // Yanıt iç ayrıntı (bağlantı dizesi, host, port) içermez.
        var problem = await (await client.GetAsync("/health/ready")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("127.0.0.1", problem);
        Assert.DoesNotContain("kulvar_app", problem);
    }

    [Fact]
    public async Task Login_Throttled_429_WithRetryAfter_DistinctFromQuota()
    {
        await using var app = env.NewApp(new Dictionary<string, string?> { ["RateLimits:LoginPerMinute"] = "3" });
        var client = app.NewClient();
        for (var i = 0; i < 3; i++)
        {
            await client.PostAsync("/api/auth/login", new { email = "yok@kulvar-test.example", password = "Yanlis-Parola-12345" }).ShouldBeProblemAsync(HttpStatusCode.Unauthorized, "invalid_credentials");
        }
        var throttled = await client.PostAsync("/api/auth/login", new { email = "yok@kulvar-test.example", password = "Yanlis-Parola-12345" });
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.RetryAfter?.Delta is { TotalSeconds: > 0 and <= 60 });
        var body = await throttled.JsonAsync();
        Assert.Equal("rate_limited", body["code"].Str());
        Assert.Equal("application/problem+json", throttled.Content.Headers.ContentType?.MediaType);
        // Hız sınırı yalnız o kovayı etkiler: diğer uçlar çalışır.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/plans")).StatusCode);
    }

    [Fact]
    public async Task Responses_CarrySecurityHeaders_AndCookiesAreHardened()
    {
        var client = env.App.NewClient();
        foreach (var path in new[] { "/api/plans", "/api/me", "/health/live", "/api/does-not-exist" })
        {
            var response = await client.GetAsync(path);
            var csp = response.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("default-src 'self'", csp);
            Assert.Contains("script-src 'self'", csp);
            Assert.Contains("frame-ancestors 'none'", csp);
            Assert.DoesNotContain("unsafe-inline", csp);
            Assert.DoesNotContain("unsafe-eval", csp);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Contains("max-age=", response.Headers.GetValues("Strict-Transport-Security").Single());
            Assert.False(response.Headers.Contains("Server"));
            Assert.False(response.Headers.Contains("X-Powered-By"));
        }
        Assert.Contains("no-store", (await client.GetAsync("/api/plans")).Headers.CacheControl?.ToString());

        // Cookie'ler: __Host- öneki, HttpOnly, Secure, SameSite, Path=/; Domain yok.
        var email = env.World.UniqueEmail("cookie");
        var fresh = env.App.NewClient();
        await fresh.PostAsync("/api/auth/signup", new { email, displayName = "Cookie", password = World.DefaultPassword }).ShouldBeAsync(HttpStatusCode.Accepted);
        var login = await fresh.PostAsync("/api/auth/login", new { email, password = World.DefaultPassword });
        var csrf = await fresh.GetAsync("/api/auth/csrf");
        var cookies = login.Headers.GetValues("Set-Cookie").Concat(csrf.Headers.TryGetValues("Set-Cookie", out var c) ? c : []).ToList();
        var session = cookies.Single(v => v.StartsWith("__Host-kulvar-session=", StringComparison.Ordinal));
        foreach (var attribute in new[] { "httponly", "secure", "samesite=lax", "path=/" })
        {
            Assert.Contains(attribute, session, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("domain=", session, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", session, StringComparison.OrdinalIgnoreCase); // oturum cookie'si; kalıcı değil
        Assert.All(fresh.Cookies.Keys, name => Assert.StartsWith("__Host-kulvar-", name, StringComparison.Ordinal));
        // Oturum cookie'si kullanıcı/rol/şirket bilgisini açık metin taşımaz (şifreli bilet).
        Assert.DoesNotContain(email, session, StringComparison.OrdinalIgnoreCase);
    }
}
