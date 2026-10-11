using Npgsql;

namespace Kulvar.Testing;

/// <summary>Testlerin sahip olduğu veritabanının kimliği: koşu kimliği + container'ın bildirdiği uç nokta + veritabanı adı.</summary>
public sealed record TestDatabaseManifest(string RunId, string Host, int Port, string Database);

public sealed class TestDatabaseGuardException(string message) : Exception(message);

/// <summary>Guard'ın hedefe yaptığı çağrılar. Birim testinde sahte (spy) uygulamayla "hiç yazma yapılmadı" doğrulanır.</summary>
public interface ITestDatabaseProbe
{
    string Host { get; }
    int Port { get; }
    Task<string?> ReadScalarAsync(string sql, CancellationToken ct);
    Task WriteAsync(string sql, CancellationToken ct);
}

/// <summary>
/// Testler yalnız kendi oluşturduğu veritabanına yazar. Ad öneki tek başına yeterli sayılmaz; ilk yazmadan önce üçü birlikte istenir:
/// (1) açık onay, (2) bağlanılan uç noktanın manifest ile aynı olması, (3) sunucudaki sahiplik işaretinin koşu kimliğiyle eşleşmesi.
/// Kontroller yalnız okuma yapar; herhangi biri tutmazsa hedefe tek bir yazma komutu gönderilmez.
/// </summary>
public sealed class TestDatabaseGuard(ITestDatabaseProbe probe, TestDatabaseManifest manifest, string? optIn)
{
    public const string OptInVariable = "KULVAR_TEST_DB";
    public const string OptInValue = "owned";
    public const string MarkerQuery = "SELECT run_id FROM kulvar_test.run_marker LIMIT 1";

    private bool _verified;

    public async Task VerifyAsync(CancellationToken ct = default)
    {
        if (!string.Equals(optIn, OptInValue, StringComparison.Ordinal))
        {
            throw new TestDatabaseGuardException($"Açık onay yok: {OptInVariable}={OptInValue} bekleniyor.");
        }
        if (!string.Equals(probe.Host, manifest.Host, StringComparison.OrdinalIgnoreCase) || probe.Port != manifest.Port)
        {
            throw new TestDatabaseGuardException("Bağlanılan uç nokta, bu koşunun oluşturduğu container değil.");
        }
        if (!manifest.Database.StartsWith("kulvar_test_", StringComparison.Ordinal))
        {
            throw new TestDatabaseGuardException("Manifest veritabanı adı test veritabanı değil.");
        }

        string? database;
        string? marker;
        try
        {
            database = await probe.ReadScalarAsync("SELECT current_database()", ct);
            marker = await probe.ReadScalarAsync(MarkerQuery, ct);
        }
        catch (Exception ex) when (ex is not TestDatabaseGuardException)
        {
            throw new TestDatabaseGuardException($"Sahiplik işareti okunamadı ({ex.GetType().Name}); hedef bu koşuya ait sayılmaz.");
        }

        if (!string.Equals(database, manifest.Database, StringComparison.Ordinal))
        {
            throw new TestDatabaseGuardException("Bağlanılan veritabanı manifest ile aynı değil.");
        }
        if (!string.Equals(marker, manifest.RunId, StringComparison.Ordinal))
        {
            throw new TestDatabaseGuardException("Sahiplik işareti bu koşuya ait değil.");
        }
        _verified = true;
    }

    /// <summary>Doğrulama geçmeden hedefe yazma gönderilmez.</summary>
    public async Task WriteAsync(string sql, CancellationToken ct = default)
    {
        if (!_verified)
        {
            await VerifyAsync(ct);
        }
        await probe.WriteAsync(sql, ct);
    }
}

public sealed class NpgsqlTestDatabaseProbe(string connectionString) : ITestDatabaseProbe
{
    private readonly NpgsqlConnectionStringBuilder _builder = new(connectionString);

    public string Host => _builder.Host ?? string.Empty;
    public int Port => _builder.Port;

    public async Task<string?> ReadScalarAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync(ct))?.ToString();
    }

    public async Task WriteAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
