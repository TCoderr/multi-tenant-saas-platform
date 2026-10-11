using Kulvar.IntegrationTests.Infrastructure;
using Npgsql;

namespace Kulvar.IntegrationTests.Stage2;

/// <summary>Şema kurallarının <c>pg_catalog</c> üzerinden doğrulanması: sınıflandırılmamış veya RLS'siz tablo şemaya giremez.</summary>
public sealed class SchemaConventionTests(TestEnvironment env)
{
    private async Task<List<string>> ColumnAsync(string sql, params object?[] args)
    {
        await using var connection = new NpgsqlConnection(env.Postgres.Superuser());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    [Fact]
    public async Task AllTablesClassified_TenantTablesForceRls()
    {
        var tables = await ColumnAsync("SELECT tablename::text FROM pg_tables WHERE schemaname = 'app' AND tablename <> '__ef_migrations_history' ORDER BY 1");
        Assert.NotEmpty(tables);

        var unclassified = tables.Except(SchemaCatalog.Tables.Keys).ToList();
        Assert.True(unclassified.Count == 0, $"Sınıflandırılmamış tablo(lar): {string.Join(", ", unclassified)} — SchemaCatalog'a eklenmeli ve izolasyonu tanımlanmalı.");
        var stale = SchemaCatalog.Tables.Keys.Except(tables).ToList();
        Assert.True(stale.Count == 0, $"Katalogda olup şemada olmayan tablo(lar): {string.Join(", ", stale)}");

        foreach (var (table, tableClass) in SchemaCatalog.Tables)
        {
            var flags = await env.AdminScalarAsync<string>(
                "SELECT concat(c.relrowsecurity::int, c.relforcerowsecurity::int) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'app' AND c.relname = $1", table);
            var policies = await env.AdminScalarAsync<long>("SELECT count(*) FROM pg_policies WHERE schemaname = 'app' AND tablename = $1", table);
            var ownerName = await env.AdminScalarAsync<string>("SELECT tableowner::text FROM pg_tables WHERE schemaname = 'app' AND tablename = $1", table);
            Assert.Equal("kulvar_owner", ownerName);

            if (tableClass == TableClass.Reference)
            {
                continue; // Public referans veri: RLS yok, runtime rollerinde yalnız SELECT (ayrı testte).
            }
            Assert.True(flags == "11", $"{table}: RLS ENABLE+FORCE bekleniyor (relrowsecurity/relforcerowsecurity = {flags}).");
            Assert.True(policies > 0, $"{table}: hiç politika yok.");

            var tenantColumn = await env.AdminScalarAsync<string>(
                "SELECT is_nullable::text FROM information_schema.columns WHERE table_schema = 'app' AND table_name = $1 AND column_name = 'tenant_id'", table);
            if (tableClass == TableClass.Tenant)
            {
                Assert.True(tenantColumn == "NO", $"{table}: tenant_id NOT NULL bekleniyor.");
                Assert.True(SchemaCatalog.TenantTables.Any(t => t.Name == table), $"{table}: kiracı tablosu RLS test kataloğunda (TenantTables) yok.");
                // Kiracı politikası hem USING hem WITH CHECK taşımalı.
                var loose = await env.AdminScalarAsync<long>(
                    "SELECT count(*) FROM pg_policies WHERE schemaname = 'app' AND tablename = $1 AND cmd IN ('ALL', 'UPDATE') AND (qual IS NULL OR with_check IS NULL) AND NOT (roles = '{kulvar_owner}')", table);
                Assert.True(loose == 0, $"{table}: USING veya WITH CHECK eksik politika var.");
            }
        }

        // tenant_id sütunu NOT NULL olan her tablo kiracı tablosu olarak sınıflandırılmış olmalı.
        var withTenantId = await ColumnAsync(
            "SELECT table_name::text FROM information_schema.columns WHERE table_schema = 'app' AND column_name = 'tenant_id' AND is_nullable = 'NO'");
        foreach (var table in withTenantId)
        {
            Assert.Equal(TableClass.Tenant, SchemaCatalog.Tables[table]);
        }
    }

    [Fact]
    public async Task NoSecurityDefinerFunctions_AndHelpersPinSearchPath()
    {
        Assert.Equal(0, await env.AdminScalarAsync<long>(
            "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname IN ('app', 'fakepsp') AND p.prosecdef"));
        var unpinned = await ColumnAsync(
            "SELECT p.proname::text FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'app' AND (p.proconfig IS NULL OR NOT EXISTS (SELECT 1 FROM unnest(p.proconfig) c WHERE c LIKE 'search_path=%'))");
        Assert.Empty(unpinned);
    }

    [Fact]
    public async Task NoCascadingForeignKeys()
    {
        // confdeltype: a = NO ACTION, r = RESTRICT. Kademeli silme (c) veya SET NULL/DEFAULT (n, d) yok.
        var cascading = await ColumnAsync(
            "SELECT c.conname::text FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'app' AND c.contype = 'f' AND c.confdeltype NOT IN ('a', 'r')");
        Assert.Empty(cascading);
    }
}

/// <summary>"En az bir Owner" kuralının veritabanı düzeyindeki yedeği: uygulama kodunu atlayan ham SQL de kuralı çiğneyemez.</summary>
public sealed class OwnerTriggerTests(TestEnvironment env)
{
    [Fact]
    public async Task RawSql_RemovingLastOwner_FailsAtCommit()
    {
        var owner = await env.World.CreateUserAsync();
        var tenant = await env.World.CreateTenantAsync(owner);

        foreach (var statement in new[]
                 {
                     "UPDATE app.memberships SET role = 'Admin' WHERE tenant_id = $1 AND user_id = $2",
                     "UPDATE app.memberships SET status = 'Removed' WHERE tenant_id = $1 AND user_id = $2",
                 })
        {
            await using var session = await RoleSession.OpenAsync(env.Postgres.App(), owner.Id, tenant);
            Assert.Equal(1, await session.ExecAsync(statement, tenant, owner.Id)); // Deyim geçer...
            var ex = await Assert.ThrowsAsync<PostgresException>(() => session.CommitAsync()); // ...commit geçmez.
            Assert.Equal("23514", ex.SqlState);
            Assert.Equal("ck_tenant_has_owner", ex.ConstraintName);
        }

        Assert.Equal(1L, await env.AdminScalarAsync<long>(
            "SELECT count(*) FROM app.memberships WHERE tenant_id = $1 AND role = 'Owner' AND status = 'Active'", tenant));
    }

    [Fact]
    public async Task RawSql_SwappingOwners_InOneTransaction_IsAllowed()
    {
        var owner = await env.World.CreateUserAsync();
        var tenant = await env.World.CreateTenantAsync(owner);
        var admin = await env.World.CreateMemberAsync(tenant, Kulvar.Platform.Tenancy.TenantRole.Admin);

        await using var session = await RoleSession.OpenAsync(env.Postgres.App(), owner.Id, tenant);
        // Arada Owner'sız an oluşur ama kural commit anında (ertelenmiş) değerlendirilir.
        await session.ExecAsync("UPDATE app.memberships SET role = 'Admin' WHERE tenant_id = $1 AND user_id = $2", tenant, owner.Id);
        await session.ExecAsync("UPDATE app.memberships SET role = 'Owner' WHERE tenant_id = $1 AND user_id = $2", tenant, admin.Id);
        await session.CommitAsync();

        Assert.Equal(admin.Id, await env.AdminScalarAsync<Guid>(
            "SELECT user_id FROM app.memberships WHERE tenant_id = $1 AND role = 'Owner' AND status = 'Active'", tenant));
    }
}
