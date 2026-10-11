using Kulvar.IntegrationTests.Infrastructure;
using Npgsql;

namespace Kulvar.IntegrationTests.Stage2;

/// <summary>İki bağımsız şirket (A/B), sahipleri ve her kiracı tablosunda birer satır. Satırlar superuser ile eklenir (kurulum).</summary>
public sealed class TwoTenants(TestEnvironment env) : IAsyncLifetime
{
    public TestUser OwnerA { get; private set; } = null!;
    public TestUser OwnerB { get; private set; } = null!;
    public TestUser SeedSpareA { get; private set; } = null!;
    public TestUser SeedSpareB { get; private set; } = null!;
    public TestUser Outsider { get; private set; } = null!;
    public Guid TenantA { get; private set; }
    public Guid TenantB { get; private set; }

    public async ValueTask InitializeAsync()
    {
        OwnerA = await env.World.CreateUserAsync("RLS Owner A");
        OwnerB = await env.World.CreateUserAsync("RLS Owner B");
        SeedSpareA = await env.World.CreateUserAsync("RLS Spare A");
        SeedSpareB = await env.World.CreateUserAsync("RLS Spare B");
        Outsider = await env.World.CreateUserAsync("RLS Outsider");
        TenantA = await env.World.CreateTenantAsync(OwnerA, "CANARY-A-RLS");
        TenantB = await env.World.CreateTenantAsync(OwnerB, "CANARY-B-RLS");
        foreach (var table in SchemaCatalog.TenantTables)
        {
            await env.AdminExecuteAsync(table.InsertSql, TenantA, OwnerA.Id, SeedSpareA.Id);
            await env.AdminExecuteAsync(table.InsertSql, TenantB, OwnerB.Id, SeedSpareB.Id);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// RLS'in doğrudan SQL ile, gerçek kısıtlı runtime rolleriyle doğrulanması. EF ve uygulama kodu devrede değildir:
/// burada geçen her şey, uygulamadaki bir filtre unutulsa bile veritabanının tek başına verdiği güvencedir.
/// </summary>
public sealed class RlsSqlTests(TestEnvironment env, TwoTenants world) : IClassFixture<TwoTenants>
{
    private const string RlsViolation = "42501"; // insufficient_privilege: hem yetki reddi hem RLS WITH CHECK ihlali bu kodu verir.

    public static TheoryData<string> TenantTableNames => new(SchemaCatalog.TenantTables.Select(t => t.Name));

    private static TenantTable Table(string name) => SchemaCatalog.TenantTables.Single(t => t.Name == name);

    [Theory]
    [MemberData(nameof(TenantTableNames))]
    public async Task MissingContext_SelectsNothing_WritesFail(string tableName)
    {
        var table = Table(tableName);
        Assert.True(await env.AdminScalarAsync<long>($"SELECT count(*) FROM app.{table.Name}") >= 2, "Kurulum satırları yok.");

        await using var session = await RoleSession.OpenAsync(env.Postgres.App());
        Assert.Equal(0, await session.CountAsync($"SELECT count(*) FROM app.{table.Name}"));

        var insert = await session.TryExecAsync(table.InsertSql, world.TenantA, world.OwnerA.Id, world.Outsider.Id);
        Assert.Equal(RlsViolation, insert.SqlState);

        var update = await session.TryExecAsync($"UPDATE app.{table.Name} SET tenant_id = tenant_id");
        Assert.True(update is { Affected: 0, SqlState: null } or { SqlState: RlsViolation });
        var delete = await session.TryExecAsync($"DELETE FROM app.{table.Name}");
        Assert.True(delete is { Affected: 0, SqlState: null } or { SqlState: RlsViolation });
    }

    [Fact]
    public async Task MissingContext_GlobalAndQueueTables_AlsoFailClosed()
    {
        await using var session = await RoleSession.OpenAsync(env.Postgres.App());
        foreach (var table in new[] { "users", "sessions", "user_tokens", "platform_operators", "tenants", "sink_messages" })
        {
            Assert.Equal(0, await session.CountAsync($"SELECT count(*) FROM app.{table}"));
        }
        // Kuyruğu uygulama rolü hiç okuyamaz (yalnız ekleyebilir) ve kapsam olmadan ekleyemez.
        Assert.Equal(RlsViolation, (await session.TryExecAsync("SELECT count(*) FROM app.outbox_messages")).SqlState);
        var enqueue = await session.TryExecAsync(
            "INSERT INTO app.outbox_messages (id, tenant_id, type, payload, status, attempts, available_at, lease_token, created_at) VALUES (gen_random_uuid(), $1, 'x', '{}', 'Pending', 0, now(), 0, now())",
            world.TenantA);
        Assert.Equal(RlsViolation, enqueue.SqlState);
        Assert.Equal(RlsViolation, (await session.TryExecAsync(
            "INSERT INTO app.tenants (id, name, status, created_by_user_id, created_at, version) VALUES (gen_random_uuid(), 'x', 'Active', $1, now(), 1)", world.OwnerA.Id)).SqlState);
    }

    [Fact]
    public async Task InvalidContextValue_FailsClosed_WithError()
    {
        await using var session = await RoleSession.OpenAsync(env.Postgres.App());
        await session.SetAsync("app.tenant_id", "not-a-uuid");
        var result = await session.TryExecAsync("SELECT count(*) FROM app.memberships");
        Assert.Equal("22P02", result.SqlState); // invalid_text_representation: sorgu satır döndürmek yerine hata verir.
    }

    [Theory]
    [MemberData(nameof(TenantTableNames))]
    public async Task TenantA_CannotSelectInsertUpdateDelete_TenantB(string tableName)
    {
        var table = Table(tableName);
        var rowsA = await env.AdminScalarAsync<long>($"SELECT count(*) FROM app.{table.Name} WHERE tenant_id = $1", world.TenantA);
        var rowsB = await env.AdminScalarAsync<long>($"SELECT count(*) FROM app.{table.Name} WHERE tenant_id = $1", world.TenantB);
        Assert.True(rowsA > 0 && rowsB > 0, "Kurulum satırları yok.");

        await using (var session = await RoleSession.OpenAsync(env.Postgres.App(), world.OwnerA.Id, world.TenantA))
        {
            // SELECT: yalnız A; B'yi açıkça istemek de satır döndürmez.
            Assert.Equal(rowsA, await session.CountAsync($"SELECT count(*) FROM app.{table.Name}"));
            Assert.Equal(0, await session.CountAsync($"SELECT count(*) FROM app.{table.Name} WHERE tenant_id = $1", world.TenantB));

            // INSERT: B kimlikli satır eklenemez.
            Assert.Equal(RlsViolation, (await session.TryExecAsync(table.InsertSql, world.TenantB, world.OwnerB.Id, world.Outsider.Id)).SqlState);

            // UPDATE / DELETE: B satırları görünmediği için etkilenmez (veya rolün o yetkisi hiç yoktur).
            var update = await session.TryExecAsync($"UPDATE app.{table.Name} SET tenant_id = tenant_id WHERE tenant_id = $1", world.TenantB);
            Assert.True(update is { Affected: 0, SqlState: null } or { SqlState: RlsViolation }, $"UPDATE: {update}");
            Assert.Equal(table.AppCanUpdate, update.SqlState is null);
            var delete = await session.TryExecAsync($"DELETE FROM app.{table.Name} WHERE tenant_id = $1", world.TenantB);
            Assert.True(delete is { Affected: 0, SqlState: null } or { SqlState: RlsViolation }, $"DELETE: {delete}");
            Assert.Equal(table.AppCanDelete, delete.SqlState is null);

            // Koşulsuz toplu UPDATE/DELETE de yalnız A'ya dokunabilir (aşağıda B sayısı doğrulanır); etkiyi geri al.
            await session.TryExecAsync($"UPDATE app.{table.Name} SET tenant_id = tenant_id");
            await session.RollbackAsync();
        }

        Assert.Equal(rowsB, await env.AdminScalarAsync<long>($"SELECT count(*) FROM app.{table.Name} WHERE tenant_id = $1", world.TenantB));
    }

    [Theory]
    [MemberData(nameof(TenantTableNames))]
    public async Task Update_TenantId_ToOtherTenant_Rejected(string tableName)
    {
        var table = Table(tableName);
        await using var session = await RoleSession.OpenAsync(env.Postgres.App(), world.OwnerA.Id, world.TenantA);
        // A'nın kendi satırını B'ye "taşımak": WITH CHECK ihlali (veya UPDATE yetkisi yok). Hiçbir durumda başarılı olamaz.
        var move = await session.TryExecAsync($"UPDATE app.{table.Name} SET tenant_id = $1 WHERE tenant_id = $2", world.TenantB, world.TenantA);
        Assert.Equal(RlsViolation, move.SqlState);
    }

    [Fact]
    public async Task PooledConnection_A_Then_B_Then_Missing_NoLeak()
    {
        // Havuz tek bağlantılı: üç transaction da aynı fiziksel bağlantıyı kullanmak zorunda.
        await using var pool = NpgsqlDataSource.Create(env.Postgres.App(maxPool: 1));
        var pids = new List<int>();

        await using (var a = await RoleSession.OpenAsync(string.Empty, world.OwnerA.Id, world.TenantA, pool))
        {
            pids.Add((int)(await a.ScalarAsync("SELECT pg_backend_pid()"))!);
            Assert.Equal([world.TenantA.ToString()], await a.ColumnAsync("SELECT DISTINCT tenant_id::text FROM app.audit_events"));
            await a.CommitAsync();
        }
        await using (var b = await RoleSession.OpenAsync(string.Empty, world.OwnerB.Id, world.TenantB, pool))
        {
            pids.Add((int)(await b.ScalarAsync("SELECT pg_backend_pid()"))!);
            Assert.Equal([world.TenantB.ToString()], await b.ColumnAsync("SELECT DISTINCT tenant_id::text FROM app.audit_events"));
            await b.RollbackAsync(); // Rollback de kapsamı temizler.
        }
        await using (var none = await RoleSession.OpenAsync(string.Empty, dataSource: pool))
        {
            pids.Add((int)(await none.ScalarAsync("SELECT pg_backend_pid()"))!);
            Assert.Null(await none.ScalarAsync("SELECT nullif(current_setting('app.tenant_id', true), '')"));
            Assert.Null(await none.ScalarAsync("SELECT nullif(current_setting('app.user_id', true), '')"));
            Assert.Equal(0, await none.CountAsync("SELECT count(*) FROM app.audit_events"));
            Assert.Equal(0, await none.CountAsync("SELECT count(*) FROM app.memberships"));
        }

        Assert.Single(pids.Distinct()); // Gerçekten aynı backend yeniden kullanıldı.
    }

    [Fact]
    public async Task OwnerRole_SeesNoTenantRows_BecauseForced()
    {
        // Migration rolü tabloların sahibidir; FORCE olmasa RLS'i atlardı.
        await using var session = await RoleSession.OpenAsync(env.Postgres.Owner(), world.OwnerA.Id, world.TenantA);
        foreach (var table in new[] { "memberships", "audit_events", "tenants", "users", "sessions", "sink_messages", "outbox_messages" })
        {
            Assert.Equal(0, await session.CountAsync($"SELECT count(*) FROM app.{table}"));
        }
        var forced = await env.AdminScalarAsync<long>(
            "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'app' AND c.relkind = 'r' AND c.relrowsecurity AND NOT c.relforcerowsecurity");
        Assert.Equal(0, forced);
    }

    [Fact]
    public async Task PlatformRole_DeniedOnContentTables()
    {
        await using var session = await RoleSession.OpenAsync(env.Postgres.Platform(), world.OwnerA.Id, world.TenantA);
        foreach (var table in SchemaCatalog.PlatformForbidden)
        {
            Assert.Equal(RlsViolation, (await session.TryExecAsync($"SELECT 1 FROM app.{table} LIMIT 1")).SqlState);
            Assert.Equal(RlsViolation, (await session.TryExecAsync($"DELETE FROM app.{table}")).SqlState);
            Assert.False(await env.AdminScalarAsync<bool>(
                "SELECT has_table_privilege('kulvar_platform', $1, 'SELECT') OR has_table_privilege('kulvar_platform', $1, 'INSERT') OR has_table_privilege('kulvar_platform', $1, 'UPDATE') OR has_table_privilege('kulvar_platform', $1, 'DELETE')",
                $"app.{table}"));
        }
        // Metadata görünür: şirket kaydı (ad, durum) – içerik değil.
        Assert.True(await session.CountAsync("SELECT count(*) FROM app.tenants WHERE id IN ($1, $2)", world.TenantA, world.TenantB) == 2);
    }

    [Fact]
    public async Task AppRole_DeniedOnPlatformTables_AndProviderState()
    {
        await using var session = await RoleSession.OpenAsync(env.Postgres.App(), world.OwnerA.Id, world.TenantA);
        foreach (var table in SchemaCatalog.AppForbidden.Select(t => $"app.{t}").Concat(["fakepsp.operations", "fakepsp.subscriptions", "fakepsp.webhooks", "fakepsp.simulation_requests"]))
        {
            Assert.Equal(RlsViolation, (await session.TryExecAsync($"SELECT 1 FROM {table} LIMIT 1")).SqlState);
        }
        // Referans veri: herkes okur, hiçbir runtime rolü yazamaz.
        Assert.Equal(3, await session.CountAsync("SELECT count(*) FROM app.plans"));
        Assert.Equal(RlsViolation, (await session.TryExecAsync("UPDATE app.plan_versions SET seat_limit = 9999")).SqlState);
        Assert.Equal(RlsViolation, (await session.TryExecAsync("INSERT INTO app.plans (code, name, sort_order) VALUES ('free', 'Free', 0)")).SqlState);
        // Uygulama rolü ödeme işlemini isteyebilir (INSERT) ama sonucunu yazamaz: durum yalnız worker/operatör yolundan değişir.
        Assert.Equal(RlsViolation, (await session.TryExecAsync("UPDATE app.billing_operations SET status = 'Succeeded' WHERE tenant_id = $1", world.TenantA)).SqlState);
        // Platform operatörü simüle sağlayıcının durumunu okuyamaz; yalnız simülasyon isteği ekleyebilir.
        await using var platform = await RoleSession.OpenAsync(env.Postgres.Platform());
        Assert.Equal(RlsViolation, (await platform.TryExecAsync("SELECT 1 FROM fakepsp.operations LIMIT 1")).SqlState);
        Assert.Equal(RlsViolation, (await platform.TryExecAsync("UPDATE app.subscriptions SET status = 'Active'")).SqlState);
    }

    [Fact]
    public async Task RuntimeRoles_NotSuperuser_NotOwner_NoBypassRls()
    {
        foreach (var role in new[] { "kulvar_app", "kulvar_platform", "kulvar_worker", "kulvar_owner" })
        {
            Assert.Equal("f,f,f,f", await env.AdminScalarAsync<string>(
                "SELECT concat_ws(',', rolsuper::text::char, rolbypassrls::text::char, rolcreatedb::text::char, rolcreaterole::text::char) FROM pg_roles WHERE rolname = $1", role));
        }
        foreach (var role in new[] { "kulvar_app", "kulvar_platform", "kulvar_worker" })
        {
            Assert.Equal(0, await env.AdminScalarAsync<long>(
                "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace JOIN pg_roles r ON r.oid = c.relowner WHERE n.nspname IN ('app', 'fakepsp') AND r.rolname = $1", role));
            Assert.False(await env.AdminScalarAsync<bool>("SELECT has_schema_privilege($1, 'app', 'CREATE')", role));
            Assert.False(await env.AdminScalarAsync<bool>("SELECT has_schema_privilege($1, 'public', 'CREATE')", role));
            Assert.False(await env.AdminScalarAsync<bool>("SELECT has_database_privilege($1, current_database(), 'CREATE')", role));
            Assert.Equal(0, await env.AdminScalarAsync<long>(
                "SELECT count(*) FROM information_schema.role_table_grants WHERE grantee = $1 AND table_schema = 'app' AND privilege_type IN ('TRUNCATE', 'REFERENCES', 'TRIGGER')", role));
        }

        // Bağlantının gerçekten o rolle açıldığı (testlerin superuser ile koşmadığı) doğrulanır.
        await using var session = await RoleSession.OpenAsync(env.Postgres.App());
        Assert.Equal("kulvar_app", await session.ScalarAsync("SELECT current_user::text"));
        Assert.Equal("off", await session.ScalarAsync("SELECT current_setting('is_superuser')"));
        Assert.Equal(RlsViolation, (await session.TryExecAsync("CREATE TABLE app.should_not_exist (id int)")).SqlState);
        Assert.Equal(RlsViolation, (await session.TryExecAsync("ALTER TABLE app.memberships DISABLE ROW LEVEL SECURITY")).SqlState);
        Assert.Equal(RlsViolation, (await session.TryExecAsync("TRUNCATE app.audit_events")).SqlState);
    }

    [Fact]
    public async Task UserScope_SeesOwnMemberships_ButNoTenantContent()
    {
        // Ortak kullanıcı: iki şirkete de üye (kurulumda Viewer olarak eklendi → yalnız A; B'ye de ekleyelim).
        var shared = await env.World.CreateUserAsync("RLS Ortak");
        await env.World.AddMemberDirectAsync(world.TenantA, shared, Kulvar.Platform.Tenancy.TenantRole.Agent);
        await env.World.AddMemberDirectAsync(world.TenantB, shared, Kulvar.Platform.Tenancy.TenantRole.Admin);

        await using var session = await RoleSession.OpenAsync(env.Postgres.App(), shared.Id);
        // Kullanıcı kapsamı: yalnız kendi üyelikleri ve üyesi olduğu şirketlerin kaydı.
        Assert.Equal(2, await session.CountAsync("SELECT count(*) FROM app.memberships"));
        Assert.Equal(0, await session.CountAsync("SELECT count(*) FROM app.memberships WHERE user_id <> $1", shared.Id));
        Assert.Equal(2, await session.CountAsync("SELECT count(*) FROM app.tenants"));
        Assert.Equal(1, await session.CountAsync("SELECT count(*) FROM app.users"));
        // İçerik: kullanıcı kapsamı tek başına hiçbir şirket verisini açmaz.
        Assert.Equal(0, await session.CountAsync("SELECT count(*) FROM app.audit_events"));

        // Şirket kapsamına girince: yalnız o şirket. Diğer şirketteki kendi üyeliğim bile artık görünmez.
        await session.SetAsync("app.tenant_id", world.TenantA.ToString());
        Assert.Equal([world.TenantA.ToString()], await session.ColumnAsync("SELECT DISTINCT tenant_id::text FROM app.memberships"));
        Assert.Equal([world.TenantA.ToString()], await session.ColumnAsync("SELECT id::text FROM app.tenants"));
        Assert.Equal(0, await session.CountAsync("SELECT count(*) FROM app.users u WHERE u.id = $1", world.OwnerB.Id));
        Assert.Equal(1, await session.CountAsync("SELECT count(*) FROM app.users u WHERE u.id = $1", world.OwnerA.Id));
    }

    [Fact]
    public async Task UserScope_OtherUsersSessionsAndTokens_Invisible()
    {
        await using var session = await RoleSession.OpenAsync(env.Postgres.App(), world.OwnerA.Id);
        Assert.Equal(0, await session.CountAsync("SELECT count(*) FROM app.sessions WHERE user_id <> $1", world.OwnerA.Id));
        Assert.True(await session.CountAsync("SELECT count(*) FROM app.sessions") >= 1);
        Assert.Equal(0, await session.CountAsync("SELECT count(*) FROM app.user_tokens WHERE user_id <> $1", world.OwnerA.Id));
        Assert.Equal(0, await session.CountAsync("SELECT count(*) FROM app.sink_messages WHERE recipient_normalized_email <> $1", world.OwnerA.Email));
        // Başkasının oturumunu iptal edemez / kendi adına oturum uyduramaz.
        Assert.Equal(0, (await session.TryExecAsync("UPDATE app.sessions SET revoked_at = now() WHERE user_id = $1", world.OwnerB.Id)).Affected);
        Assert.Equal(RlsViolation, (await session.TryExecAsync(
            "INSERT INTO app.sessions (id, user_id, created_at, expires_at) VALUES (gen_random_uuid(), $1, now(), now() + interval '1 hour')", world.OwnerB.Id)).SqlState);
    }

    [Fact]
    public async Task LoginScope_RevealsOnlyThatEmailRow()
    {
        await using var session = await RoleSession.OpenAsync(env.Postgres.App());
        await session.SetAsync("app.login_email", world.OwnerA.Email);
        Assert.Equal([world.OwnerA.Id.ToString()], await session.ColumnAsync("SELECT id::text FROM app.users"));
        Assert.Equal(0, await session.CountAsync("SELECT count(*) FROM app.memberships"));
        Assert.Equal(0, await session.CountAsync("SELECT count(*) FROM app.tenants"));
    }

    [Fact]
    public async Task WorkerRole_NeedsTenantScope_ForContent_ButSeesQueue()
    {
        await using var session = await RoleSession.OpenAsync(env.Postgres.Worker());
        Assert.Equal(0, await session.CountAsync("SELECT count(*) FROM app.audit_events"));
        Assert.Equal(0, await session.CountAsync("SELECT count(*) FROM app.memberships"));
        Assert.Equal(0, await session.CountAsync("SELECT count(*) FROM app.users"));
        Assert.True(await session.CountAsync("SELECT count(*) FROM app.outbox_messages") > 0);
        Assert.True(await session.CountAsync("SELECT count(*) FROM app.tenants") >= 2);

        await session.SetAsync("app.tenant_id", world.TenantA.ToString());
        Assert.Equal([world.TenantA.ToString()], await session.ColumnAsync("SELECT DISTINCT tenant_id::text FROM app.audit_events"));
        // Worker'ın da BYPASSRLS'i yoktur: A kapsamında B satırını silemez.
        Assert.Equal(0, (await session.TryExecAsync("DELETE FROM app.audit_events WHERE tenant_id = $1", world.TenantB)).Affected);
    }
}
