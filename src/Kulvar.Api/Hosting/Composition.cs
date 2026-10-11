using System.Reflection;
using Kulvar.Modules.Billing;
using Kulvar.Modules.Customers;
using Kulvar.Modules.Identity;
using Kulvar.Modules.Operations;
using Kulvar.Modules.Support;
using Kulvar.Modules.Tenancy;
using Kulvar.Platform.Data;
using Kulvar.Platform.Messaging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Kulvar.Api.Hosting;

/// <summary>Bileşim kökü: süreç türünden (API / worker / migrator) bağımsız ortak kayıtlar.</summary>
public static class Composition
{
    public const string AppConnection = "KulvarApp";
    public const string PlatformConnection = "KulvarPlatform";
    public const string WorkerConnection = "KulvarWorker";
    public const string MigratorConnection = "KulvarMigrator";

    /// <summary>EF modelini oluşturan derlemeler. Migration'ların tek sahibi bu projedir (Kulvar.Api).</summary>
    public static AppModelSource ModelSource { get; } = new(
    [
        typeof(AppDbContext).Assembly,
        typeof(IdentityModule).Assembly,
        typeof(TenancyModule).Assembly,
        typeof(CustomersModule).Assembly,
        typeof(SupportModule).Assembly,
        typeof(BillingModule).Assembly,
        typeof(OperationsModule).Assembly,
    ]);

    public static void AddSecretFiles(this ConfigurationManager configuration)
    {
        // Compose secret'ları /run/secrets altında "Bölüm__Anahtar" adlı dosyalardır; değerler ortam değişkenine veya image'a girmez.
        const string secretsDirectory = "/run/secrets";
        if (Directory.Exists(secretsDirectory))
        {
            configuration.AddKeyPerFile(secretsDirectory, optional: true);
        }
    }

    public static IServiceCollection AddKulvarCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(ModelSource);
        services.Configure<OutboxOptions>(configuration.GetSection("Outbox"));

        // Her veritabanı rolü için ayrı havuz; ilk kullanımda kurulur. Bir süreç yalnız kendi rolünün bağlantı dizesini taşır:
        // API'de worker dizesi, worker'da uygulama/platform dizesi yoktur ve o çalıştırıcılar hiç çözümlenmez.
        services.AddSingleton<DataSourceRegistry>();
        services.AddSingleton(sp => new AppDb(DataSource(sp, AppConnection), sp.GetRequiredService<AppModelSource>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<AppDb>>()));
        services.AddSingleton(sp => new PlatformDb(DataSource(sp, PlatformConnection), sp.GetRequiredService<AppModelSource>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<PlatformDb>>()));
        services.AddSingleton(sp => new WorkerDb(DataSource(sp, WorkerConnection), sp.GetRequiredService<AppModelSource>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<WorkerDb>>()));
        services.AddSingleton<OutboxProcessor>();

        services.AddIdentityModule(configuration);
        services.AddTenancyModule();
        services.AddCustomersModule();
        services.AddSupportModule();
        services.AddBillingModule(configuration);
        services.AddOperationsModule(configuration);
        return services;
    }

    private static NpgsqlDataSource DataSource(IServiceProvider services, string name)
    {
        var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"ConnectionStrings:{name} tanımlı değil; bu süreç o veritabanı rolünü kullanamaz.");
        }
        return services.GetRequiredService<DataSourceRegistry>().GetOrCreate(name, connectionString);
    }
}

/// <summary>Havuzların tek sahibi; süreç kapanırken hepsini kapatır.</summary>
public sealed class DataSourceRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, NpgsqlDataSource> _sources = [];
    private readonly Lock _gate = new();

    public NpgsqlDataSource GetOrCreate(string name, string connectionString)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(name, out var source))
            {
                var builder = new NpgsqlDataSourceBuilder(connectionString);
                builder.ConnectionStringBuilder.ApplicationName ??= $"kulvar-{name}";
                _sources[name] = source = builder.Build();
            }
            return source;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _sources.Values)
        {
            await source.DisposeAsync();
        }
        _sources.Clear();
    }
}

/// <summary>Üretim ortamında demo/simülasyon anahtarları açık bırakılmışsa süreç başlamayı reddeder.</summary>
public static class StartupGuards
{
    public static void Enforce(IConfiguration configuration, IHostEnvironment environment)
    {
        var relaxed = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        if (relaxed)
        {
            return;
        }
        foreach (var key in new[] { "Seed:Enabled", "Billing:Simulation:Enabled", "Billing:Simulation:AllowOutcomeSelection" })
        {
            if (configuration.GetValue<bool>(key))
            {
                throw new InvalidOperationException($"'{key}' yalnız Development/Testing ortamında açılabilir (ortam: {environment.EnvironmentName}).");
            }
        }
    }
}
