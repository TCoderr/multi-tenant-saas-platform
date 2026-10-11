using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Kulvar.Platform.Data;

/// <summary>Bir iş biriminin başlangıç kapsamı. Değişmez; şirket kapsamı sonradan yalnız bir kez eklenebilir.</summary>
public sealed record DbScope(Guid? UserId = null, Guid? TenantId = null, string? LoginEmail = null, string? InviteTokenHashHex = null)
{
    public static DbScope None { get; } = new();
    public static DbScope ForUser(Guid userId) => new(UserId: userId);
    public static DbScope ForTenant(Guid tenantId) => new(TenantId: tenantId);
}

/// <summary>Tek transaction + onun RLS kapsamı. Transaction bitince kapsam veritabanı bağlantısında kalmaz.</summary>
public sealed class DbSession
{
    internal DbSession(AppDbContext db, TimeProvider clock)
    {
        Db = db;
        Now = clock.GetUtcNow();
    }

    public AppDbContext Db { get; }
    public Guid? UserId { get; private set; }
    public Guid? TenantId { get; private set; }

    /// <summary>İş biriminin sunucu saati (UTC). Aynı iş birimindeki tüm satırlar aynı anı kullanır.</summary>
    public DateTimeOffset Now { get; }

    internal async Task ApplyAsync(DbScope scope, CancellationToken ct)
    {
        if (scope.LoginEmail is { } email)
        {
            await SetAsync("app.login_email", email, ct);
        }
        if (scope.InviteTokenHashHex is { } hash)
        {
            await SetAsync("app.invite_token_hash", hash, ct);
        }
        if (scope.UserId is { } userId)
        {
            await EnterUserAsync(userId, ct);
        }
        if (scope.TenantId is { } tenantId)
        {
            await EnterTenantAsync(tenantId, ct);
        }
    }

    /// <summary>Kullanıcı kapsamını ayarlar (ör. parola doğrulandıktan sonra). Bir iş biriminde değiştirilemez.</summary>
    public async Task EnterUserAsync(Guid userId, CancellationToken ct)
    {
        if (UserId is { } current)
        {
            if (current != userId)
            {
                throw new InvalidOperationException("Kullanıcı kapsamı bu iş biriminde zaten ayarlı; değiştirilemez.");
            }
            return;
        }
        await SetAsync("app.user_id", userId.ToString("D"), ct);
        UserId = userId;
    }

    /// <summary>
    /// Şirket kapsamını ayarlar. Çağıran, üyeliği (veya belgelenmiş iki istisnadan birini) önceden doğrulamış olmalıdır.
    /// Bir iş biriminde yalnız bir şirkete girilebilir.
    /// </summary>
    public async Task EnterTenantAsync(Guid tenantId, CancellationToken ct)
    {
        if (TenantId is { } current)
        {
            if (current != tenantId)
            {
                throw new InvalidOperationException("Şirket kapsamı bu iş biriminde zaten ayarlı; değiştirilemez.");
            }
            return;
        }
        Db.BindTenant(tenantId);
        await SetAsync("app.tenant_id", tenantId.ToString("D"), ct);
        TenantId = tenantId;
    }

    // set_config(..., true): SET LOCAL eşdeğeri, parametreli. Commit/rollback ile kaybolur.
    private Task SetAsync(string key, string value, CancellationToken ct) =>
        Db.Database.ExecuteSqlAsync($"SELECT set_config({key}, {value}, true)", ct);
}

/// <summary>
/// Bir veritabanı rolü için iş birimi çalıştırıcısı. Her çağrı yeni context + yeni transaction'dır;
/// serileştirme/deadlock hatasında iş yeni transaction'da ve kapsam yeniden kurularak tekrarlanır.
/// </summary>
public class DbRunner
{
    private const int MaxAttempts = 3;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AppModelSource _model;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    public DbRunner(NpgsqlDataSource dataSource, AppModelSource model, TimeProvider clock, ILogger log)
    {
        DataSource = dataSource;
        _model = model;
        _clock = clock;
        _log = log;
        _options = BuildOptions(dataSource);
    }

    public NpgsqlDataSource DataSource { get; }

    public static DbContextOptions<AppDbContext> BuildOptions(NpgsqlDataSource dataSource) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(dataSource, o => o.MigrationsAssembly("Kulvar.Api").MigrationsHistoryTable("__ef_migrations_history", "app"))
            .UseSnakeCaseNamingConvention()
            .Options;

    public async Task<T> RunAsync<T>(DbScope scope, Func<DbSession, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = new AppDbContext(_options, _model);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var session = new DbSession(db, _clock);
                await session.ApplyAsync(scope, ct);
                var result = await work(session, ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (Exception ex) when (attempt < MaxAttempts && PostgresErrors.IsSerializationOrDeadlock(ex))
            {
                _log.LogInformation("İş birimi yeniden deneniyor (deneme {Attempt}, sqlstate {SqlState})", attempt, PostgresErrors.SqlState(ex));
                // Dispose rollback yapar; yeni denemede kapsam baştan kurulur.
            }
        }
    }

    public Task RunAsync(DbScope scope, Func<DbSession, CancellationToken, Task> work, CancellationToken ct) =>
        RunAsync<object?>(scope, async (s, c) =>
        {
            await work(s, c);
            return null;
        }, ct);
}

/// <summary>API'nin kullanıcı/şirket kapsamlı istekleri: <c>kulvar_app</c> rolü.</summary>
public sealed class AppDb(NpgsqlDataSource dataSource, AppModelSource model, TimeProvider clock, ILogger<AppDb> log)
    : DbRunner(dataSource, model, clock, log);

/// <summary>Platform operatörü uçları ve webhook alımı: <c>kulvar_platform</c> rolü (içerik tablolarında yetkisi yok).</summary>
public sealed class PlatformDb(NpgsqlDataSource dataSource, AppModelSource model, TimeProvider clock, ILogger<PlatformDb> log)
    : DbRunner(dataSource, model, clock, log);

/// <summary>Arka plan işleri: <c>kulvar_worker</c> rolü.</summary>
public sealed class WorkerDb(NpgsqlDataSource dataSource, AppModelSource model, TimeProvider clock, ILogger<WorkerDb> log)
    : DbRunner(dataSource, model, clock, log);

public static class PostgresErrors
{
    public const string UniqueViolation = "23505";
    public const string ForeignKeyViolation = "23503";

    /// <summary>ON DELETE RESTRICT ihlali (başvurulan satır silinemez).</summary>
    public const string RestrictViolation = "23001";
    public const string CheckViolation = "23514";
    public const string InsufficientPrivilege = "42501";

    public static PostgresException? Find(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg)
            {
                return pg;
            }
        }
        return null;
    }

    public static string? SqlState(Exception ex) => Find(ex)?.SqlState;

    public static bool IsSerializationOrDeadlock(Exception ex) => SqlState(ex) is "40001" or "40P01";

    public static bool IsUniqueViolation(Exception ex, string? constraint = null) =>
        Find(ex) is { SqlState: UniqueViolation } pg && (constraint is null || pg.ConstraintName == constraint);

    public static bool IsConstraint(Exception ex, string sqlState, string constraint) =>
        Find(ex) is { } pg && pg.SqlState == sqlState && pg.ConstraintName == constraint;

    /// <summary>Bağlantı kurulamadı / koptu: bağımlılık kullanılamıyor (iş kuralı hatası değil).</summary>
    public static bool IsUnavailable(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg)
            {
                // 57P01..57P03: admin shutdown / crash shutdown / cannot connect now; 08xxx: bağlantı hataları; 53300: too many connections.
                return pg.SqlState.StartsWith("08", StringComparison.Ordinal) || pg.SqlState is "57P01" or "57P02" or "57P03" or "53300";
            }
            if (e is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
            {
                return true;
            }
        }
        return false;
    }
}
