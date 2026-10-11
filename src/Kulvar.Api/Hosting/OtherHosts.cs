using Kulvar.Platform.Data;
using Kulvar.Platform.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Kulvar.Api.Hosting;

/// <summary>Tek seferlik migration süreci. Yalnız <c>kulvar_owner</c> bağlantısıyla çalışır; API ve worker migration yapmaz.</summary>
public static class MigratorHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddSecretFiles();
        StartupGuards.Enforce(builder.Configuration, builder.Environment);
        using var host = builder.Build();
        var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Kulvar.Migrator");

        var connectionString = builder.Configuration.GetConnectionString(Composition.MigratorConnection);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            log.LogError("ConnectionStrings:{Name} tanımlı değil", Composition.MigratorConnection);
            return 2;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using (var db = new AppDbContext(DbRunner.BuildOptions(dataSource), Composition.ModelSource))
        {
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            await db.Database.MigrateAsync();
            log.LogInformation("Migration tamam: {Applied} yeni migration uygulandı", pending.Count);
        }

        var grantIndex = Array.IndexOf(args, "--grant-operator");
        if (grantIndex >= 0)
        {
            if (grantIndex + 1 >= args.Length)
            {
                log.LogError("Kullanım: --migrate --grant-operator <e-posta>");
                return 2;
            }
            var granted = await OperatorSetup.GrantAsync(dataSource, args[grantIndex + 1], TimeProvider.System.GetUtcNow(), CancellationToken.None);
            log.LogInformation("Platform operatörü kaydı: {Result}", granted ? "eklendi veya zaten vardı" : "kullanıcı bulunamadı");
            return granted ? 0 : 3;
        }
        return 0;
    }
}

/// <summary>
/// Platform operatörü yalnız buradan (migration rolüyle çalışan kurulum komutu) atanır. Public signup ve uygulama rolü bu tabloya yazamaz.
/// Sahip rol kullanıcı tablosunda yalnız aranan e-postanın satırını görebilir (arama anahtarı kapsamı).
/// </summary>
public static class OperatorSetup
{
    public static async Task<bool> GrantAsync(NpgsqlDataSource ownerDataSource, string email, DateTimeOffset now, CancellationToken ct)
    {
        var normalized = Platform.Security.EmailNormalizer.Normalize(email);
        await using var connection = await ownerDataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.login_email', $1, true)", connection, transaction))
        {
            scope.Parameters.Add(new NpgsqlParameter { Value = normalized });
            await scope.ExecuteNonQueryAsync(ct);
        }
        await using var insert = new NpgsqlCommand(
            "INSERT INTO app.platform_operators (user_id, created_at) SELECT u.id, $2 FROM app.users u WHERE u.normalized_email = $1 ON CONFLICT (user_id) DO NOTHING RETURNING user_id",
            connection, transaction);
        insert.Parameters.Add(new NpgsqlParameter { Value = normalized });
        insert.Parameters.Add(new NpgsqlParameter { Value = now });
        var inserted = await insert.ExecuteScalarAsync(ct) is not null;
        await using var exists = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM app.platform_operators o JOIN app.users u ON u.id = o.user_id WHERE u.normalized_email = $1)", connection, transaction);
        exists.Parameters.Add(new NpgsqlParameter { Value = normalized });
        var present = inserted || (bool)(await exists.ExecuteScalarAsync(ct))!;
        await transaction.CommitAsync(ct);
        return present;
    }
}

/// <summary><c>dotnet ef</c> tasarım zamanı fabrikası. Migration üretmek veritabanına bağlanmaz.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("KULVAR_DESIGN_CONNECTION") ?? "Host=localhost;Database=kulvar_design;Username=kulvar_owner";
        return new AppDbContext(DbRunner.BuildOptions(NpgsqlDataSource.Create(connectionString)), Composition.ModelSource);
    }
}

/// <summary>Arka plan işleri süreci. Yalnız <c>kulvar_worker</c> bağlantısını taşır.</summary>
public static class WorkerHost
{
    public const string HeartbeatPath = "/tmp/kulvar-worker.heartbeat";

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = CreateBuilder(args);
        if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(Composition.WorkerConnection)))
        {
            Console.Error.WriteLine($"ConnectionStrings:{Composition.WorkerConnection} tanımlı değil");
            return 2;
        }
        using var host = builder.Build();
        await host.RunAsync();
        return 0;
    }

    /// <summary>Worker'ın servis kayıtları. Testler aynı kurucuyu kullanarak bağımlılık grafiğinin kurulabildiğini doğrular.</summary>
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddSecretFiles();
        StartupGuards.Enforce(builder.Configuration, builder.Environment);
        builder.Services.AddKulvarCore(builder.Configuration);
        builder.Services.AddHostedService<OutboxWorker>();
        builder.Services.AddHostedService<LifecycleWorker>();
        return builder;
    }
}

/// <summary>
/// Zamana bağlı ve kuyruk dışı işler. Her saniye: webhook inbox'ı (ve simülasyon açıksa simüle sağlayıcının teslimatı).
/// Her saniye ayrıca export işleri. Her beş saniyede: abonelik zamanlayıcıları, ödeme mutabakatı, artefakt süresi ve şirket temizleme. Her tur yinelenebilirdir; bir işin hatası diğerlerini durdurmaz.
/// </summary>
public sealed class LifecycleWorker(
    Kulvar.Modules.Billing.SubscriptionTimers subscriptionTimers,
    Kulvar.Modules.Billing.ProviderInboxProcessor inbox,
    Kulvar.Modules.Billing.BillingReconciler reconciler,
    Kulvar.Modules.Billing.Provider.ProviderSimulator simulator,
    Kulvar.Modules.Operations.ExportRunner exports,
    Kulvar.Modules.Operations.TenantPurger purger,
    Microsoft.Extensions.Options.IOptions<Kulvar.Modules.Billing.BillingOptions> billing,
    ILogger<LifecycleWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var simulation = billing.Value.Simulation;
        // Simülatör API'ye servis adıyla bağlanır; geliştirici sertifikası (CN=localhost) bu ad için doğrulanamaz. Bu gevşetme yalnız
        // simülasyon açıkken (yalnız Development/Testing; StartupGuards) ve yalnız simülatörün kendi isteklerinde geçerlidir.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tick = 0;
        do
        {
            await GuardAsync("inbox", () => inbox.RunOnceAsync(stoppingToken));
            await GuardAsync("exports", () => exports.RunOnceAsync(stoppingToken));
            if (simulation.Enabled && !string.IsNullOrWhiteSpace(simulation.WebhookUrl))
            {
                await GuardAsync("simulator", async () =>
                {
                    await simulator.ProduceRequestedEventsAsync(stoppingToken);
                    return await simulator.DeliverPendingAsync(http, simulation.WebhookUrl, stoppingToken);
                });
            }
            if (tick++ % 5 == 0)
            {
                await GuardAsync("subscription-timers", () => subscriptionTimers.RunOnceAsync(stoppingToken));
                await GuardAsync("reconciler", () => reconciler.RunOnceAsync(stoppingToken));
                await GuardAsync("export-sweeper", () => exports.SweepExpiredAsync(stoppingToken));
                await GuardAsync("tenant-purger", () => purger.RunOnceAsync(stoppingToken));
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private async Task GuardAsync(string job, Func<Task<int>> run)
    {
        try
        {
            await run();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.LogError("Arka plan turu hatası: {Job} {ExceptionType} sqlstate={SqlState}", job, ex.GetType().Name, PostgresErrors.SqlState(ex));
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Outbox döngüsü: boşta 1 sn bekler, iş varken beklemeden sürdürür. Kapatma mesajlar arasında gözlenir.</summary>
public sealed class OutboxWorker(OutboxProcessor processor, ILogger<OutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = (await processor.RunOnceAsync(stoppingToken)).Count;
                WorkerHeartbeat.Touch(); // Yalnız başarılı turda: veritabanına ulaşamayan worker "sağlıklı" görünmez.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Veritabanı kesintisi gibi durumlarda döngü ölmez; bekleyip yeniden dener.
                log.LogError("Outbox döngüsü hatası: {ExceptionType} sqlstate={SqlState}", ex.GetType().Name, PostgresErrors.SqlState(ex));
            }
            if (processed == 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>Worker'ın canlılık işareti: container healthcheck'i dosyanın tazeliğine bakar (worker'ın HTTP ucu yoktur).</summary>
public static class WorkerHeartbeat
{
    public static void Touch()
    {
        try
        {
            File.WriteAllText(WorkerHost.HeartbeatPath, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (IOException)
        {
            // Yazılamıyorsa healthcheck zaten başarısız olur; iş döngüsü bundan etkilenmez.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static bool IsFresh(TimeSpan maxAge) =>
        File.Exists(WorkerHost.HeartbeatPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(WorkerHost.HeartbeatPath) <= maxAge;
}

/// <summary>Container healthcheck'i için: yalnız localhost'a istek atar; geliştirici sertifikası container içinde güvenilir olmadığından doğrulamaz.</summary>
public static class HealthProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--worker-heartbeat"))
        {
            return WorkerHeartbeat.IsFresh(TimeSpan.FromSeconds(30)) ? 0 : 1;
        }
        var index = Array.IndexOf(args, "--probe");
        if (index < 0 || index + 1 >= args.Length || !Uri.TryCreate(args[index + 1], UriKind.Absolute, out var uri) || !uri.IsLoopback)
        {
            Console.Error.WriteLine("Kullanım: --probe https://localhost:<port>/health/live");
            return 2;
        }
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            using var response = await client.GetAsync(uri);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
