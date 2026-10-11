using Kulvar.Api.Hosting;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Kulvar.IntegrationTests.Stage2;

/// <summary>
/// Migration doğrulamaları ayrı, koşuya ait veritabanlarında yapılır: boş veritabanına uygulama, her önceki migration'dan
/// sentetik veriyle yükseltme, model/snapshot tutarlılığı ve idempotent betiğin gerçek veritabanına iki kez uygulanması.
/// </summary>
public sealed class MigrationTests(TestEnvironment env)
{
    private static int _sequence;

    private async Task<string> NewDatabaseAsync(string purpose)
    {
        var name = $"kulvar_test_{env.Postgres.RunId}_{purpose}_{Interlocked.Increment(ref _sequence)}";
        await env.Postgres.CreateDatabaseAsync(name);
        return name;
    }

    private async Task<T?> ScalarAsync<T>(string database, string sql, params object?[] args)
    {
        await using var connection = new NpgsqlConnection(env.Postgres.Superuser(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static List<string> AllMigrations()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        using var db = new AppDbContext(DbRunner.BuildOptions(dataSource), Composition.ModelSource);
        return db.Database.GetMigrations().ToList();
    }

    [Fact]
    public async Task Applies_ToEmptyDatabase()
    {
        var database = await NewDatabaseAsync("empty");
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM pg_tables WHERE schemaname = 'app'"));

        await env.Postgres.MigrateAsync(database);

        var applied = await ScalarAsync<long>(database, "SELECT count(*) FROM app.__ef_migrations_history");
        Assert.Equal(AllMigrations().Count, applied);
        Assert.True(await ScalarAsync<long>(database, "SELECT count(*) FROM pg_policies WHERE schemaname = 'app'") > 10);
        // Yeni veritabanında da roller yalnız verilen yetkilere sahiptir: runtime rolü şemada nesne oluşturamaz.
        Assert.False(await ScalarAsync<bool>(database, "SELECT has_schema_privilege('kulvar_app', 'app', 'CREATE')"));

        // İkinci çalıştırma etkisizdir.
        await env.Postgres.MigrateAsync(database);
        Assert.Equal(applied, await ScalarAsync<long>(database, "SELECT count(*) FROM app.__ef_migrations_history"));
    }

    [Fact]
    public async Task Model_HasNoPendingChanges()
    {
        await using var dataSource = NpgsqlDataSource.Create(env.Postgres.Owner());
        await using var db = new AppDbContext(DbRunner.BuildOptions(dataSource), Composition.ModelSource);
        Assert.False(db.Database.HasPendingModelChanges(), "Model ile son migration snapshot'ı arasında fark var: yeni migration üretilmeli.");
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task IdempotentScript_AppliesTwice()
    {
        string script;
        await using (var dataSource = NpgsqlDataSource.Create(env.Postgres.Owner()))
        await using (var db = new AppDbContext(DbRunner.BuildOptions(dataSource), Composition.ModelSource))
        {
            script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        }
        Assert.Contains("ROW LEVEL SECURITY", script);

        var database = await NewDatabaseAsync("script");
        for (var run = 1; run <= 2; run++)
        {
            await using var connection = new NpgsqlConnection(env.Postgres.Owner(database));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(script, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal((long)AllMigrations().Count, await ScalarAsync<long>(database, "SELECT count(*) FROM app.__ef_migrations_history"));
        // Betikle kurulan şema, Migrate() ile kurulanla aynı politika ve tablo sayısına sahiptir.
        var reference = await NewDatabaseAsync("reference");
        await env.Postgres.MigrateAsync(reference);
        foreach (var probe in new[]
                 {
                     "SELECT count(*) FROM pg_policies WHERE schemaname = 'app'",
                     "SELECT count(*) FROM pg_tables WHERE schemaname IN ('app', 'fakepsp')",
                     "SELECT count(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'app'",
                     "SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'app' AND NOT t.tgisinternal",
                     "SELECT count(*) FROM information_schema.role_table_grants WHERE table_schema IN ('app', 'fakepsp') AND grantee LIKE 'kulvar\\_%' AND grantee <> 'kulvar_owner'",
                 })
        {
            Assert.Equal(await ScalarAsync<long>(reference, probe), await ScalarAsync<long>(database, probe));
        }
    }

    [Fact]
    public async Task Upgrades_FromEachPreviousMigration_WithData()
    {
        var migrations = AllMigrations();
        Assert.NotEmpty(migrations);

        // Son migration hariç her biri için: o noktaya kadar kur, o aşamanın şemasına sentetik veri koy, en sona yükselt, veriyi doğrula.
        for (var index = 0; index < migrations.Count - 1; index++)
        {
            var stage = migrations[index];
            var database = await NewDatabaseAsync($"upgrade{index + 1}");
            await env.Postgres.MigrateAsync(database, stage);

            var seeded = 0;
            foreach (var (requiredMigration, seed) in UpgradeSeeds.All)
            {
                if (migrations.IndexOf(migrations.Single(m => m.EndsWith(requiredMigration, StringComparison.Ordinal))) <= index)
                {
                    await env.Postgres.Psql(database, "-c", seed.Sql);
                    seeded++;
                }
            }
            Assert.True(seeded > 0, $"{stage} için sentetik veri tanımı yok.");

            await env.Postgres.MigrateAsync(database);

            foreach (var (requiredMigration, seed) in UpgradeSeeds.All)
            {
                if (migrations.IndexOf(migrations.Single(m => m.EndsWith(requiredMigration, StringComparison.Ordinal))) <= index)
                {
                    foreach (var (sql, expected) in seed.Checks)
                    {
                        var actual = await env.Postgres.Psql(database, "-c", sql);
                        Assert.True(actual.Trim() == expected, $"{stage} → son: '{sql}' beklenen '{expected}', gelen '{actual.Trim()}'");
                    }
                }
            }
            foreach (var (sql, expected) in UpgradeSeeds.Invariants)
            {
                var actual = await env.Postgres.Psql(database, "-c", sql);
                Assert.True(actual.Trim() == expected, $"{stage} → son (değişmez): '{sql}' beklenen '{expected}', gelen '{actual.Trim()}'");
            }
            Assert.Equal((long)migrations.Count, await ScalarAsync<long>(database, "SELECT count(*) FROM app.__ef_migrations_history"));
        }
    }

    [Fact]
    public void EveryMigration_HasUpgradeSeed()
    {
        // Yeni migration eklendiğinde onun şemasına ait sentetik veri de tanımlanmalıdır; yoksa yükseltme testi sessizce zayıflar.
        var withoutSeed = AllMigrations()
            .Where(m => !UpgradeSeeds.All.Any(s => m.EndsWith(s.Migration, StringComparison.Ordinal)))
            .ToList();
        Assert.True(withoutSeed.Count == 0, $"Yükseltme verisi tanımlanmamış migration: {string.Join(", ", withoutSeed)}");
    }
}

public sealed record UpgradeSeed(string Sql, (string Sql, string Expected)[] Checks);

/// <summary>
/// Her migration'ın eklediği tablolara konan sentetik veri (superuser ile, RLS'i atlayarak) ve yükseltme sonrası beklenenler.
/// Sabit kimlikler kullanılır; veri kişisel veri değildir.
/// </summary>
public static class UpgradeSeeds
{
    private const string UserA = "018f0000-0000-7000-8000-00000000a001";
    private const string UserB = "018f0000-0000-7000-8000-00000000b001";
    private const string TenantA = "018f0000-0000-7000-8000-0000000a0001";
    private const string TenantB = "018f0000-0000-7000-8000-0000000b0001";

    public static readonly (string Migration, UpgradeSeed Seed)[] All =
    [
        ("_IdentityTenancy", new UpgradeSeed(
            $"""
            INSERT INTO app.users (id, email, normalized_email, display_name, password_hash, email_verified_at, failed_login_count, created_at) VALUES
              ('{UserA}', 'upgrade.a@example.test', 'upgrade.a@example.test', 'Upgrade A', 'x', now(), 0, now()),
              ('{UserB}', 'upgrade.b@example.test', 'upgrade.b@example.test', 'Upgrade B', 'x', now(), 0, now());
            INSERT INTO app.tenants (id, name, status, created_by_user_id, created_at, version) VALUES
              ('{TenantA}', 'Upgrade Tenant A', 'Active', '{UserA}', now(), 1),
              ('{TenantB}', 'Upgrade Tenant B', 'Active', '{UserB}', now(), 1);
            INSERT INTO app.memberships (tenant_id, user_id, role, status, created_at, version) VALUES
              ('{TenantA}', '{UserA}', 'Owner', 'Active', now(), 1),
              ('{TenantB}', '{UserB}', 'Owner', 'Active', now(), 1),
              ('{TenantA}', '{UserB}', 'Agent', 'Active', now(), 1);
            INSERT INTO app.audit_events (id, tenant_id, actor_user_id, action, target_type, data, occurred_at) VALUES
              (gen_random_uuid(), '{TenantA}', '{UserA}', 'tenant.created', 'tenant', jsonb_build_object(), now());
            """,
            [
                ("SELECT count(*) FROM app.users WHERE id IN ('" + UserA + "','" + UserB + "')", "2"),
                ("SELECT string_agg(role, ',' ORDER BY role) FROM app.memberships WHERE tenant_id = '" + TenantA + "'", "Agent,Owner"),
                ("SELECT name FROM app.tenants WHERE id = '" + TenantB + "'", "Upgrade Tenant B"),
                ("SELECT count(*) FROM app.audit_events WHERE tenant_id = '" + TenantA + "'", "1"),
            ])),
        ("_CustomersSupport", new UpgradeSeed(
            $"""
            INSERT INTO app.customers (id, tenant_id, name, email, normalized_email, created_by_user_id, created_at, updated_at, version) VALUES
              ('{CustomerA}', '{TenantA}', 'Upgrade Müşteri A', 'musteri@example.test', 'musteri@example.test', '{UserA}', now(), now(), 1),
              ('{CustomerB}', '{TenantB}', 'Upgrade Müşteri B', 'musteri@example.test', 'musteri@example.test', '{UserB}', now(), now(), 1);
            INSERT INTO app.tickets (id, tenant_id, number, customer_id, title, description, priority, status, assignee_user_id, created_by_user_id, created_at, updated_at, reopen_count, version) VALUES
              ('{TicketA}', '{TenantA}', 1, '{CustomerA}', 'Upgrade talep', 'açıklama', 'High', 'InProgress', '{UserB}', '{UserA}', now(), now(), 0, 2);
            INSERT INTO app.ticket_comments (id, tenant_id, ticket_id, author_user_id, body, created_at) VALUES
              (gen_random_uuid(), '{TenantA}', '{TicketA}', '{UserB}', 'Upgrade yorum', now());
            INSERT INTO app.tenant_counters (tenant_id, next_ticket_number) VALUES ('{TenantA}', 2);
            """,
            [
                ("SELECT count(*) FROM app.customers WHERE normalized_email = 'musteri@example.test'", "2"),
                ("SELECT status || ':' || priority || ':' || version FROM app.tickets WHERE id = '" + TicketA + "'", "InProgress:High:2"),
                ("SELECT count(*) FROM app.ticket_comments WHERE ticket_id = '" + TicketA + "'", "1"),
                ("SELECT search_text FROM app.customers WHERE id = '" + CustomerA + "'", "upgrade müşteri a  musteri@example.test"),
            ])),
        ("_InvitesOwnership", new UpgradeSeed(
            $"""
            INSERT INTO app.invites (id, tenant_id, email, normalized_email, role, status, token_hash, invited_by_user_id, created_at, expires_at) VALUES
              (gen_random_uuid(), '{TenantA}', 'davetli@example.test', 'davetli@example.test', 'Viewer', 'Pending', sha256('upgrade-token'::bytea), '{UserA}', now(), now() + interval '7 days');
            INSERT INTO app.ownership_offers (id, tenant_id, from_user_id, to_user_id, demote_self, status, created_at, expires_at) VALUES
              (gen_random_uuid(), '{TenantA}', '{UserA}', '{UserB}', true, 'Pending', now(), now() + interval '3 days');
            """,
            [
                ("SELECT role || ':' || status FROM app.invites WHERE tenant_id = '" + TenantA + "'", "Viewer:Pending"),
                ("SELECT count(*) FROM app.ownership_offers WHERE tenant_id = '" + TenantA + "' AND status = 'Pending'", "1"),
            ])),
        ("_BillingEntitlements", new UpgradeSeed(
            // Bu aşamadan sonra kurulan veride abonelik satırını uygulama açar; önceki aşamalardan yükseltmede migration backfill'i açar.
            $"""
            INSERT INTO app.subscriptions (tenant_id, plan_version_id, status, epoch, provider_sequence, updated_at, version) VALUES
              ('{TenantA}', '018f1000-0000-7000-8000-000000000003', 'Active', 0, 0, now(), 1),
              ('{TenantB}', '018f1000-0000-7000-8000-000000000001', 'Trialing', 0, 0, now(), 1)
            ON CONFLICT (tenant_id) DO NOTHING;
            INSERT INTO app.usage_counters (tenant_id, metric, period_start, used) VALUES ('{TenantA}', 'tickets_created', date '2026-10-01', 4);
            """,
            [
                ("SELECT used FROM app.usage_counters WHERE tenant_id = '" + TenantA + "'", "4"),
                ("SELECT status FROM app.subscriptions WHERE tenant_id = '" + TenantA + "'", "Active"),
            ])),
        ("_BillingOperations", new UpgradeSeed(
            $"""
            INSERT INTO app.billing_operations (id, tenant_id, merchant_reference, target_plan_version_id, amount_minor, currency, status, epoch, requested_by_user_id, attempts, created_at, updated_at, version) VALUES
              (gen_random_uuid(), '{TenantA}', 'mref_upgrade_a', '018f1000-0000-7000-8000-000000000003', 149000, 'TRY', 'Succeeded', 0, '{UserA}', 0, now(), now(), 2);
            INSERT INTO app.provider_events (provider_event_id, payload_hash, payload, received_at, status, attempts) VALUES
              ('evt_upgrade_1', sha256('x'::bytea), jsonb_build_object('id', 'evt_upgrade_1'), now(), 'Applied', 0);
            """,
            [
                ("SELECT status || ':' || amount_minor FROM app.billing_operations WHERE merchant_reference = 'mref_upgrade_a'", "Succeeded:149000"),
                ("SELECT status FROM app.provider_events WHERE provider_event_id = 'evt_upgrade_1'", "Applied"),
            ])),
        ("_ExportsLifecycle", new UpgradeSeed(
            $"""
            INSERT INTO app.export_jobs (id, tenant_id, requested_by_user_id, status, created_at, attempts, lease_token) VALUES
              (gen_random_uuid(), '{TenantA}', '{UserA}', 'Expired', now(), 1, 1);
            """,
            [
                ("SELECT status FROM app.export_jobs WHERE tenant_id = '" + TenantA + "'", "Expired"),
            ])),
    ];

    /// <summary>Hangi aşamadan yükseltilirse yükseltilsin, yükseltme sonunda tutması gereken değişmezler (sorgu → beklenen).</summary>
    public static readonly (string Sql, string Expected)[] Invariants =
    [
        // Abonelik backfill'i: her (silinmemiş) şirketin tam bir abonelik satırı vardır.
        ("SELECT count(*) FROM app.tenants t WHERE t.status NOT IN ('Purging','Deleted') AND NOT EXISTS (SELECT 1 FROM app.subscriptions s WHERE s.tenant_id = t.id)", "0"),
        ("SELECT count(*) FROM app.subscriptions WHERE tenant_id IN ('" + TenantA + "','" + TenantB + "')", "2"),
        // Backfill sonrası şirket kaydında FORCE geri konmuştur.
        ("SELECT relforcerowsecurity::int FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'app' AND c.relname = 'tenants'", "1"),
        ("SELECT count(*) FROM app.plan_versions WHERE is_current", "3"),
    ];

    private const string CustomerA = "018f0000-0000-7000-8000-0000000ca001";
    private const string CustomerB = "018f0000-0000-7000-8000-0000000cb001";
    private const string TicketA = "018f0000-0000-7000-8000-00000007a001";
}
