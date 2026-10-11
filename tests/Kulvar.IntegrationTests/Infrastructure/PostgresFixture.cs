using System.Security.Cryptography;
using Kulvar.Api.Hosting;
using Kulvar.Platform.Data;
using Kulvar.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Kulvar.IntegrationTests.Infrastructure;

/// <summary>
/// Koşuya ait tek kullanımlık PostgreSQL 18 container'ı. Roller üretimdeki betiklerle (db/bootstrap) kurulur;
/// şema <c>kulvar_owner</c> ile migrate edilir; test edilen kod yalnız kısıtlı runtime rolleriyle bağlanır.
/// Superuser bağlantısı yalnız test kurulumu ve sonuç incelemesi içindir.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly Dictionary<string, string> _passwords = new()
    {
        ["postgres"] = Secret(), ["kulvar_owner"] = Secret(), ["kulvar_app"] = Secret(), ["kulvar_platform"] = Secret(), ["kulvar_worker"] = Secret(),
    };
    private PostgreSqlContainer _container = null!;

    public string RunId => _runId;
    public string Database => $"kulvar_test_{_runId}";
    public string Host => _container.Hostname;
    public int Port => _container.GetMappedPublicPort(5432);
    public TestDatabaseManifest Manifest => new(_runId, Host, Port, Database);

    public string Superuser(string? database = null) => Connection("postgres", database);
    public string Owner(string? database = null) => Connection("kulvar_owner", database);
    public string App(string? database = null, int maxPool = 20) => Connection("kulvar_app", database, maxPool);
    public string Platform(string? database = null) => Connection("kulvar_platform", database);
    public string Worker(string? database = null) => Connection("kulvar_worker", database);

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:18.6")
            .WithDatabase("postgres")
            .WithUsername("postgres")
            .WithPassword(_passwords["postgres"])
            .WithLabel("kulvar.test-run", _runId)
            .WithResourceMapping(new DirectoryInfo(Path.Combine(RepoRoot(), "db", "bootstrap")), "/kulvar-bootstrap/")
            .Build();
        await _container.StartAsync();

        await Psql("postgres", "-f", "/kulvar-bootstrap/01-roles.sql",
            "-v", $"owner_password={_passwords["kulvar_owner"]}", "-v", $"app_password={_passwords["kulvar_app"]}",
            "-v", $"platform_password={_passwords["kulvar_platform"]}", "-v", $"worker_password={_passwords["kulvar_worker"]}");

        await CreateDatabaseAsync(Database);

        // İlk ağ üzerinden yazmadan (migration) önce hedef doğrulanır.
        var guard = new TestDatabaseGuard(new NpgsqlTestDatabaseProbe(Superuser()), Manifest, TestDatabaseGuard.OptInValue);
        await guard.VerifyAsync();
        await MigrateAsync(Database);
    }

    /// <summary>Yeni, boş, bu koşuya işaretli veritabanı (rol/şema kurulumu yapılmış, migration uygulanmamış).</summary>
    public async Task CreateDatabaseAsync(string database)
    {
        if (!database.StartsWith("kulvar_test_", StringComparison.Ordinal) || !database.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException("Test veritabanı adı kulvar_test_ ile başlamalı ve yalnız harf/rakam/alt çizgi içermelidir.", nameof(database));
        }
        await Psql("postgres", "-c", $"CREATE DATABASE {database}");
        await Psql(database, "-f", "/kulvar-bootstrap/02-database.sql");
        await Psql(database, "-c", $"CREATE SCHEMA kulvar_test; CREATE TABLE kulvar_test.run_marker (run_id text PRIMARY KEY); INSERT INTO kulvar_test.run_marker VALUES ('{_runId}');");
    }

    public async Task MigrateAsync(string database, string? targetMigration = null)
    {
        await using var dataSource = NpgsqlDataSource.Create(Owner(database));
        await using var db = new AppDbContext(DbRunner.BuildOptions(dataSource), Composition.ModelSource);
        if (targetMigration is null)
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(targetMigration);
        }
    }

    public async Task<string> Psql(string database, params string[] args)
    {
        var command = new List<string> { "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", database, "-X", "-q", "-A", "-t" };
        command.AddRange(args);
        var result = await _container.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"psql başarısız (exit {result.ExitCode}): {result.Stderr}");
        }
        return result.Stdout;
    }

    public Task StopAsync() => _container.StopAsync();
    public Task StartAsync() => _container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _container.DisposeAsync();
    }

    private string Connection(string user, string? database, int maxPool = 20) => new NpgsqlConnectionStringBuilder
    {
        Host = Host,
        Port = Port,
        Database = database ?? Database,
        Username = user,
        Password = _passwords[user],
        MaxPoolSize = maxPool,
        Timeout = 5,
        CommandTimeout = 20,
        IncludeErrorDetail = true,
    }.ConnectionString;

    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(18));

    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Kulvar.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Depo kökü bulunamadı (Kulvar.slnx).");
    }
}
