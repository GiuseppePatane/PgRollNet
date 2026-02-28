using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PgRoll.Core.State;

namespace PgRoll.PostgreSQL;

public sealed class PgStateStore : IMigrationState
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PgStateStore> _logger;

    public PgStateStore(NpgsqlDataSource dataSource, ILogger<PgStateStore>? logger = null)
    {
        _dataSource = dataSource;
        _logger = logger ?? NullLogger<PgStateStore>.Instance;
    }

    /// <summary>
    /// Convenience constructor for creating a data source from a connection string.
    /// </summary>
    public PgStateStore(string connectionString, ILogger<PgStateStore>? logger = null)
        : this(NpgsqlDataSource.Create(connectionString), logger)
    {
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var sql = ReadEmbeddedSql("init.sql");
        _logger.LogInformation("Initializing pgroll state schema.");
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("pgroll state schema initialized.");
    }

    public async Task<MigrationRecord?> GetActiveMigrationAsync(string schema, CancellationToken ct = default)
    {
        const string sql = """
            SELECT schema, name, migration::text, created_at, updated_at, parent, done
            FROM pgroll.migrations
            WHERE schema = $1 AND done = FALSE
            ORDER BY created_at DESC
            LIMIT 1
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schema);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadRecord(reader);
    }

    public async Task RecordStartedAsync(MigrationRecord record, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO pgroll.migrations (schema, name, migration, parent, done)
            VALUES ($1, $2, $3::jsonb, $4, FALSE)
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(record.Schema);
        cmd.Parameters.AddWithValue(record.Name);
        cmd.Parameters.AddWithValue(record.MigrationJson is null ? DBNull.Value : record.MigrationJson);
        cmd.Parameters.AddWithValue(record.Parent is null ? DBNull.Value : record.Parent);

        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Recorded migration start: {Schema}/{Name}", record.Schema, record.Name);
    }

    public async Task RecordCompletedAsync(string schema, string name, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE pgroll.migrations
            SET done = TRUE, updated_at = NOW()
            WHERE schema = $1 AND name = $2
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schema);
        cmd.Parameters.AddWithValue(name);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
            throw new InvalidOperationException($"Migration '{schema}/{name}' not found.");

        _logger.LogInformation("Recorded migration complete: {Schema}/{Name}", schema, name);
    }

    public async Task DeleteRecordAsync(string schema, string name, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM pgroll.migrations WHERE schema = $1 AND name = $2";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schema);
        cmd.Parameters.AddWithValue(name);

        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Deleted migration record: {Schema}/{Name}", schema, name);
    }

    public async Task<IReadOnlyList<MigrationRecord>> GetHistoryAsync(string schema, CancellationToken ct = default)
    {
        const string sql = """
            SELECT schema, name, migration::text, created_at, updated_at, parent, done
            FROM pgroll.migrations
            WHERE schema = $1
            ORDER BY created_at ASC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schema);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var records = new List<MigrationRecord>();
        while (await reader.ReadAsync(ct))
            records.Add(ReadRecord(reader));

        return records;
    }

    private static MigrationRecord ReadRecord(NpgsqlDataReader reader) => new(
        Schema: reader.GetString(0),
        Name: reader.GetString(1),
        MigrationJson: reader.IsDBNull(2) ? null : reader.GetString(2),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(3),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(4),
        Parent: reader.IsDBNull(5) ? null : reader.GetString(5),
        Done: reader.GetBoolean(6)
    );

    private static string ReadEmbeddedSql(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"PgRoll.PostgreSQL.Sql.{fileName}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
