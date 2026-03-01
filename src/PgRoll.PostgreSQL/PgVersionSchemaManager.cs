using Npgsql;

namespace PgRoll.PostgreSQL;

public sealed class PgVersionSchemaManager
{
    public static string VersionSchemaName(string baseSchema, string migrationName) =>
        $"{baseSchema}_{migrationName}";

    /// <summary>
    /// Creates a versioned schema with a view over the given table exposing the specified column expressions.
    /// </summary>
    /// <param name="columnExpressions">
    /// SQL expressions for each column, e.g. ["\"id\"", "\"name\"", "\"_pgroll_new_email\" AS \"email\""].
    /// </param>
    public static async Task CreateVersionSchemaAsync(
        NpgsqlConnection conn,
        string baseSchema,
        string migrationName,
        string tableName,
        IReadOnlyList<string> columnExpressions,
        CancellationToken ct = default)
    {
        var versionSchema = VersionSchemaName(baseSchema, migrationName);
        var colList = string.Join(", ", columnExpressions);

        await ExecAsync(conn, $"""CREATE SCHEMA IF NOT EXISTS "{versionSchema}" """, ct);
        // DROP + CREATE instead of CREATE OR REPLACE: PostgreSQL disallows replacing a view when
        // column names change (e.g. multiple alter_column ops on the same table in one migration).
        await ExecAsync(conn, $"""DROP VIEW IF EXISTS "{versionSchema}"."{tableName}" CASCADE""", ct);
        await ExecAsync(conn, $"""
            CREATE VIEW "{versionSchema}"."{tableName}" AS
            SELECT {colList} FROM "{baseSchema}"."{tableName}"
            """, ct);
    }

    /// <summary>Drops the versioned schema and all its objects (CASCADE).</summary>
    public static async Task DropVersionSchemaAsync(
        NpgsqlConnection conn,
        string baseSchema,
        string migrationName,
        CancellationToken ct = default)
    {
        var versionSchema = VersionSchemaName(baseSchema, migrationName);
        await ExecAsync(conn, $"""DROP SCHEMA IF EXISTS "{versionSchema}" CASCADE""", ct);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
