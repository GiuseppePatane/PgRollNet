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

        // Propagate USAGE grants from the base schema to the version schema so that any
        // role that can read the base schema can also read the version schema views.
        var usageGrantees = await ReadUsageGranteesAsync(conn, baseSchema, ct);
        foreach (var grantee in usageGrantees)
            await ExecAsync(conn, $"""GRANT USAGE ON SCHEMA "{versionSchema}" TO {grantee}""", ct);

        // DROP + CREATE instead of CREATE OR REPLACE: PostgreSQL disallows replacing a view when
        // column names change (e.g. multiple alter_column ops on the same table in one migration).
        await ExecAsync(conn, $"""DROP VIEW IF EXISTS "{versionSchema}"."{tableName}" CASCADE""", ct);
        await ExecAsync(conn, $"""
            CREATE VIEW "{versionSchema}"."{tableName}" AS
            SELECT {colList} FROM "{baseSchema}"."{tableName}"
            """, ct);

        foreach (var grantee in usageGrantees)
            await ExecAsync(conn, $"""GRANT SELECT ON "{versionSchema}"."{tableName}" TO {grantee}""", ct);
    }

    /// <summary>
    /// Returns the SQL-safe grantee identifiers (e.g. <c>"myrole"</c> or <c>PUBLIC</c>)
    /// that have USAGE privilege on <paramref name="schemaName"/>.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadUsageGranteesAsync(
        NpgsqlConnection conn, string schemaName, CancellationToken ct)
    {
        // aclexplode returns grantee oid = 0 for PUBLIC.
        // Use has_schema_privilege to check each role directly — avoids aclexplode()
        // which is a set-returning function that causes problems inside CASE expressions.
        // The UNION ALL checks the PUBLIC pseudo-role separately (oid 0 in pg ACL internals).
        const string sql = """
            SELECT '"' || replace(rolname, '"', '""') || '"' AS grantee
            FROM pg_catalog.pg_roles
            WHERE has_schema_privilege(oid, $1, 'USAGE')
            UNION ALL
            SELECT 'PUBLIC'
            WHERE has_schema_privilege('public', $1, 'USAGE')
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schemaName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await reader.ReadAsync(ct))
            if (!reader.IsDBNull(0))
                result.Add(reader.GetString(0));
        return result;
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
