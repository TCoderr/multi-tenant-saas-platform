using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Kulvar.Platform.Data;

/// <summary>
/// Parametreli ham SQL (kilitler, tek deyimli sayaçlar, kuyruk kiralama). Dize birleştirme yoktur: değerler yalnız $1..$n ile geçer.
/// Komut her zaman context'in açık transaction'ında çalışır; RLS kapsamı o transaction'a aittir.
/// </summary>
public static class Sql
{
    public static async Task<List<T>> QueryAsync<T>(this AppDbContext db, string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct, params object?[] args)
    {
        await using var command = Create(db, sql, args);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(map(reader));
        }
        return rows;
    }

    public static async Task<int> ExecuteAsync(this AppDbContext db, string sql, CancellationToken ct, params object?[] args)
    {
        await using var command = Create(db, sql, args);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<object?> ScalarAsync(this AppDbContext db, string sql, CancellationToken ct, params object?[] args)
    {
        await using var command = Create(db, sql, args);
        var value = await command.ExecuteScalarAsync(ct);
        return value is DBNull ? null : value;
    }

    private static NpgsqlCommand Create(AppDbContext db, string sql, object?[] args)
    {
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction
            ?? throw new InvalidOperationException("Ham SQL yalnız açık bir iş birimi (transaction) içinde çalıştırılır.");
        var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }
        return command;
    }

    public static Guid? GetGuidOrNull(this NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
}
