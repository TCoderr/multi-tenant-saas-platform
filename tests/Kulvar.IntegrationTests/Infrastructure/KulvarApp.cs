using Kulvar.Api;
using Kulvar.Api.Hosting;
using Kulvar.Platform.Data;
using Kulvar.Platform.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

// Testler tek veritabanını ve tek sahte saati paylaşır; sıralı koşu (xunit.runner.json) sonuçları deterministik kılar.
[assembly: AssemblyFixture(typeof(Kulvar.IntegrationTests.Infrastructure.TestEnvironment))]

namespace Kulvar.IntegrationTests.Infrastructure;

/// <summary>Gerçek PostgreSQL'e karşı çalışan API (TestServer). Üç runtime rolü de ayrı havuzla bağlanır; saat sahtedir.</summary>
public sealed class KulvarApp : WebApplicationFactory<Program>
{
    private readonly PostgresFixture _postgres;
    private readonly Dictionary<string, string?> _settings;

    public KulvarApp(PostgresFixture postgres, FakeTimeProvider clock, CapturingLoggerProvider logs, IDictionary<string, string?>? overrides = null, string environment = "Testing")
    {
        _postgres = postgres;
        Clock = clock;
        Logs = logs;
        EnvironmentName = environment;
        _settings = new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{Composition.AppConnection}"] = postgres.App(),
            [$"ConnectionStrings:{Composition.PlatformConnection}"] = postgres.Platform(),
            [$"ConnectionStrings:{Composition.WorkerConnection}"] = postgres.Worker(),
            ["Auth:PasswordIterations"] = "1000",
            ["RateLimits:LoginPerMinute"] = "100000",
            ["RateLimits:SignupPerMinute"] = "100000",
            ["RateLimits:WebhookPerMinute"] = "100000",
            ["RateLimits:ApiPerMinute"] = "1000000",
            ["Billing:Webhook:Secret"] = WebhookSecret,
            ["Billing:Simulation:Enabled"] = "true",
            ["Billing:Simulation:AllowOutcomeSelection"] = "true",
            ["Exports:Root"] = ExportRoot(postgres.RunId),
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            _settings[key] = value;
        }
    }

    /// <summary>Koşuya ait geçici export kökü (test bitince silinir).</summary>
    public static string ExportRoot(string runId) => Path.Combine(Path.GetTempPath(), $"kulvar-test-exports-{runId}");

    public const string WebhookSecret = "test-webhook-secret-0123456789abcdef-0123456789";

    public FakeTimeProvider Clock { get; }
    public CapturingLoggerProvider Logs { get; }
    public string EnvironmentName { get; }

    public AppDb AppDb => Services.GetRequiredService<AppDb>();
    public WorkerDb WorkerDb => Services.GetRequiredService<WorkerDb>();
    public PlatformDb PlatformDb => Services.GetRequiredService<PlatformDb>();
    public OutboxProcessor Outbox => Services.GetRequiredService<OutboxProcessor>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(Logs);
            logging.SetMinimumLevel(LogLevel.Information);
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public ApiClient NewClient() => new(CreateDefaultClient(new Uri("https://localhost")));

    /// <summary>Bekleyen outbox mesajları bitene kadar worker işleyicisini çalıştırır (deterministik; arka plan döngüsü yok).</summary>
    public async Task DrainOutboxAsync(int maxRounds = 20)
    {
        for (var round = 0; round < maxRounds; round++)
        {
            if ((await Outbox.RunOnceAsync(CancellationToken.None)).Count == 0)
            {
                return;
            }
        }
        throw new InvalidOperationException("Outbox boşalmadı.");
    }
}

/// <summary>Tüm testlerin paylaştığı ortam: tek container, tek veritabanı, tek API, tek sahte saat.</summary>
public sealed class TestEnvironment : IAsyncLifetime
{
    public PostgresFixture Postgres { get; } = new();
    /// <summary>Koşuya ait geçici export kökü (test bitince silinir).</summary>
    public static string ExportRoot(string runId) => Path.Combine(Path.GetTempPath(), $"kulvar-test-exports-{runId}");

    public const string WebhookSecret = "test-webhook-secret-0123456789abcdef-0123456789";

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
    public CapturingLoggerProvider Logs { get; } = new();
    public KulvarApp App { get; private set; } = null!;
    public World World { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Postgres.InitializeAsync();
        App = new KulvarApp(Postgres, Clock, Logs);
        _ = App.Services; // Host'u başlat.
        World = new World(this);
        HttpResponseExtensions.Diagnostics = () => string.Join("\n", Logs.Snapshot().Where(l => l.StartsWith("Error", StringComparison.Ordinal) || l.StartsWith("Warning", StringComparison.Ordinal)).TakeLast(4));
    }

    public KulvarApp NewApp(IDictionary<string, string?> overrides, string environment = "Testing") =>
        new(Postgres, Clock, Logs, overrides, environment);

    /// <summary>Superuser ile ham sorgu: yalnız test kurulumu ve sonuç doğrulaması için (RLS'i atlar).</summary>
    public async Task<T?> AdminScalarAsync<T>(string sql, params object?[] args)
    {
        await using var connection = new NpgsqlConnection(Postgres.Superuser());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<int> AdminExecuteAsync(string sql, params object?[] args)
    {
        await using var connection = new NpgsqlConnection(Postgres.Superuser());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }
        return await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        var exports = KulvarApp.ExportRoot(Postgres.RunId);
        if (Directory.Exists(exports))
        {
            Directory.Delete(exports, recursive: true); // Yalnız bu koşunun kendi geçici dizini.
        }
        await Postgres.DisposeAsync();
    }
}

/// <summary>Log satırlarını bellekte toplar: kişisel veri sızıntısı testleri ve hata ayıklama için.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _lines.ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

    public void Dispose() { }

    private sealed class Capturing(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Üretimdeki JSON biçimlendirici gibi: mesaj + yapısal alanlar + (varsa) istisna metni.
            var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}")) : string.Empty;
            var line = $"{logLevel} {category} {formatter(state, exception)} {fields} {exception}";
            lock (owner._gate)
            {
                owner._lines.Add(line);
            }
        }
    }
}
