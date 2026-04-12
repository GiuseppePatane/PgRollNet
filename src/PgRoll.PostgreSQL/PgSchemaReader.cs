using Npgsql;
using PgRoll.Core.Schema;

namespace PgRoll.PostgreSQL;

public sealed class PgSchemaReader : IDisposable, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PgSchemaReader(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
        _ownsDataSource = false;
    }

    public PgSchemaReader(string connectionString)
        : this(NpgsqlDataSource.Create(connectionString))
    {
        _ownsDataSource = true;
    }

    /// <summary>
    /// Reads the full schema snapshot for the given PostgreSQL schema name (default: "public").
    /// </summary>
    public async Task<SchemaSnapshot> ReadSchemaAsync(string schemaName = "public", CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var tables = await ReadTablesAsync(conn, schemaName, ct);
        var indexNames = await ReadIndexNamesAsync(conn, schemaName, ct);

        return new SchemaSnapshot(tables, indexNames);
    }

    private static async Task<List<TableInfo>> ReadTablesAsync(
        NpgsqlConnection conn, string schemaName, CancellationToken ct)
    {
        const string tablesSql = """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = $1
              AND table_type = 'BASE TABLE'
            ORDER BY table_name
            """;

        var tableNames = new List<string>();
        await using (var cmd = new NpgsqlCommand(tablesSql, conn))
        {
            cmd.Parameters.AddWithValue(schemaName);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                tableNames.Add(reader.GetString(0));
        }

        var tables = new List<TableInfo>();
        foreach (var tableName in tableNames)
        {
            var columns = await ReadColumnsAsync(conn, schemaName, tableName, ct);
            var indexes = await ReadTableIndexNamesAsync(conn, schemaName, tableName, ct);
            var constraints = await ReadConstraintsAsync(conn, schemaName, tableName, ct);
            tables.Add(new TableInfo(schemaName, tableName, columns, indexes, constraints));
        }

        return tables;
    }

    private static async Task<List<ColumnInfo>> ReadColumnsAsync(
        NpgsqlConnection conn, string schemaName, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT
                c.column_name,
                pg_catalog.format_type(a.atttypid, a.atttypmod) AS data_type,
                c.is_nullable = 'YES',
                c.column_default,
                COALESCE(
                    (SELECT TRUE FROM information_schema.key_column_usage kcu
                     JOIN information_schema.table_constraints tc
                       ON tc.constraint_name = kcu.constraint_name
                      AND tc.table_schema = kcu.table_schema
                     WHERE tc.constraint_type = 'PRIMARY KEY'
                       AND kcu.table_schema = c.table_schema
                       AND kcu.table_name = c.table_name
                       AND kcu.column_name = c.column_name
                     LIMIT 1),
                    FALSE
                ) AS is_pk,
                c.ordinal_position
            FROM information_schema.columns c
            JOIN pg_catalog.pg_attribute a
              ON a.attrelid = (SELECT oid FROM pg_catalog.pg_class
                               WHERE relname = c.table_name
                                 AND relnamespace = (SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = c.table_schema))
             AND a.attname = c.column_name
            WHERE c.table_schema = $1
              AND c.table_name = $2
            ORDER BY c.ordinal_position
            """;

        var columns = new List<ColumnInfo>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schemaName);
        cmd.Parameters.AddWithValue(tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(new ColumnInfo(
                Name: reader.GetString(0),
                DataType: reader.GetString(1),
                IsNullable: reader.GetBoolean(2),
                Default: reader.IsDBNull(3) ? null : reader.GetString(3),
                IsPrimaryKey: reader.GetBoolean(4),
                OrdinalPosition: reader.GetInt32(5)
            ));
        }

        return columns;
    }

    private static async Task<List<string>> ReadTableIndexNamesAsync(
        NpgsqlConnection conn, string schemaName, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT i.relname
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = $1
              AND t.relname = $2
              AND NOT ix.indisprimary
            ORDER BY i.relname
            """;

        var indexes = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schemaName);
        cmd.Parameters.AddWithValue(tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            indexes.Add(reader.GetString(0));

        return indexes;
    }

    private static async Task<List<ConstraintInfo>> ReadConstraintsAsync(
        NpgsqlConnection conn, string schemaName, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT conname, contype::text, pg_get_constraintdef(c.oid)
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = $1
              AND t.relname = $2
              AND contype IN ('c', 'u', 'f')
            ORDER BY conname
            """;

        var constraints = new List<ConstraintInfo>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schemaName);
        cmd.Parameters.AddWithValue(tableName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            constraints.Add(new ConstraintInfo(
                Name: reader.GetString(0),
                Type: reader.GetString(1),
                Definition: reader.GetString(2)
            ));
        }

        return constraints;
    }

    private static async Task<List<string>> ReadIndexNamesAsync(
        NpgsqlConnection conn, string schemaName, CancellationToken ct)
    {
        const string sql = """
            SELECT i.relname
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = $1
              AND NOT ix.indisprimary
            ORDER BY i.relname
            """;

        var indexes = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schemaName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            indexes.Add(reader.GetString(0));

        return indexes;
    }

    public void Dispose()
    {
        if (_ownsDataSource)
            _dataSource.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
            await _dataSource.DisposeAsync();
    }
}
