using Npgsql;

namespace Kulvar.IntegrationTests.Infrastructure;

/// <summary>
/// Belirli bir veritabanı rolüyle açılmış tek transaction: RLS testleri EF kullanmadan, doğrudan SQL ile konuşur.
/// Kapsam anahtarları üretimdeki gibi <c>set_config(..., true)</c> ile ayarlanır. Hata beklenen deyimler savepoint içinde çalışır.
/// </summary>
public sealed class RoleSession : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private int _savepoint;

    private RoleSession(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public static async Task<RoleSession> OpenAsync(string connectionString, Guid? userId = null, Guid? tenantId = null, NpgsqlDataSource? dataSource = null)
    {
        var connection = dataSource is null ? new NpgsqlConnection(connectionString) : dataSource.CreateConnection();
        await connection.OpenAsync();
        var session = new RoleSession(connection, await connection.BeginTransactionAsync());
        if (userId is { } u)
        {
            await session.SetAsync("app.user_id", u.ToString());
        }
        if (tenantId is { } t)
        {
            await session.SetAsync("app.tenant_id", t.ToString());
        }
        return session;
    }

    public Task SetAsync(string key, string value) => ExecAsync("SELECT set_config($1, $2, true)", key, value);

    public async Task<object?> ScalarAsync(string sql, params object?[] args)
    {
        await using var command = Create(sql, args);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    public async Task<long> CountAsync(string sql, params object?[] args) => Convert.ToInt64(await ScalarAsync(sql, args), System.Globalization.CultureInfo.InvariantCulture);

    public async Task<int> ExecAsync(string sql, params object?[] args)
    {
        await using var command = Create(sql, args);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<List<string>> ColumnAsync(string sql, params object?[] args)
    {
        await using var command = Create(sql, args);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString()!);
        }
        return values;
    }

    /// <summary>Deyimi savepoint içinde çalıştırır. Başarılıysa etkilenen satır sayısını, hata verirse SQLSTATE'i döner; transaction kullanılabilir kalır.</summary>
    public async Task<(int Affected, string? SqlState)> TryExecAsync(string sql, params object?[] args)
    {
        var name = $"sp_{++_savepoint}";
        await _transaction.SaveAsync(name);
        try
        {
            var affected = await ExecAsync(sql, args);
            await _transaction.ReleaseAsync(name);
            return (affected, null);
        }
        catch (PostgresException ex)
        {
            await _transaction.RollbackAsync(name);
            return (0, ex.SqlState);
        }
    }

    public Task CommitAsync() => _transaction.CommitAsync();
    public Task RollbackAsync() => _transaction.RollbackAsync();

    private NpgsqlCommand Create(string sql, object?[] args)
    {
        var command = new NpgsqlCommand(sql, _connection, _transaction);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }
        return command;
    }

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
