using Kulvar.Api.Hosting;
using Npgsql;

namespace Kulvar.Api.Seeding;

/// <summary>
/// <c>--seed</c>: çalışan API'ye karşı demo verisini kurar, ardından platform operatörünü migration rolüyle kaydeder.
/// Yalnız <c>Seed:Enabled=true</c> ve Development/Testing ortamında çalışır (aksi halde süreç başlamayı reddeder).
/// </summary>
public static class SeedHost
{
    public const string PasswordDirectory = "/run/kulvar-seed";

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddSecretFiles();
        if (Directory.Exists(PasswordDirectory))
        {
            builder.Configuration.AddKeyPerFile(PasswordDirectory, optional: true);
        }
        StartupGuards.Enforce(builder.Configuration, builder.Environment);
        using var host = builder.Build();
        var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Kulvar.Seed");
        var configuration = builder.Configuration;

        if (!configuration.GetValue<bool>("Seed:Enabled"))
        {
            log.LogError("Demo seed kapalı (Seed:Enabled=false). Açık opt-in gerekir.");
            return 2;
        }
        if (!Uri.TryCreate(configuration["Seed:BaseUrl"], UriKind.Absolute, out var baseUrl))
        {
            log.LogError("Seed:BaseUrl tanımlı değil.");
            return 2;
        }
        var migrator = configuration.GetConnectionString(Composition.MigratorConnection);
        if (string.IsNullOrWhiteSpace(migrator))
        {
            log.LogError("ConnectionStrings:{Name} tanımlı değil (operatör kaydı için gerekir).", Composition.MigratorConnection);
            return 2;
        }

        // Yerel yığında API geliştirici sertifikası (CN=localhost) kullanır; seed ona servis adıyla bağlandığı için ad doğrulaması yapılamaz.
        // Bu gevşetme yalnız bu tek seferlik demo aracındadır ve StartupGuards üretimde çalışmasını engeller.
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            UseCookies = false,
        };
        try
        {
            var seeder = new DemoSeeder(
                () => new HttpClient(handler, disposeHandler: false) { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(20) },
                key => configuration[$"Seed:Passwords:{key}"],
                log);
            await seeder.RunAsync(CancellationToken.None);

            await using var owner = NpgsqlDataSource.Create(migrator);
            var operatorEmail = DemoData.Users.Single(u => u.Key == DemoData.OperatorKey).Email;
            if (!await OperatorSetup.GrantAsync(owner, operatorEmail, TimeProvider.System.GetUtcNow(), CancellationToken.None))
            {
                log.LogError("Platform operatörü kaydedilemedi.");
                return 3;
            }
            return 0;
        }
        catch (SeedException ex)
        {
            log.LogError("Demo seed başarısız: {Reason}", ex.Message);
            return 1;
        }
        catch (HttpRequestException ex)
        {
            log.LogError("Demo seed API'ye ulaşamadı: {ExceptionType}", ex.GetType().Name);
            return 1;
        }
    }
}
