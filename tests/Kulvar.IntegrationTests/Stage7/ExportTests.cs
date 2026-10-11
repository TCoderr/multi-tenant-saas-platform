using System.IO.Compression;
using System.Net;
using System.Text;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Modules.Operations;
using Kulvar.Platform.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kulvar.IntegrationTests.Stage7;

public sealed class ExportTests(TestEnvironment env)
{
    private World World => env.World;
    private ExportRunner Runner => env.App.Services.GetRequiredService<ExportRunner>();
    private ExportOptions Options => env.App.Services.GetRequiredService<IOptions<ExportOptions>>().Value;

    private ExportRunner NewRunner(string id) => new(env.App.WorkerDb, env.App.Services.GetRequiredService<IOptions<ExportOptions>>(), NullLogger<ExportRunner>.Instance) { WorkerId = id };

    private async Task<(TestUser Owner, Guid Tenant, Guid CustomerId)> ExportableTenantAsync(string name = "Export Şirketi")
    {
        var (owner, tenant, customerId) = await World.CreateTenantWithCustomerAsync(name, "CANARY-A Müşteri");
        await World.SetPlanDirectAsync(tenant, "team");
        return (owner, tenant, customerId);
    }

    private static async Task<Guid> RequestAsync(ApiClient owner, Guid tenant) =>
        (await owner.PostAsync($"/api/t/{tenant}/exports").ShouldBeAsync(HttpStatusCode.Accepted))["id"].Guid();

    private static async Task<string> LinkAsync(ApiClient owner, Guid tenant, Guid job) =>
        (await owner.PostAsync($"/api/t/{tenant}/exports/{job}/download-link").ShouldBeAsync(HttpStatusCode.OK))["url"].Str();

    private static Dictionary<string, string> ReadZip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Encoding.UTF8.GetString(buffer.ToArray()); // BOM korunur
        });
    }

    [Fact]
    public async Task Export_NotInPlan_403()
    {
        var owner = await World.CreateUserAsync();
        var tenant = await World.CreateTenantAsync(owner); // Trial: export yok
        var problem = await owner.Client.PostAsync($"/api/t/{tenant}/exports").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "feature_not_in_plan");
        Assert.Equal("export", problem["feature"].Str());
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.export_jobs WHERE tenant_id = $1", tenant));
    }

    [Fact]
    public async Task Artifact_ContainsNeutralisedCells_OnlyOwnTenant()
    {
        var (owner, tenant, customerId) = await ExportableTenantAsync();
        var (otherOwner, otherTenant, otherCustomer) = await ExportableTenantAsync("Öteki Şirket");
        await World.CreateCustomerAsync(otherOwner.Client, otherTenant, "CANARY-B Müşteri", "canary-b@ornek.test", notes: "CANARY-B not");
        await World.CreateTicketAsync(otherOwner.Client, otherTenant, otherCustomer, "CANARY-B talep");

        // Formül enjeksiyonu denemeleri ve CSV'yi bozabilecek karakterler.
        await World.CreateCustomerAsync(owner.Client, tenant, "=HYPERLINK(\"http://kotu.example\",\"tıkla\")", "formul@ornek.test", companyTitle: "+90 A.Ş.", notes: "@SUM(A1:A9)");
        await World.CreateCustomerAsync(owner.Client, tenant, "Virgül, \"Tırnak\" ve\nsatır sonu", notes: "-2+3");
        var ticket = await World.CreateTicketAsync(owner.Client, tenant, customerId, "=cmd|' /C calc'!A0", "Açıklama; noktalı virgül");
        await owner.Client.PostAsync($"/api/t/{tenant}/tickets/{ticket["id"].Str()}/comments", new { body = "+1 katılıyorum" }).ShouldBeAsync(HttpStatusCode.Created);

        var job = await RequestAsync(owner.Client, tenant);
        // Şirket başına aynı anda tek export.
        await owner.Client.PostAsync($"/api/t/{tenant}/exports").ShouldBeProblemAsync(HttpStatusCode.Conflict, "export_in_progress");
        await owner.Client.PostAsync($"/api/t/{tenant}/exports/{job}/download-link").ShouldBeProblemAsync(HttpStatusCode.Conflict, "export_not_ready");
        Assert.True(await Runner.RunOnceAsync(CancellationToken.None) >= 1);

        var download = await owner.Client.GetAsync(await LinkAsync(owner.Client, tenant, job));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("no-store", download.Headers.CacheControl?.ToString());
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(await env.AdminScalarAsync<string>("SELECT sha256 FROM app.export_jobs WHERE id = $1", job), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));

        var files = ReadZip(bytes);
        Assert.Equal(["comments.csv", "customers.csv", "manifest.txt", "tickets.csv"], files.Keys.Order(StringComparer.Ordinal).ToList());
        var customers = files["customers.csv"];
        var tickets = files["tickets.csv"];
        var comments = files["comments.csv"];

        // Biçim: UTF-8 BOM, CRLF, başlık satırı.
        Assert.StartsWith("﻿id,name,company_title,email,phone,notes,created_at,updated_at\r\n", customers, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", customers.Replace("\r\n", string.Empty).Replace("ve\nsatır", "ve satır"), StringComparison.Ordinal);

        // Formül karakteriyle başlayan hücreler tek tırnakla etkisizleştirilmiş; tırnak/virgül/satır sonu RFC 4180'e göre kaçırılmış.
        Assert.Contains("\"'=HYPERLINK(\"\"http://kotu.example\"\",\"\"tıkla\"\")\"", customers, StringComparison.Ordinal);
        Assert.Contains(",'+90 A.Ş.,", customers, StringComparison.Ordinal);
        Assert.Contains(",'@SUM(A1:A9),", customers, StringComparison.Ordinal);
        Assert.Contains("\"Virgül, \"\"Tırnak\"\" ve\nsatır sonu\"", customers, StringComparison.Ordinal);
        Assert.Contains(",'-2+3,", customers, StringComparison.Ordinal);
        Assert.Contains(",'=cmd|' /C calc'!A0,", tickets, StringComparison.Ordinal);
        Assert.Contains(",'+1 katılıyorum,", comments, StringComparison.Ordinal);
        foreach (var csv in new[] { customers, tickets, comments })
        {
            foreach (var line in csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var cell in line.Split(','))
                {
                    Assert.False(cell.StartsWith('=') || cell.StartsWith("\"=", StringComparison.Ordinal), $"Etkisizleştirilmemiş formül hücresi: {cell}");
                }
            }
        }

        // İzolasyon: yalnız kendi şirketinin satırları.
        var everything = string.Join("\n", files.Values);
        Assert.Contains("CANARY-A", everything);
        Assert.DoesNotContain("CANARY-B", everything);
        Assert.DoesNotContain(otherTenant.ToString(), everything);
        Assert.Contains($"tenant_id: {tenant:D}", files["manifest.txt"]);
        Assert.Contains("customers: 3", files["manifest.txt"]);

        // Denetim: istek, bağlantı, indirme.
        var audit = await (await owner.Client.GetAsync($"/api/t/{tenant}/audit?limit=50")).Content.ReadAsStringAsync();
        foreach (var action in new[] { "export.requested", "export.completed", "export.download_link_issued", "export.downloaded" })
        {
            Assert.Contains(action, audit);
        }
    }

    [Fact]
    public async Task Download_RequiresMember_Permission_Requester_AndToken()
    {
        var (owner, tenant, _) = await ExportableTenantAsync();
        var admin = await World.CreateMemberAsync(tenant, TenantRole.Admin);
        var coOwner = await World.CreateMemberAsync(tenant, TenantRole.Owner, "Ortak Sahip");
        var (otherOwner, otherTenant, _) = await ExportableTenantAsync("Başka Şirket");
        var job = await RequestAsync(owner.Client, tenant);
        await Runner.RunOnceAsync(CancellationToken.None);
        var url = await LinkAsync(owner.Client, tenant, job);
        var token = url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..];

        // Token sızmış olsa bile tek başına yetki değildir.
        var anonymous = await env.App.NewClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await admin.Client.GetAsync(url).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");                  // izin yok
        await coOwner.Client.GetAsync(url).ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");                 // izinli ama işi isteyen değil
        await otherOwner.Client.GetAsync(url).ShouldBeProblemAsync(HttpStatusCode.NotFound, "tenant_not_found");       // başka şirketin sahibi
        await otherOwner.Client.GetAsync($"/api/t/{otherTenant}/exports/{job}/file?token={token}").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found"); // başka şirketin yolu
        await otherOwner.Client.PostAsync($"/api/t/{otherTenant}/exports/{job}/download-link").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await coOwner.Client.PostAsync($"/api/t/{tenant}/exports/{job}/download-link").ShouldBeProblemAsync(HttpStatusCode.NotFound, "not_found");
        await admin.Client.GetAsync($"/api/t/{tenant}/exports").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        // Yanlış token.
        await owner.Client.GetAsync($"/api/t/{tenant}/exports/{job}/file?token={Kulvar.Platform.Security.Tokens.NewToken()}").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "invalid_download_token");
        await owner.Client.GetAsync($"/api/t/{tenant}/exports/{job}/file").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "invalid_download_token");

        // Başarısız denemeler token'ı tüketmedi: asıl sahibi indirir.
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync(url)).StatusCode);
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'export.downloaded'", tenant));

        // İsteyen kişi Owner olmaktan çıkarsa kendi işini de indiremez.
        var members = await coOwner.Client.GetAsync($"/api/t/{tenant}/members").ShouldBeAsync(HttpStatusCode.OK);
        var version = members["items"]!.AsArray().Single(m => m!["userId"].Guid() == owner.Id)!["version"].Int();
        await coOwner.Client.PatchAsync($"/api/t/{tenant}/members/{owner.Id}", new { role = "Admin" }, ("If-Match", $"\"{version}\"")).ShouldBeAsync(HttpStatusCode.OK);
        await owner.Client.PostAsync($"/api/t/{tenant}/exports/{job}/download-link").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task DownloadToken_Expires_And_IsSingleUse()
    {
        var (owner, tenant, _) = await ExportableTenantAsync();
        var job = await RequestAsync(owner.Client, tenant);
        await Runner.RunOnceAsync(CancellationToken.None);

        var first = await LinkAsync(owner.Client, tenant, job);
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync(first)).StatusCode);
        await owner.Client.GetAsync(first).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "invalid_download_token"); // tek kullanım

        var second = await LinkAsync(owner.Client, tenant, job);
        env.Clock.Advance(TimeSpan.FromSeconds(61));
        await owner.Client.GetAsync(second).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "invalid_download_token"); // 60 sn ömür

        // Yeni bağlantı eskisini geçersiz kılar.
        var third = await LinkAsync(owner.Client, tenant, job);
        var fourth = await LinkAsync(owner.Client, tenant, job);
        await owner.Client.GetAsync(third).ShouldBeProblemAsync(HttpStatusCode.Forbidden, "invalid_download_token");
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync(fourth)).StatusCode);
        // Ham token veritabanında tutulmaz.
        var token = fourth[(fourth.IndexOf("token=", StringComparison.Ordinal) + 6)..];
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.export_jobs WHERE export_jobs::text LIKE '%' || $1 || '%'", token));
    }

    [Fact]
    public async Task TraversalInIdOrToken_Rejected()
    {
        var (owner, tenant, _) = await ExportableTenantAsync();
        var job = await RequestAsync(owner.Client, tenant);
        await Runner.RunOnceAsync(CancellationToken.None);

        // Yol parçası GUID değilse uç eşleşmez; dosya yolu hiçbir zaman istemci girdisinden kurulmaz.
        foreach (var id in new[] { "..%2F..%2F..%2Fetc%2Fpasswd", "..", $"{job}%2F..%2F..", $"{job}.zip", "%2e%2e%2f" })
        {
            var response = await owner.Client.GetAsync($"/api/t/{tenant}/exports/{id}/file?token=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        foreach (var token in new[] { "../../secrets/app_connection", "..%2F..%2Fsecrets", "a/b", new string('a', 500) })
        {
            await owner.Client.GetAsync($"/api/t/{tenant}/exports/{job}/file?token={token}").ShouldBeProblemAsync(HttpStatusCode.Forbidden, "invalid_download_token");
        }

        // Yol kurucu: kök dışına çıkamaz; her şirketin dizini ayrıdır.
        var path = Options.ArtifactPath(tenant, job);
        Assert.StartsWith(Path.GetFullPath(Options.Root) + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine(tenant.ToString("D"), job.ToString("D") + ".zip"), path, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
        Assert.Throws<InvalidOperationException>(() => new ExportOptions { Root = string.Empty }.ArtifactPath(tenant, job));
    }

    [Fact]
    public async Task Artifact_Expires_FileRemoved()
    {
        var (owner, tenant, _) = await ExportableTenantAsync();
        var job = await RequestAsync(owner.Client, tenant);
        await Runner.RunOnceAsync(CancellationToken.None);
        var path = Options.ArtifactPath(tenant, job);
        Assert.True(File.Exists(path));
        await Runner.SweepExpiredAsync(CancellationToken.None);
        Assert.True(File.Exists(path)); // Süresi dolmamış artefakta dokunulmaz.

        env.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        var fresh = env.App.NewClient();
        await fresh.LoginAsync(owner.Email, owner.Password).ShouldBeAsync(HttpStatusCode.OK);
        // Süpürücü çalışmadan önce bile süresi dolmuş artefakt indirilemez.
        await fresh.PostAsync($"/api/t/{tenant}/exports/{job}/download-link").ShouldBeProblemAsync(HttpStatusCode.Gone, "export_expired");

        Assert.True(await Runner.SweepExpiredAsync(CancellationToken.None) >= 1);
        Assert.False(File.Exists(path));
        Assert.Equal("Expired", await env.AdminScalarAsync<string>("SELECT status FROM app.export_jobs WHERE id = $1", job));
        await fresh.PostAsync($"/api/t/{tenant}/exports/{job}/download-link").ShouldBeProblemAsync(HttpStatusCode.Gone, "export_expired");
        // Süresi dolunca yeni export istenebilir.
        await RequestAsync(fresh, tenant);
    }

    [Fact]
    public async Task Crash_Retry_And_Fencing_ProduceSingleArtifact()
    {
        var (owner, tenant, _) = await ExportableTenantAsync();
        await Runner.RunOnceAsync(CancellationToken.None); // Başka testlerden kalmış kuyruk varsa önce o biter: sayılar deterministik olur.
        var job = await RequestAsync(owner.Client, tenant);
        var path = Options.ArtifactPath(tenant, job);

        // 1) Dosya yazıldıktan sonra, yayımlanmadan önce çökme: iş yeniden denenmek üzere bekler (hemen değil), geçici dosya kalmaz.
        var crashing = NewRunner("export-crash");
        crashing.BeforeFinalizeHook = _ => throw new IOException("simulated crash before finalize");
        Assert.Equal(1, await crashing.RunOnceAsync(CancellationToken.None)); // Aynı turda yeniden denenmedi.
        Assert.Equal("Running:1", await env.AdminScalarAsync<string>("SELECT status || ':' || attempts FROM app.export_jobs WHERE id = $1", job));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.Exists(Path.GetDirectoryName(path)!) ? Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp") : []);
        await owner.Client.PostAsync($"/api/t/{tenant}/exports/{job}/download-link").ShouldBeProblemAsync(HttpStatusCode.Conflict, "export_not_ready");

        // 2) Yavaş worker işi kiralar; kiralaması dolar, hızlı worker devralıp tamamlar. Yavaş worker'ın yayımı reddedilir (fencing).
        var slow = NewRunner("export-slow");
        var fast = NewRunner("export-fast");
        Assert.Equal(0, await slow.RunOnceAsync(CancellationToken.None)); // Yeniden deneme vakti gelmedi.
        env.Clock.Advance(TimeSpan.FromSeconds(31));
        slow.BeforeFinalizeHook = async _ =>
        {
            env.Clock.Advance(TimeSpan.FromSeconds(121));
            Assert.Equal(1, await fast.RunOnceAsync(CancellationToken.None));
        };
        await slow.RunOnceAsync(CancellationToken.None);

        Assert.Equal("Ready", await env.AdminScalarAsync<string>("SELECT status FROM app.export_jobs WHERE id = $1", job));
        Assert.Equal(1L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1 AND action = 'export.completed'", tenant));
        Assert.True(File.Exists(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!)); // Yayımlanmamış geçici dosya kalmadı.
        var fresh = env.App.NewClient();
        await fresh.LoginAsync(owner.Email, owner.Password).ShouldBeAsync(HttpStatusCode.OK);
        var bytes = await (await fresh.GetAsync(await LinkAsync(fresh, tenant, job))).Content.ReadAsByteArrayAsync();
        Assert.Equal(await env.AdminScalarAsync<string>("SELECT sha256 FROM app.export_jobs WHERE id = $1", job), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }
}
