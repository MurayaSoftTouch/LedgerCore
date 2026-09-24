using Npgsql;

namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

/// <summary>Raw SQL helpers for tests that deliberately bypass the application.</summary>
internal static class Sql
{
    public static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql, params object[] args)
    {
        await EnsureOpenAsync(connection);
        await using var command = Command(connection, sql, args);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, params object[] args)
    {
        await EnsureOpenAsync(connection);
        await using var command = Command(connection, sql, args);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs the SQL and returns the PostgreSQL error it raises; fails the test if none.</summary>
    public static async Task<PostgresException> FailsAsync(NpgsqlConnection connection, string sql, params object[] args) =>
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql, args));

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, object[] args)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return command;
    }

    private static async Task EnsureOpenAsync(NpgsqlConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
    }
}
