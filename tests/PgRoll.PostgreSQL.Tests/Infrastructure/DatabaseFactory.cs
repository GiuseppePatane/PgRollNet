using Npgsql;

namespace PgRoll.PostgreSQL.Tests.Infrastructure;

/// <summary>
/// Helper for creating and dropping isolated test databases.
/// </summary>
public static class DatabaseFactory
{
    public static async Task<NpgsqlDataSource> CreateIsolatedDatabaseAsync(
        string adminConnectionString, string dbName, CancellationToken ct = default)
    {
        await using var adminConn = new NpgsqlConnection(adminConnectionString);
        await adminConn.OpenAsync(ct);

        // Drop first in case of leftover from a previous run
        await using (var dropCmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\"", adminConn))
            await dropCmd.ExecuteNonQueryAsync(ct);

        await using (var createCmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", adminConn))
            await createCmd.ExecuteNonQueryAsync(ct);

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = dbName };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public static async Task DropDatabaseAsync(
        string adminConnectionString, string dbName, CancellationToken ct = default)
    {
        await using var adminConn = new NpgsqlConnection(adminConnectionString);
        await adminConn.OpenAsync(ct);

        // Terminate existing connections
        await using (var killCmd = new NpgsqlCommand(
            $"""
            SELECT pg_terminate_backend(pg_stat_activity.pid)
            FROM pg_stat_activity
            WHERE pg_stat_activity.datname = '{dbName}'
              AND pid <> pg_backend_pid()
            """, adminConn))
            await killCmd.ExecuteNonQueryAsync(ct);

        await using (var dropCmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\"", adminConn))
            await dropCmd.ExecuteNonQueryAsync(ct);
    }
}
