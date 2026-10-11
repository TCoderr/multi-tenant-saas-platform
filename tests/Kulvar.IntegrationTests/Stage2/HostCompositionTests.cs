using System.Net;
using Kulvar.Api.Hosting;
using Kulvar.Api.Seeding;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Platform.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Kulvar.IntegrationTests.Stage2;

/// <summary>Süreç bileşimleri: API ve worker bağımlılık grafikleri doğrulamayla kurulabilmeli; her süreç yalnız kendi rolünü taşır.</summary>
public sealed class HostCompositionTests(TestEnvironment env)
{
    [Fact]
    public async Task WorkerHost_ServiceGraph_Builds_AndProcessesWithWorkerRoleOnly()
    {
        // Development: bağımlılık grafiği kurulurken doğrulanır (eksik kayıt burada patlar, container'da değil).
        var builder = WorkerHost.CreateBuilder(["--environment=Development", $"--ConnectionStrings:{Composition.WorkerConnection}={env.Postgres.Worker()}"]);
        using var host = builder.Build();

        var processor = host.Services.GetRequiredService<OutboxProcessor>();
        await processor.RunOnceAsync(CancellationToken.None);

        // Worker süreci uygulama/platform bağlantı dizelerini taşımaz; o çalıştırıcılar çözümlenemez.
        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<Kulvar.Platform.Data.AppDb>());
        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<Kulvar.Platform.Data.PlatformDb>());
    }

    [Fact]
    public async Task ApiHost_ServiceGraph_Builds_InDevelopment()
    {
        await using var app = env.NewApp(new Dictionary<string, string?>(), environment: "Development");
        var response = await app.NewClient().GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("Seed:Enabled")]
    [InlineData("Billing:Simulation:Enabled")]
    [InlineData("Billing:Simulation:AllowOutcomeSelection")]
    public void Production_WithDemoSwitch_RefusesToStart(string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = "true" }).Build();
        var production = new HostingEnvironment { EnvironmentName = Environments.Production };
        var ex = Assert.Throws<InvalidOperationException>(() => StartupGuards.Enforce(configuration, production));
        Assert.Contains(key, ex.Message);

        StartupGuards.Enforce(configuration, new HostingEnvironment { EnvironmentName = Environments.Development });
        StartupGuards.Enforce(new ConfigurationBuilder().Build(), production);
    }

    [Fact]
    public async Task DemoSeed_IsIdempotent_AndOperatorIsGrantedOnlyBySetupCommand()
    {
        // Seed bu testte TestServer'a karşı, üretimdekiyle aynı sınıfla koşar. Demo e-postaları sabit olduğundan test yalnız bir kez kurar.
        var seeder = new DemoSeeder(
            () => env.App.CreateDefaultClient(new Uri("https://localhost")),
            key => $"Seed-Test-Parola-{key}!",
            NullLogger.Instance,
            pump: _ => env.App.DrainOutboxAsync());

        await seeder.RunAsync(CancellationToken.None);
        var snapshot = await SnapshotAsync();
        await seeder.RunAsync(CancellationToken.None);
        Assert.Equal(snapshot, await SnapshotAsync());

        var operatorUser = DemoData.Users.Single(u => u.Key == DemoData.OperatorKey);
        var session = seeder.SessionOf(DemoData.OperatorKey);
        var (_, before) = await session.GetAsync("/api/me");
        Assert.False(before["isPlatformOperator"]!.GetValue<bool>()); // Seed (uygulama rolü) operatör atayamaz.

        await using var owner = NpgsqlDataSource.Create(env.Postgres.Owner());
        Assert.True(await OperatorSetup.GrantAsync(owner, operatorUser.Email.ToUpperInvariant(), env.Clock.GetUtcNow(), CancellationToken.None));
        Assert.True(await OperatorSetup.GrantAsync(owner, operatorUser.Email, env.Clock.GetUtcNow(), CancellationToken.None)); // yinelenebilir
        Assert.False(await OperatorSetup.GrantAsync(owner, "yok@kulvar-test.example", env.Clock.GetUtcNow(), CancellationToken.None));

        var (_, after) = await session.GetAsync("/api/me");
        Assert.True(after["isPlatformOperator"]!.GetValue<bool>());
        Assert.Equal(1L, await env.AdminScalarAsync<long>(
            "SELECT count(*) FROM app.platform_operators o JOIN app.users u ON u.id = o.user_id WHERE u.normalized_email = $1", operatorUser.Email));

        async Task<string> SnapshotAsync()
        {
            var emails = DemoData.Users.Select(u => u.Email).ToArray();
            var users = await env.AdminScalarAsync<long>("SELECT count(*) FROM app.users WHERE normalized_email = ANY($1)", (object)emails);
            var tenants = await env.AdminScalarAsync<long>(
                "SELECT count(*) FROM app.tenants t JOIN app.users u ON u.id = t.created_by_user_id WHERE u.normalized_email = ANY($1)", (object)emails);
            var memberships = await env.AdminScalarAsync<long>(
                "SELECT count(*) FROM app.memberships m JOIN app.users u ON u.id = m.user_id WHERE u.normalized_email = ANY($1)", (object)emails);
            return $"users={users};tenants={tenants};memberships={memberships}";
        }
    }

    private sealed class HostingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Kulvar.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
