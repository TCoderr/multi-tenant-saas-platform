using Kulvar.Api.Hosting;
using Kulvar.IntegrationTests.Infrastructure;
using Kulvar.Modules.Tenancy.Domain;
using Kulvar.Platform.Audit;
using Kulvar.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Kulvar.IntegrationTests.Stage2;

/// <summary>
/// İş birimi çalıştırıcısının kapsam yaşam döngüsü: kapsam yalnız kendi transaction'ında yaşar; hata, iptal ve yeniden deneme
/// sonrasında aynı fiziksel bağlantıyı alan bir sonraki iş birimine taşınmaz.
/// </summary>
public sealed class DbRunnerTests(TestEnvironment env, TwoTenants world) : IClassFixture<TwoTenants>
{
    private AppDb SingleConnectionDb(out NpgsqlDataSource pool)
    {
        pool = NpgsqlDataSource.Create(env.Postgres.App(maxPool: 1));
        return new AppDb(pool, Composition.ModelSource, env.Clock, NullLogger<AppDb>.Instance);
    }

    private static async Task<(int Pid, string? Tenant, string? User, long AuditRows)> ProbeAsync(DbSession session, CancellationToken ct) =>
    (
        (int)(await session.Db.ScalarAsync("SELECT pg_backend_pid()", ct))!,
        (string?)await session.Db.ScalarAsync("SELECT nullif(current_setting('app.tenant_id', true), '')", ct),
        (string?)await session.Db.ScalarAsync("SELECT nullif(current_setting('app.user_id', true), '')", ct),
        (long)(await session.Db.ScalarAsync("SELECT count(*) FROM app.audit_events", ct))!
    );

    [Fact]
    public async Task Scope_DoesNotSurvive_Exception_Cancellation_OrRetry()
    {
        var db = SingleConnectionDb(out var pool);
        await using var _ = pool;
        var scopeA = new DbScope(UserId: world.OwnerA.Id, TenantId: world.TenantA);

        // 1) İstisna: transaction geri alınır, kapsam bağlantıda kalmaz.
        var inside = default((int Pid, string? Tenant, string? User, long AuditRows));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.RunAsync(scopeA, async (session, ct) =>
        {
            inside = await ProbeAsync(session, ct);
            throw new InvalidOperationException("iş kodu hatası");
        }, CancellationToken.None));
        Assert.Equal(world.TenantA.ToString(), inside.Tenant);
        Assert.True(inside.AuditRows > 0);

        var afterException = await db.RunAsync(DbScope.None, ProbeAsync, CancellationToken.None);
        Assert.Equal(inside.Pid, afterException.Pid); // Aynı fiziksel bağlantı
        Assert.Null(afterException.Tenant);
        Assert.Null(afterException.User);
        Assert.Equal(0, afterException.AuditRows);

        // 2) İptal: iş ortasında iptal edilen transaction da kapsam bırakmaz.
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.RunAsync(scopeA, async (session, ct) =>
        {
            await ProbeAsync(session, ct);
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return 0;
        }, cts.Token));
        var afterCancel = await db.RunAsync(DbScope.None, ProbeAsync, CancellationToken.None);
        Assert.Equal(inside.Pid, afterCancel.Pid);
        Assert.Null(afterCancel.Tenant);
        Assert.Equal(0, afterCancel.AuditRows);

        // 3) Yeniden deneme: serileştirme hatasında yeni transaction açılır ve kapsam baştan kurulur.
        var attempts = 0;
        var retried = await db.RunAsync(scopeA, async (session, ct) =>
        {
            attempts++;
            var probe = await ProbeAsync(session, ct);
            if (attempts == 1)
            {
                await session.Db.ExecuteAsync("DO $$ BEGIN RAISE EXCEPTION 'simulated' USING ERRCODE = '40001'; END $$", ct);
            }
            return probe;
        }, CancellationToken.None);
        Assert.Equal(2, attempts);
        Assert.Equal(world.TenantA.ToString(), retried.Tenant);
        Assert.True(retried.AuditRows > 0);

        // 4) Başka şirketin kapsamı aynı bağlantıda öncekinden etkilenmez.
        var asB = await db.RunAsync(new DbScope(UserId: world.OwnerB.Id, TenantId: world.TenantB), async (session, ct) =>
            await session.Db.Set<AuditEvent>().Select(a => a.TenantId).Distinct().ToListAsync(ct), CancellationToken.None);
        Assert.Equal([world.TenantB], asB);

        var afterAll = await db.RunAsync(DbScope.None, ProbeAsync, CancellationToken.None);
        Assert.Null(afterAll.Tenant);
        Assert.Equal(0, afterAll.AuditRows);
    }

    [Fact]
    public async Task NonTransientError_IsNotRetried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<PostgresException>(() => env.App.AppDb.RunAsync(DbScope.None, async (session, ct) =>
        {
            attempts++;
            await session.Db.ExecuteAsync("SELECT 1/0", ct);
            return 0;
        }, CancellationToken.None));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task EnterTenant_Twice_WithDifferentTenant_Throws()
    {
        await env.App.AppDb.RunAsync(DbScope.ForUser(world.OwnerA.Id), async (session, ct) =>
        {
            await session.EnterTenantAsync(world.TenantA, ct);
            await session.EnterTenantAsync(world.TenantA, ct); // Aynı şirket: etkisiz.
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.EnterTenantAsync(world.TenantB, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.EnterUserAsync(world.OwnerB.Id, ct));
            Assert.Equal(world.TenantA, session.TenantId);
            Assert.Equal(world.TenantA.ToString(), await session.Db.ScalarAsync("SELECT current_setting('app.tenant_id', true)", ct));
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RawSql_OutsideUnitOfWork_IsRefused()
    {
        await using var db = new AppDbContext(DbRunner.BuildOptions(env.App.AppDb.DataSource), Composition.ModelSource);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.ScalarAsync("SELECT 1", CancellationToken.None));
    }
}

/// <summary>
/// EF global sorgu filtresi (uygulama katmanı) ve onu atlayan yolların RLS'e (veritabanı katmanı) takılması.
/// İki katman ayrı ayrı sınanır: filtre açıkken, filtre kapalıyken ve toplu işlemlerde.
/// </summary>
public sealed class EfFilterTests(TestEnvironment env, TwoTenants world) : IClassFixture<TwoTenants>
{
    private Task<T> AsTenantA<T>(Func<DbSession, CancellationToken, Task<T>> work) =>
        env.App.AppDb.RunAsync(new DbScope(UserId: world.OwnerA.Id, TenantId: world.TenantA), work, CancellationToken.None);

    [Fact]
    public async Task UnfilteredQuery_ReturnsOnlyScopedTenant()
    {
        var tenants = await AsTenantA(async (session, ct) =>
        {
            // Sorguda hiç Where yok: filtre + RLS yalnız A'yı döndürür.
            var audit = await session.Db.Set<AuditEvent>().Select(a => a.TenantId).Distinct().ToListAsync(ct);
            var members = await session.Db.Set<Membership>().Select(m => m.TenantId).Distinct().ToListAsync(ct);
            return audit.Concat(members).Distinct().ToList();
        });
        Assert.Equal([world.TenantA], tenants);
    }

    [Fact]
    public async Task EfFilter_Alone_ScopesQueries_EvenForSuperuser()
    {
        // Uygulama katmanını tek başına sınamak için RLS'i atlayan superuser bağlantısı kullanılır:
        // burada B satırlarını gizleyen tek şey EF filtresidir.
        await using var pool = NpgsqlDataSource.Create(env.Postgres.Superuser());
        var superDb = new AppDb(pool, Composition.ModelSource, env.Clock, NullLogger<AppDb>.Instance);

        var filtered = await superDb.RunAsync(DbScope.ForTenant(world.TenantA), async (session, ct) =>
            await session.Db.Set<AuditEvent>().Select(a => a.TenantId).Distinct().ToListAsync(ct), CancellationToken.None);
        Assert.Equal([world.TenantA], filtered);

        // Kapsam bağlanmamış context: filtre boş kimlikle eşleşir, hiçbir satır dönmez (superuser olsa bile).
        var unscoped = await superDb.RunAsync(DbScope.None, async (session, ct) =>
            await session.Db.Set<AuditEvent>().CountAsync(ct), CancellationToken.None);
        Assert.Equal(0, unscoped);

        // Filtre kapatılınca superuser her şeyi görür: bu yüzden runtime rolü superuser olamaz ve RLS ikinci katmandır.
        var bypassed = await superDb.RunAsync(DbScope.ForTenant(world.TenantA), async (session, ct) =>
            await session.Db.Set<AuditEvent>().IgnoreQueryFilters().Select(a => a.TenantId).Distinct().CountAsync(ct), CancellationToken.None);
        Assert.True(bypassed >= 2);
    }

    [Fact]
    public async Task IgnoreQueryFilters_StillBoundedByRls()
    {
        var tenants = await AsTenantA(async (session, ct) =>
        {
            var audit = await session.Db.Set<AuditEvent>().IgnoreQueryFilters().Select(a => a.TenantId).Distinct().ToListAsync(ct);
            var explicitB = await session.Db.Set<AuditEvent>().IgnoreQueryFilters().CountAsync(a => a.TenantId == world.TenantB, ct);
            Assert.Equal(0, explicitB);
            return audit;
        });
        Assert.Equal([world.TenantA], tenants);
    }

    [Fact]
    public async Task RawSql_InScope_StillBoundedByRls()
    {
        var count = await AsTenantA(async (session, ct) =>
            (long)(await session.Db.ScalarAsync("SELECT count(*) FROM app.audit_events WHERE tenant_id = $1", ct, world.TenantB))!);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ExecuteUpdate_ExecuteDelete_CannotCrossTenant()
    {
        var beforeB = await env.AdminScalarAsync<string>(
            "SELECT string_agg(user_id::text || ':' || role || ':' || status || ':' || version::text, ',' ORDER BY user_id) FROM app.memberships WHERE tenant_id = $1", world.TenantB);

        var affected = await AsTenantA(async (session, ct) =>
        {
            // Filtre açık ve kapalı: toplu güncelleme B'ye dokunamaz.
            var filtered = await session.Db.Set<Membership>().Where(m => m.TenantId == world.TenantB)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.Version, m => m.Version + 100), ct);
            var unfiltered = await session.Db.Set<Membership>().IgnoreQueryFilters().Where(m => m.TenantId == world.TenantB)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.Version, m => m.Version + 100), ct);
            return filtered + unfiltered;
        });
        Assert.Equal(0, affected);

        // Toplu silme: uygulama rolünün üyelik silme yetkisi yoktur; B'yi hedefleyen silme veritabanında reddedilir.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => AsTenantA(async (session, ct) =>
            await session.Db.Set<Membership>().IgnoreQueryFilters().Where(m => m.TenantId == world.TenantB).ExecuteDeleteAsync(ct)));
        Assert.Equal("42501", ex.SqlState);

        var afterB = await env.AdminScalarAsync<string>(
            "SELECT string_agg(user_id::text || ':' || role || ':' || status || ':' || version::text, ',' ORDER BY user_id) FROM app.memberships WHERE tenant_id = $1", world.TenantB);
        Assert.Equal(beforeB, afterB);
    }

    [Fact]
    public async Task TrackedEntity_WithForeignTenantId_CannotBeInserted()
    {
        // Kod hatası senaryosu: A kapsamında B kimlikli satır eklenmeye çalışılır. EF filtresi INSERT'i durdurmaz; RLS durdurur.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => AsTenantA(async (session, ct) =>
        {
            session.Db.Add(AuditEvent.Create(world.TenantB, world.OwnerA.Id, "test.cross_tenant_insert", "test", null, null, null, session.Now));
            return await session.Db.SaveChangesAsync(ct);
        }));
        Assert.Equal("42501", PostgresErrors.SqlState(ex));
        Assert.Equal(0L, await env.AdminScalarAsync<long>("SELECT count(*) FROM app.audit_events WHERE action = 'test.cross_tenant_insert'"));
    }
}
