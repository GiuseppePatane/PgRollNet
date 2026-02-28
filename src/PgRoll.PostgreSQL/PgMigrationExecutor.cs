using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PgRoll.Core.Errors;
using PgRoll.Core.Models;
using PgRoll.Core.Operations;
using PgRoll.Core.Schema;
using PgRoll.Core.State;

namespace PgRoll.PostgreSQL;

/// <summary>
/// Orchestrates the full migration lifecycle: Start → Complete / Rollback.
/// </summary>
public sealed class PgMigrationExecutor
{
    private const string SoftDeletePrefix = "_pgroll_del_";
    private const string TempColPrefix = "_pgroll_new_";
    private const string DupColPrefix = "_pgroll_dup_";

    private readonly NpgsqlDataSource _dataSource;
    private readonly PgStateStore _stateStore;
    private readonly PgSchemaReader _schemaReader;
    private readonly ILogger<PgMigrationExecutor> _logger;
    private readonly string _schemaName;

    public PgMigrationExecutor(
        NpgsqlDataSource dataSource,
        string schemaName = "public",
        ILogger<PgMigrationExecutor>? logger = null)
    {
        _dataSource = dataSource;
        _schemaName = schemaName;
        _logger = logger ?? NullLogger<PgMigrationExecutor>.Instance;
        _stateStore = new PgStateStore(dataSource);
        _schemaReader = new PgSchemaReader(dataSource);
    }

    public PgMigrationExecutor(
        string connectionString,
        string schemaName = "public",
        ILogger<PgMigrationExecutor>? logger = null)
        : this(NpgsqlDataSource.Create(connectionString), schemaName, logger)
    {
    }

    /// <summary>Initializes the pgroll state schema (idempotent).</summary>
    public Task InitializeAsync(CancellationToken ct = default) =>
        _stateStore.InitializeAsync(ct);

    /// <summary>
    /// Starts a migration: validates, executes Start phase of every operation, records state.
    /// </summary>
    public async Task<StartResult> StartAsync(Migration migration, CancellationToken ct = default)
    {
        if (migration.Operations.Count == 0)
            throw new EmptyMigrationError();

        var active = await _stateStore.GetActiveMigrationAsync(_schemaName, ct);
        if (active is not null)
            throw new MigrationAlreadyActiveError(active.Name);

        var snapshot = await _schemaReader.ReadSchemaAsync(_schemaName, ct);

        foreach (var op in migration.Operations)
        {
            var result = op.Validate(snapshot);
            if (!result.IsValid)
                throw new InvalidMigrationError(result.Error!);
        }

        _logger.LogInformation("Starting migration '{Name}'", migration.Name);

        foreach (var op in migration.Operations)
        {
            if (op.RequiresConcurrentConnection)
                await ExecuteStartConcurrently(op, migration, snapshot, ct);
            else
                await ExecuteStartTransactional(op, migration, snapshot, ct);
        }

        var migrationJson = migration.Serialize();
        var lastCompleted = (await _stateStore.GetHistoryAsync(_schemaName, ct))
            .LastOrDefault(r => r.Done)?.Name;

        var record = new MigrationRecord(
            Schema: _schemaName,
            Name: migration.Name,
            MigrationJson: migrationJson,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Parent: lastCompleted,
            Done: false
        );

        await _stateStore.RecordStartedAsync(record, ct);
        _logger.LogInformation("Migration '{Name}' started successfully.", migration.Name);
        return new StartResult(migration.Name, RequiresComplete: true);
    }

    /// <summary>Completes the active migration: executes Complete phase of every operation.</summary>
    public async Task CompleteAsync(CancellationToken ct = default)
    {
        var active = await _stateStore.GetActiveMigrationAsync(_schemaName, ct)
            ?? throw new NoActiveMigrationError();

        var migration = Migration.Deserialize(active.MigrationJson!);
        _logger.LogInformation("Completing migration '{Name}'", migration.Name);

        var snapshot = await _schemaReader.ReadSchemaAsync(_schemaName, ct);

        foreach (var op in migration.Operations)
        {
            if (op.RequiresConcurrentConnection)
                await ExecuteCompleteConcurrently(op, migration, snapshot, ct);
            else
                await ExecuteCompleteTransactional(op, migration, snapshot, ct);
        }

        await _stateStore.RecordCompletedAsync(_schemaName, active.Name, ct);
        _logger.LogInformation("Migration '{Name}' completed successfully.", migration.Name);
    }

    /// <summary>Rolls back the active migration: executes Rollback phase in reverse order.</summary>
    public async Task RollbackAsync(CancellationToken ct = default)
    {
        var active = await _stateStore.GetActiveMigrationAsync(_schemaName, ct)
            ?? throw new NoActiveMigrationError();

        var migration = Migration.Deserialize(active.MigrationJson!);
        _logger.LogInformation("Rolling back migration '{Name}'", migration.Name);

        var snapshot = await _schemaReader.ReadSchemaAsync(_schemaName, ct);

        foreach (var op in migration.Operations.Reverse())
        {
            if (op.RequiresConcurrentConnection)
                await ExecuteRollbackConcurrently(op, migration, snapshot, ct);
            else
                await ExecuteRollbackTransactional(op, migration, snapshot, ct);
        }

        await _stateStore.DeleteRecordAsync(_schemaName, active.Name, ct);
        _logger.LogInformation("Migration '{Name}' rolled back successfully.", migration.Name);
    }

    /// <summary>Shows the currently active migration, or null if none.</summary>
    public Task<MigrationRecord?> GetStatusAsync(CancellationToken ct = default) =>
        _stateStore.GetActiveMigrationAsync(_schemaName, ct);

    /// <summary>Returns the full migration history for the schema.</summary>
    public Task<IReadOnlyList<MigrationRecord>> GetHistoryAsync(CancellationToken ct = default) =>
        _stateStore.GetHistoryAsync(_schemaName, ct);

    // ── Start phase helpers ───────────────────────────────────────────────────

    private async Task ExecuteStartTransactional(
        IMigrationOperation op, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await DispatchStart(op, conn, migration, snapshot, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task ExecuteStartConcurrently(
        IMigrationOperation op, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await DispatchStart(op, conn, migration, snapshot, ct);
    }

    // ── Complete phase helpers ─────────────────────────────────────────────────

    private async Task ExecuteCompleteTransactional(
        IMigrationOperation op, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await DispatchComplete(op, conn, migration, snapshot, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task ExecuteCompleteConcurrently(
        IMigrationOperation op, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await DispatchComplete(op, conn, migration, snapshot, ct);
    }

    // ── Rollback phase helpers ─────────────────────────────────────────────────

    private async Task ExecuteRollbackTransactional(
        IMigrationOperation op, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await DispatchRollback(op, conn, migration, snapshot, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task ExecuteRollbackConcurrently(
        IMigrationOperation op, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await DispatchRollback(op, conn, migration, snapshot, ct);
    }

    // ── Operation dispatch ────────────────────────────────────────────────────

    private Task DispatchStart(IMigrationOperation op, NpgsqlConnection conn, Migration migration, SchemaSnapshot snapshot, CancellationToken ct) =>
        op switch
        {
            CreateTableOperation o => StartCreateTable(o, conn, ct),
            DropTableOperation o => StartDropTable(o, conn, ct),
            RenameTableOperation _ => Task.CompletedTask,
            AddColumnOperation o => StartAddColumn(o, conn, migration, snapshot, ct),
            DropColumnOperation _ => Task.CompletedTask,
            RenameColumnOperation _ => Task.CompletedTask,
            CreateIndexOperation o => StartCreateIndex(o, conn, ct),
            DropIndexOperation _ => Task.CompletedTask,
            AlterColumnOperation o => StartAlterColumn(o, conn, migration, snapshot, ct),
            CreateConstraintOperation o => StartCreateConstraint(o, conn, ct),
            DropConstraintOperation _ => Task.CompletedTask,
            RenameConstraintOperation _ => Task.CompletedTask,
            _ => throw new UnknownOperationTypeError(op.GetType().Name)
        };

    private Task DispatchComplete(IMigrationOperation op, NpgsqlConnection conn, Migration migration, SchemaSnapshot snapshot, CancellationToken ct) =>
        op switch
        {
            CreateTableOperation _ => Task.CompletedTask,
            DropTableOperation o => CompleteDropTable(o, conn, ct),
            RenameTableOperation o => CompleteRenameTable(o, conn, ct),
            AddColumnOperation o => CompleteAddColumn(o, conn, migration, ct),
            DropColumnOperation o => CompleteDropColumn(o, conn, ct),
            RenameColumnOperation o => CompleteRenameColumn(o, conn, ct),
            CreateIndexOperation _ => Task.CompletedTask,
            DropIndexOperation o => CompleteDropIndex(o, conn, ct),
            AlterColumnOperation o => CompleteAlterColumn(o, conn, migration, snapshot, ct),
            CreateConstraintOperation o => CompleteCreateConstraint(o, conn, ct),
            DropConstraintOperation o => CompleteDropConstraint(o, conn, ct),
            RenameConstraintOperation o => CompleteRenameConstraint(o, conn, ct),
            _ => throw new UnknownOperationTypeError(op.GetType().Name)
        };

    private Task DispatchRollback(IMigrationOperation op, NpgsqlConnection conn, Migration migration, SchemaSnapshot snapshot, CancellationToken ct) =>
        op switch
        {
            CreateTableOperation o => RollbackCreateTable(o, conn, ct),
            DropTableOperation o => RollbackDropTable(o, conn, ct),
            RenameTableOperation _ => Task.CompletedTask,
            AddColumnOperation o => RollbackAddColumn(o, conn, migration, ct),
            DropColumnOperation _ => Task.CompletedTask,
            RenameColumnOperation _ => Task.CompletedTask,
            CreateIndexOperation o => RollbackCreateIndex(o, conn, ct),
            DropIndexOperation _ => Task.CompletedTask,
            AlterColumnOperation o => RollbackAlterColumn(o, conn, migration, ct),
            CreateConstraintOperation o => RollbackCreateConstraint(o, conn, ct),
            DropConstraintOperation _ => Task.CompletedTask,
            RenameConstraintOperation _ => Task.CompletedTask,
            _ => throw new UnknownOperationTypeError(op.GetType().Name)
        };

    // ── create_table ──────────────────────────────────────────────────────────

    private async Task StartCreateTable(CreateTableOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE IF NOT EXISTS {Quote(_schemaName, op.Table)} (");

        var defs = op.Columns.Select(col =>
        {
            var def = $"{QuoteIdent(col.Name)} {col.Type}";
            if (!col.Nullable) def += " NOT NULL";
            if (col.Default is not null) def += $" DEFAULT {col.Default}";
            if (col.PrimaryKey) def += " PRIMARY KEY";
            return def;
        });

        sb.Append(string.Join(", ", defs));
        sb.Append(')');

        await ExecAsync(conn, sb.ToString(), ct);
        _logger.LogDebug("Created table {Table}", op.Table);
    }

    private Task RollbackCreateTable(CreateTableOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn, $"DROP TABLE IF EXISTS {Quote(_schemaName, op.Table)}", ct);

    // ── drop_table ────────────────────────────────────────────────────────────

    private Task StartDropTable(DropTableOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var softName = SoftDeletePrefix + op.Table;
        return ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} RENAME TO {QuoteIdent(softName)}", ct);
    }

    private Task CompleteDropTable(DropTableOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var softName = SoftDeletePrefix + op.Table;
        return ExecAsync(conn, $"DROP TABLE IF EXISTS {Quote(_schemaName, softName)}", ct);
    }

    private Task RollbackDropTable(DropTableOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var softName = SoftDeletePrefix + op.Table;
        return ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, softName)} RENAME TO {QuoteIdent(op.Table)}", ct);
    }

    // ── rename_table ──────────────────────────────────────────────────────────

    private Task CompleteRenameTable(RenameTableOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.From)} RENAME TO {QuoteIdent(op.To)}", ct);

    // ── add_column ────────────────────────────────────────────────────────────

    private async Task StartAddColumn(
        AddColumnOperation op, NpgsqlConnection conn, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        if (op.Up is null)
        {
            // Simple path: add column directly
            var col = op.Column;
            var colDef = new StringBuilder($"ADD COLUMN {QuoteIdent(col.Name)} {col.Type}");
            if (!col.Nullable) colDef.Append(" NOT NULL");
            if (col.Default is not null) colDef.Append($" DEFAULT {col.Default}");

            await ExecAsync(conn, $"ALTER TABLE {Quote(_schemaName, op.Table)} {colDef}", ct);
            return;
        }

        // Expand/contract path: use temp column
        var tempCol = TempColPrefix + op.Column.Name;
        var col2 = op.Column;

        // Always add as nullable initially
        await ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} ADD COLUMN {QuoteIdent(tempCol)} {col2.Type}", ct);

        var versionSchema = PgVersionSchemaManager.VersionSchemaName(_schemaName, migration.Name);
        var upExpr = op.Up;

        await PgTriggerManager.CreateTriggerAsync(conn, _schemaName, op.Table, op.Column.Name,
            tempCol, upExpr, versionSchema, ct);

        // Backfill uses its own connections (outside the main connection)
        await PgBackfillBatcher.BackfillAsync(_dataSource, _schemaName, op.Table, tempCol, upExpr, ct: ct);

        // Build version schema view
        var origCols = OriginalColumnExpressions(snapshot, op.Table);
        var colExprs = origCols.Append($"{QuoteIdent(tempCol)} AS {QuoteIdent(op.Column.Name)}").ToList();
        await PgVersionSchemaManager.CreateVersionSchemaAsync(conn, _schemaName, migration.Name, op.Table, colExprs, ct);
    }

    private async Task CompleteAddColumn(
        AddColumnOperation op, NpgsqlConnection conn, Migration migration, CancellationToken ct)
    {
        if (op.Up is null)
        {
            // No-op — column already added at Start
            return;
        }

        var tempCol = TempColPrefix + op.Column.Name;
        await PgTriggerManager.DropTriggerAsync(conn, _schemaName, op.Table, op.Column.Name, ct);
        await ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} RENAME COLUMN {QuoteIdent(tempCol)} TO {QuoteIdent(op.Column.Name)}", ct);

        if (!op.Column.Nullable)
            await ExecAsync(conn,
                $"ALTER TABLE {Quote(_schemaName, op.Table)} ALTER COLUMN {QuoteIdent(op.Column.Name)} SET NOT NULL", ct);

        await PgVersionSchemaManager.DropVersionSchemaAsync(conn, _schemaName, migration.Name, ct);
    }

    private async Task RollbackAddColumn(
        AddColumnOperation op, NpgsqlConnection conn, Migration migration, CancellationToken ct)
    {
        if (op.Up is null)
        {
            // Simple rollback
            await ExecAsync(conn,
                $"ALTER TABLE {Quote(_schemaName, op.Table)} DROP COLUMN IF EXISTS {QuoteIdent(op.Column.Name)}", ct);
            return;
        }

        var tempCol = TempColPrefix + op.Column.Name;
        await PgTriggerManager.DropTriggerAsync(conn, _schemaName, op.Table, op.Column.Name, ct);
        // Drop version schema first — it has a view that references the temp column
        await PgVersionSchemaManager.DropVersionSchemaAsync(conn, _schemaName, migration.Name, ct);
        await ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} DROP COLUMN IF EXISTS {QuoteIdent(tempCol)}", ct);
    }

    // ── drop_column ───────────────────────────────────────────────────────────

    private Task CompleteDropColumn(DropColumnOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} DROP COLUMN IF EXISTS {QuoteIdent(op.Column)}", ct);

    // ── rename_column ─────────────────────────────────────────────────────────

    private Task CompleteRenameColumn(RenameColumnOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} RENAME COLUMN {QuoteIdent(op.From)} TO {QuoteIdent(op.To)}", ct);

    // ── create_index ──────────────────────────────────────────────────────────

    private Task StartCreateIndex(CreateIndexOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var unique = op.Unique ? "UNIQUE " : "";
        var cols = string.Join(", ", op.Columns.Select(QuoteIdent));
        return ExecAsync(conn,
            $"CREATE {unique}INDEX CONCURRENTLY IF NOT EXISTS {QuoteIdent(op.Name)} ON {Quote(_schemaName, op.Table)} ({cols})", ct);
    }

    private Task RollbackCreateIndex(CreateIndexOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn, $"DROP INDEX CONCURRENTLY IF EXISTS {Quote(_schemaName, op.Name)}", ct);

    // ── drop_index ────────────────────────────────────────────────────────────

    private Task CompleteDropIndex(DropIndexOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn, $"DROP INDEX CONCURRENTLY IF EXISTS {Quote(_schemaName, op.Name)}", ct);

    // ── alter_column ──────────────────────────────────────────────────────────

    private async Task StartAlterColumn(
        AlterColumnOperation op, NpgsqlConnection conn, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        var dupCol = DupColPrefix + op.Column;
        var origTable = snapshot.GetTable(op.Table)!;
        var origColInfo = origTable.Columns.First(c => c.Name.Equals(op.Column, StringComparison.OrdinalIgnoreCase));
        var targetType = op.DataType ?? origColInfo.DataType;
        var upExpr = op.Up ?? QuoteIdent(op.Column);
        var versionSchema = PgVersionSchemaManager.VersionSchemaName(_schemaName, migration.Name);

        // Add temp duplicate column (always nullable initially)
        var addColSql = new StringBuilder($"ALTER TABLE {Quote(_schemaName, op.Table)} ADD COLUMN {QuoteIdent(dupCol)} {targetType}");
        if (op.Default is not null) addColSql.Append($" DEFAULT {op.Default}");
        await ExecAsync(conn, addColSql.ToString(), ct);

        // Create UP trigger
        await PgTriggerManager.CreateTriggerAsync(conn, _schemaName, op.Table, op.Column,
            dupCol, upExpr, versionSchema, ct);

        // Backfill (uses its own connections)
        await PgBackfillBatcher.BackfillAsync(_dataSource, _schemaName, op.Table, dupCol, upExpr, ct: ct);

        // Add unique index if requested
        if (op.Unique == true)
        {
            var uniqIdx = $"_pgroll_uniq_{op.Column}";
            await ExecAsync(conn,
                $"CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS {QuoteIdent(uniqIdx)} ON {Quote(_schemaName, op.Table)} ({QuoteIdent(dupCol)})", ct);
        }

        // Add check constraint (NOT VALID — validate later)
        if (op.Check is not null)
        {
            var checkName = $"_pgroll_check_{op.Column}";
            await ExecAsync(conn,
                $"ALTER TABLE {Quote(_schemaName, op.Table)} ADD CONSTRAINT {QuoteIdent(checkName)} CHECK ({op.Check}) NOT VALID", ct);
        }

        // Create version schema view: originals (excluding the altered column) + dup AS finalName
        var finalName = op.Name ?? op.Column;
        var origCols = OriginalColumnExpressions(snapshot, op.Table)
            .Where(e => !e.Equals(QuoteIdent(op.Column), StringComparison.Ordinal));
        var colExprs = origCols.Append($"{QuoteIdent(dupCol)} AS {QuoteIdent(finalName)}").ToList();
        await PgVersionSchemaManager.CreateVersionSchemaAsync(conn, _schemaName, migration.Name, op.Table, colExprs, ct);
    }

    private async Task CompleteAlterColumn(
        AlterColumnOperation op, NpgsqlConnection conn, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        var dupCol = DupColPrefix + op.Column;
        var finalName = op.Name ?? op.Column;

        await PgTriggerManager.DropTriggerAsync(conn, _schemaName, op.Table, op.Column, ct);

        // Drop version schema first — it has views that reference both the original and dup columns
        await PgVersionSchemaManager.DropVersionSchemaAsync(conn, _schemaName, migration.Name, ct);

        // Drop original column and promote dup
        await ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} DROP COLUMN {QuoteIdent(op.Column)}", ct);
        await ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} RENAME COLUMN {QuoteIdent(dupCol)} TO {QuoteIdent(finalName)}", ct);

        if (op.NotNull == true)
            await ExecAsync(conn,
                $"ALTER TABLE {Quote(_schemaName, op.Table)} ALTER COLUMN {QuoteIdent(finalName)} SET NOT NULL", ct);

        if (op.Unique == true)
        {
            var uniqIdx = $"_pgroll_uniq_{op.Column}";
            // Convert index to constraint
            await ExecAsync(conn,
                $"ALTER TABLE {Quote(_schemaName, op.Table)} ADD CONSTRAINT {QuoteIdent(uniqIdx)} UNIQUE USING INDEX {QuoteIdent(uniqIdx)}", ct);
        }

        if (op.Check is not null)
        {
            var checkName = $"_pgroll_check_{op.Column}";
            await ExecAsync(conn,
                $"ALTER TABLE {Quote(_schemaName, op.Table)} VALIDATE CONSTRAINT {QuoteIdent(checkName)}", ct);
        }
    }

    private async Task RollbackAlterColumn(
        AlterColumnOperation op, NpgsqlConnection conn, Migration migration, CancellationToken ct)
    {
        var dupCol = DupColPrefix + op.Column;

        await PgTriggerManager.DropTriggerAsync(conn, _schemaName, op.Table, op.Column, ct);

        // Drop version schema first — it has views that reference the dup column
        await PgVersionSchemaManager.DropVersionSchemaAsync(conn, _schemaName, migration.Name, ct);

        await ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} DROP COLUMN IF EXISTS {QuoteIdent(dupCol)}", ct);

        if (op.Unique == true)
        {
            var uniqIdx = $"_pgroll_uniq_{op.Column}";
            await ExecAsync(conn, $"DROP INDEX CONCURRENTLY IF EXISTS {Quote(_schemaName, uniqIdx)}", ct);
        }

        if (op.Check is not null)
        {
            var checkName = $"_pgroll_check_{op.Column}";
            await ExecAsync(conn,
                $"ALTER TABLE {Quote(_schemaName, op.Table)} DROP CONSTRAINT IF EXISTS {QuoteIdent(checkName)}", ct);
        }
    }

    // ── create_constraint ─────────────────────────────────────────────────────

    private Task StartCreateConstraint(CreateConstraintOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var definition = BuildConstraintDefinition(op);
        // Only CHECK and FOREIGN KEY support NOT VALID; UNIQUE does not
        var notValid = op.ConstraintType is "check" or "foreign_key" ? " NOT VALID" : "";
        return ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} ADD CONSTRAINT {QuoteIdent(op.Name)} {definition}{notValid}", ct);
    }

    private Task CompleteCreateConstraint(CreateConstraintOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        // Only CHECK and FOREIGN KEY need VALIDATE; UNIQUE is already fully enforced
        op.ConstraintType is "check" or "foreign_key"
            ? ExecAsync(conn,
                $"ALTER TABLE {Quote(_schemaName, op.Table)} VALIDATE CONSTRAINT {QuoteIdent(op.Name)}", ct)
            : Task.CompletedTask;

    private Task RollbackCreateConstraint(CreateConstraintOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} DROP CONSTRAINT IF EXISTS {QuoteIdent(op.Name)}", ct);

    private static string BuildConstraintDefinition(CreateConstraintOperation op) =>
        op.ConstraintType switch
        {
            "check" => $"CHECK ({op.Check})",
            "unique" => $"UNIQUE ({string.Join(", ", op.Columns!.Select(QuoteIdent))})",
            "foreign_key" =>
                $"FOREIGN KEY ({string.Join(", ", op.Columns!.Select(QuoteIdent))}) " +
                $"REFERENCES {QuoteIdent(op.ReferencesTable!)} ({string.Join(", ", op.ReferencesColumns!.Select(QuoteIdent))})",
            _ => throw new InvalidOperationException($"Unknown constraint type '{op.ConstraintType}'.")
        };

    // ── drop_constraint ───────────────────────────────────────────────────────

    private Task CompleteDropConstraint(DropConstraintOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} DROP CONSTRAINT {QuoteIdent(op.Name)}", ct);

    // ── rename_constraint ─────────────────────────────────────────────────────

    private Task CompleteRenameConstraint(RenameConstraintOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} RENAME CONSTRAINT {QuoteIdent(op.From)} TO {QuoteIdent(op.To)}", ct);

    // ── Version schema helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Returns quoted column expressions for all original (non-pgroll temp) columns of a table.
    /// </summary>
    private static IEnumerable<string> OriginalColumnExpressions(SchemaSnapshot snapshot, string tableName)
    {
        var table = snapshot.GetTable(tableName);
        if (table is null) return Enumerable.Empty<string>();

        return table.Columns
            .Where(c => !c.Name.StartsWith("_pgroll_", StringComparison.OrdinalIgnoreCase))
            .Select(c => QuoteIdent(c.Name));
    }

    // ── SQL helpers ───────────────────────────────────────────────────────────

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Quote(string schema, string name) =>
        $"{QuoteIdent(schema)}.{QuoteIdent(name)}";

    private static string QuoteIdent(string name) =>
        $"\"{name.Replace("\"", "\"\"")}\"";
}
