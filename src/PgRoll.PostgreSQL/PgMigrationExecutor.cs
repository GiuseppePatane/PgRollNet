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
public sealed class PgMigrationExecutor : IDisposable, IAsyncDisposable
{
    private const string SoftDeletePrefix = "_pgroll_del_";
    private const string TempColPrefix = "_pgroll_new_";
    private const string DupColPrefix = "_pgroll_dup_";

    private readonly NpgsqlDataSource _dataSource;
    private readonly PgStateStore _stateStore;
    private readonly PgSchemaReader _schemaReader;
    private readonly ILogger<PgMigrationExecutor> _logger;
    private readonly string _schemaName;
    private readonly string? _role;
    private readonly int _lockTimeoutMs;
    private readonly int _statementTimeoutMs;
    private readonly int _backfillBatchSize;
    private readonly int _backfillDelayMs;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly bool _ownsDataSource;
    private readonly bool _ownsLoggerFactory;

    /// <summary>
    /// Optional progress reporter for backfill operations.
    /// Set before calling <see cref="StartAsync"/> to receive per-batch updates.
    /// </summary>
    public IProgress<BackfillProgress>? BackfillProgress { get; set; }

    public PgMigrationExecutor(
        NpgsqlDataSource dataSource,
        string schemaName = "public",
        string pgrollSchema = "pgroll",
        string? role = null,
        int lockTimeoutMs = 500,
        int statementTimeoutMs = 0,
        int backfillBatchSize = 1000,
        int backfillDelayMs = 0,
        ILoggerFactory? loggerFactory = null,
        ILogger<PgMigrationExecutor>? logger = null)
        : this(
            dataSource,
            schemaName,
            pgrollSchema,
            role,
            lockTimeoutMs,
            statementTimeoutMs,
            backfillBatchSize,
            backfillDelayMs,
            loggerFactory,
            logger,
            ownsDataSource: false,
            ownsLoggerFactory: false)
    {
    }

    private PgMigrationExecutor(
        NpgsqlDataSource dataSource,
        string schemaName,
        string pgrollSchema,
        string? role,
        int lockTimeoutMs,
        int statementTimeoutMs,
        int backfillBatchSize,
        int backfillDelayMs,
        ILoggerFactory? loggerFactory,
        ILogger<PgMigrationExecutor>? logger,
        bool ownsDataSource,
        bool ownsLoggerFactory)
    {
        _dataSource = dataSource;
        _schemaName = schemaName;
        _role = role;
        _lockTimeoutMs = lockTimeoutMs;
        _statementTimeoutMs = statementTimeoutMs;
        _backfillBatchSize = backfillBatchSize;
        _backfillDelayMs = backfillDelayMs;
        _loggerFactory = loggerFactory;
        _ownsDataSource = ownsDataSource;
        _ownsLoggerFactory = ownsLoggerFactory;
        _logger = logger ?? loggerFactory?.CreateLogger<PgMigrationExecutor>() ?? NullLogger<PgMigrationExecutor>.Instance;
        _stateStore = new PgStateStore(dataSource, pgrollSchema, loggerFactory?.CreateLogger<PgStateStore>());
        _schemaReader = new PgSchemaReader(dataSource);
    }

    public PgMigrationExecutor(
        string connectionString,
        string schemaName = "public",
        string pgrollSchema = "pgroll",
        int lockTimeoutMs = 500,
        int statementTimeoutMs = 0,
        int backfillBatchSize = 1000,
        int backfillDelayMs = 0,
        string? role = null,
        ILoggerFactory? loggerFactory = null,
        ILogger<PgMigrationExecutor>? logger = null)
        : this(
            NpgsqlDataSource.Create(connectionString),
            schemaName,
            pgrollSchema,
            role,
            lockTimeoutMs,
            statementTimeoutMs,
            backfillBatchSize,
            backfillDelayMs,
            loggerFactory,
            logger,
            ownsDataSource: true,
            ownsLoggerFactory: false)
    {
    }

    public static PgMigrationExecutor CreateOwned(
        string connectionString,
        string schemaName = "public",
        string pgrollSchema = "pgroll",
        int lockTimeoutMs = 500,
        int statementTimeoutMs = 0,
        int backfillBatchSize = 1000,
        int backfillDelayMs = 0,
        string? role = null,
        ILoggerFactory? loggerFactory = null,
        ILogger<PgMigrationExecutor>? logger = null)
        => new(
            NpgsqlDataSource.Create(connectionString),
            schemaName,
            pgrollSchema,
            role,
            lockTimeoutMs,
            statementTimeoutMs,
            backfillBatchSize,
            backfillDelayMs,
            loggerFactory,
            logger,
            ownsDataSource: true,
            ownsLoggerFactory: loggerFactory is not null);

    private async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        if (_lockTimeoutMs > 0)
            await ExecAsync(conn, $"SET lock_timeout = '{_lockTimeoutMs}ms'", ct);
        if (_statementTimeoutMs > 0)
            await ExecAsync(conn, $"SET statement_timeout = '{_statementTimeoutMs}ms'", ct);
        if (_role is not null)
            await ExecAsync(conn, $"SET ROLE {QuoteIdent(_role)}", ct);
        return conn;
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

        // Acquire a per-schema advisory lock (non-blocking) to prevent two concurrent processes
        // from both seeing "no active migration" and both attempting to start one.
        await using var lockConn = await OpenConnectionAsync(ct);
        if (!await TryAcquireAdvisoryLockAsync(lockConn, _schemaName, ct))
            throw new MigrationLockError(_schemaName);

        try
        {

            var active = await _stateStore.GetActiveMigrationAsync(_schemaName, ct);
            if (active is not null)
                throw new MigrationAlreadyActiveError(active.Name);

            _logger.LogInformation("Starting migration '{Name}'", migration.Name);

            // Register the migration as active in the state store BEFORE executing any operations.
            // This is write-ahead: if the process dies mid-execution, `pgroll rollback` can find the
            // record (done=false) and clean up all artifacts, even without an in-process catch block.
            var migrationJson = migration.Serialize();
            var migrationChecksum = MigrationChecksum.ComputeSha256(migrationJson);
            var lastCompleted = (await _stateStore.GetHistoryAsync(_schemaName, ct))
                .LastOrDefault(r => r.Done)?.Name;

            var record = new MigrationRecord(
                Schema: _schemaName,
                Name: migration.Name,
                MigrationJson: migrationJson,
                MigrationChecksum: migrationChecksum,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                Parent: lastCompleted,
                Done: false
            );

            await _stateStore.RecordStartedAsync(record, ct);

            // Track which operations have been started so we can roll them back if a later one fails.
            // We validate each operation against a fresh snapshot read just before executing it —
            // this lets multi-op migrations work correctly when one op creates a table that a later
            // op in the same migration references (e.g. create_table + create_constraint in initial_create).
            var started = new List<IMigrationOperation>();
            SchemaSnapshot? lastSnapshot = null;
            try
            {
                foreach (var op in migration.Operations)
                {
                    // Read a fresh snapshot so this op sees objects created by prior ops.
                    var snapshot = await _schemaReader.ReadSchemaAsync(_schemaName, ct);
                    lastSnapshot = snapshot;

                    var result = op.Validate(snapshot);
                    if (!result.IsValid)
                        throw new InvalidMigrationError(result.Error!);

                    if (op.RequiresConcurrentConnection)
                        await ExecuteStartConcurrently(op, migration, snapshot, ct);
                    else
                        await ExecuteStartTransactional(op, migration, snapshot, ct);

                    started.Add(op);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Migration '{Name}' failed during Start after {Count} operation(s). Rolling back.",
                    migration.Name, started.Count);

                // Use the last known snapshot for rollback (best-effort — avoids another read on error path)
                var rollbackSnapshot = lastSnapshot ?? await _schemaReader.ReadSchemaAsync(_schemaName, ct);

                foreach (var completedOp in Enumerable.Reverse(started))
                {
                    try
                    {
                        if (completedOp.RequiresConcurrentConnection)
                            await ExecuteRollbackConcurrently(completedOp, migration, rollbackSnapshot, ct);
                        else
                            await ExecuteRollbackTransactional(completedOp, migration, rollbackSnapshot, ct);
                    }
                    catch (Exception rollbackEx)
                    {
                        _logger.LogError(rollbackEx,
                            "Failed to rollback operation '{Op}' during Start cleanup.",
                            completedOp.GetType().Name);
                    }
                }

                // Remove the state store record so the migration can be retried cleanly.
                // If this delete fails we log but still re-throw the original error —
                // the user can use `pgroll rollback` to clean up the remaining record.
                try
                { await _stateStore.DeleteRecordAsync(_schemaName, migration.Name, ct); }
                catch (Exception deleteEx)
                {
                    _logger.LogError(deleteEx,
                        "Failed to delete state record for '{Name}' after rollback. Run `pgroll rollback` to clean up.",
                        migration.Name);
                }

                throw;
            }

            _logger.LogInformation("Migration '{Name}' started successfully.", migration.Name);
            return new StartResult(migration.Name, RequiresComplete: true);

        } // end advisory-lock try
        finally
        {
            await ReleaseAdvisoryLockAsync(lockConn, _schemaName, ct);
        }
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

    /// <summary>
    /// Records a baseline migration: inserts a completed record with no operations,
    /// anchoring the history at the current database state.
    /// </summary>
    public async Task CreateBaselineAsync(string migrationName, CancellationToken ct = default)
    {
        await _stateStore.InitializeAsync(ct);

        var active = await _stateStore.GetActiveMigrationAsync(_schemaName, ct);
        if (active is not null)
            throw new PgRollException($"Cannot create baseline: migration '{active.Name}' is still active.");

        var history = await _stateStore.GetHistoryAsync(_schemaName, ct);
        var parent = history.LastOrDefault(r => r.Done)?.Name;

        var emptyMigration = new Migration { Name = migrationName, Operations = [] };
        var emptyMigrationJson = emptyMigration.Serialize();
        var record = new MigrationRecord(
            Schema: _schemaName,
            Name: migrationName,
            MigrationJson: emptyMigrationJson,
            MigrationChecksum: MigrationChecksum.ComputeSha256(emptyMigrationJson),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Parent: parent,
            Done: false);

        await _stateStore.RecordStartedAsync(record, ct);
        await _stateStore.RecordCompletedAsync(_schemaName, migrationName, ct);
        _logger.LogInformation("Baseline migration '{Name}' created for schema '{Schema}'.", migrationName, _schemaName);
    }

    public void Dispose()
    {
        if (_ownsDataSource)
            _dataSource.Dispose();
        if (_ownsLoggerFactory)
            _loggerFactory?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
            await _dataSource.DisposeAsync();
        if (_ownsLoggerFactory)
            _loggerFactory?.Dispose();
    }

    // ── Start phase helpers ───────────────────────────────────────────────────

    private async Task ExecuteStartTransactional(
        IMigrationOperation op, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
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
        await using var conn = await OpenConnectionAsync(ct);
        await DispatchStart(op, conn, migration, snapshot, ct);
    }

    // ── Complete phase helpers ─────────────────────────────────────────────────

    private async Task ExecuteCompleteTransactional(
        IMigrationOperation op, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
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
        await using var conn = await OpenConnectionAsync(ct);
        await DispatchComplete(op, conn, migration, snapshot, ct);
    }

    // ── Rollback phase helpers ─────────────────────────────────────────────────

    private async Task ExecuteRollbackTransactional(
        IMigrationOperation op, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct);
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
        await using var conn = await OpenConnectionAsync(ct);
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
            DropColumnOperation o => StartDropColumn(o, conn, ct),
            RenameColumnOperation o => StartRenameColumn(o, conn, ct),
            CreateIndexOperation o => StartCreateIndex(o, conn, ct),
            DropIndexOperation o => StartDropIndex(o, conn, ct),
            AlterColumnOperation o => StartAlterColumn(o, conn, migration, snapshot, ct),
            CreateConstraintOperation o => StartCreateConstraint(o, conn, ct),
            DropConstraintOperation o => StartDropConstraint(o, conn, ct),
            RenameConstraintOperation _ => Task.CompletedTask,
            RawSqlOperation o => StartRawSql(o, conn, ct),
            SetNotNullOperation o => StartSetNotNull(o, conn, ct),
            DropNotNullOperation o => StartDropNotNull(o, conn, ct),
            SetDefaultOperation o => StartSetDefault(o, conn, ct),
            DropDefaultOperation o => StartDropDefault(o, conn, ct),
            CreateSchemaOperation o => StartCreateSchema(o, conn, ct),
            DropSchemaOperation o => StartDropSchema(o, conn, ct),
            CreateEnumOperation o => StartCreateEnum(o, conn, ct),
            DropEnumOperation o => StartDropEnum(o, conn, ct),
            CreateViewOperation o => StartCreateView(o, conn, ct),
            DropViewOperation o => StartDropView(o, conn, ct),
            _ => throw new UnknownOperationTypeError(op.GetType().Name)
        };

    private Task DispatchComplete(IMigrationOperation op, NpgsqlConnection conn, Migration migration, SchemaSnapshot snapshot, CancellationToken ct) =>
        op switch
        {
            CreateTableOperation _ => Task.CompletedTask,
            DropTableOperation o => CompleteDropTable(o, conn, ct),
            RenameTableOperation o => CompleteRenameTable(o, conn, ct),
            AddColumnOperation o => CompleteAddColumn(o, conn, migration, ct),
            DropColumnOperation _ => Task.CompletedTask,   // dropped in Start
            RenameColumnOperation _ => Task.CompletedTask, // renamed in Start
            CreateIndexOperation _ => Task.CompletedTask,
            DropIndexOperation _ => Task.CompletedTask,   // dropped in Start
            AlterColumnOperation o => CompleteAlterColumn(o, conn, migration, snapshot, ct),
            CreateConstraintOperation o => CompleteCreateConstraint(o, conn, ct),
            DropConstraintOperation _ => Task.CompletedTask,   // dropped in Start
            RenameConstraintOperation o => CompleteRenameConstraint(o, conn, ct),
            RawSqlOperation _ => Task.CompletedTask,
            SetNotNullOperation _ => Task.CompletedTask,
            DropNotNullOperation _ => Task.CompletedTask,
            SetDefaultOperation _ => Task.CompletedTask,
            DropDefaultOperation _ => Task.CompletedTask,
            CreateSchemaOperation _ => Task.CompletedTask,
            DropSchemaOperation o => CompleteDropSchema(o, conn, ct),
            CreateEnumOperation _ => Task.CompletedTask,
            DropEnumOperation o => CompleteDropEnum(o, conn, ct),
            CreateViewOperation _ => Task.CompletedTask,
            DropViewOperation o => CompleteDropView(o, conn, ct),
            _ => throw new UnknownOperationTypeError(op.GetType().Name)
        };

    private Task DispatchRollback(IMigrationOperation op, NpgsqlConnection conn, Migration migration, SchemaSnapshot snapshot, CancellationToken ct) =>
        op switch
        {
            CreateTableOperation o => RollbackCreateTable(o, conn, ct),
            DropTableOperation o => RollbackDropTable(o, conn, ct),
            RenameTableOperation _ => Task.CompletedTask,
            AddColumnOperation o => RollbackAddColumn(o, conn, migration, ct),
            DropColumnOperation _ => Task.CompletedTask,           // cannot restore dropped column
            RenameColumnOperation o => RollbackRenameColumn(o, conn, ct),
            CreateIndexOperation o => RollbackCreateIndex(o, conn, ct),
            DropIndexOperation _ => Task.CompletedTask,            // cannot restore dropped index
            AlterColumnOperation o => RollbackAlterColumn(o, conn, migration, ct),
            CreateConstraintOperation o => RollbackCreateConstraint(o, conn, ct),
            DropConstraintOperation _ => Task.CompletedTask,
            RenameConstraintOperation _ => Task.CompletedTask,
            RawSqlOperation o => RollbackRawSql(o, conn, ct),
            SetNotNullOperation o => RollbackSetNotNull(o, conn, ct),
            DropNotNullOperation o => RollbackDropNotNull(o, conn, ct),
            SetDefaultOperation o => RollbackSetDefault(o, conn, ct),
            DropDefaultOperation _ => Task.CompletedTask,
            CreateSchemaOperation o => RollbackCreateSchema(o, conn, ct),
            DropSchemaOperation o => RollbackDropSchema(o, conn, ct),
            CreateEnumOperation o => RollbackCreateEnum(o, conn, ct),
            DropEnumOperation o => RollbackDropEnum(o, conn, ct),
            CreateViewOperation o => RollbackCreateView(o, conn, ct),
            DropViewOperation o => RollbackDropView(o, conn, ct),
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
            if (!col.Nullable)
                def += " NOT NULL";
            if (col.Default is not null)
                def += $" DEFAULT {col.Default}";
            if (col.PrimaryKey)
                def += " PRIMARY KEY";
            if (col.Unique)
                def += " UNIQUE";
            if (col.References is not null)
                def += $" REFERENCES {col.References}";
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
            if (!col.Nullable)
                colDef.Append(" NOT NULL");
            if (col.Default is not null)
                colDef.Append($" DEFAULT {col.Default}");

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
        _logger.LogInformation(
            "Backfill start for {Schema}.{Table} with batch size {BatchSize} and delay {DelayMs}ms",
            _schemaName,
            op.Table,
            _backfillBatchSize,
            _backfillDelayMs);
        await PgBackfillBatcher.BackfillAsync(
            _dataSource,
            _schemaName,
            op.Table,
            tempCol,
            upExpr,
            batchSize: _backfillBatchSize,
            batchDelay: TimeSpan.FromMilliseconds(_backfillDelayMs),
            statementTimeoutMs: _statementTimeoutMs,
            progress: BackfillProgress,
            ct: ct);

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

    private Task StartDropColumn(DropColumnOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} DROP COLUMN IF EXISTS {QuoteIdent(op.Column)}", ct);

    // ── rename_column ─────────────────────────────────────────────────────────

    private Task StartRenameColumn(RenameColumnOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} RENAME COLUMN {QuoteIdent(op.From)} TO {QuoteIdent(op.To)}", ct);

    private Task RollbackRenameColumn(RenameColumnOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} RENAME COLUMN {QuoteIdent(op.To)} TO {QuoteIdent(op.From)}", ct);

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

    private Task StartDropIndex(DropIndexOperation op, NpgsqlConnection conn, CancellationToken ct) =>
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
        if (op.Default is not null)
            addColSql.Append($" DEFAULT {op.Default}");
        await ExecAsync(conn, addColSql.ToString(), ct);

        // All subsequent steps are non-transactional (requires_concurrent_connection).
        // If any step fails we must clean up what we've already done ourselves.
        try
        {
            // Create UP (and optional DOWN) trigger.
            // tempColumnAlias exposes the dup column under its final public name so that Down
            // expressions such as "SUBSTR(full_name, 5)" work naturally.
            var finalName = op.Name ?? op.Column;
            var tempAlias = finalName != op.Column ? finalName : null;
            await PgTriggerManager.CreateTriggerAsync(conn, _schemaName, op.Table, op.Column,
                dupCol, upExpr, versionSchema, ct,
                downExpression: op.Down, tempColumnAlias: tempAlias);

            // Backfill (uses its own connections)
            _logger.LogInformation(
                "Backfill start for {Schema}.{Table} with batch size {BatchSize} and delay {DelayMs}ms",
                _schemaName,
                op.Table,
                _backfillBatchSize,
                _backfillDelayMs);
            await PgBackfillBatcher.BackfillAsync(
                _dataSource,
                _schemaName,
                op.Table,
                dupCol,
                upExpr,
                batchSize: _backfillBatchSize,
                batchDelay: TimeSpan.FromMilliseconds(_backfillDelayMs),
                statementTimeoutMs: _statementTimeoutMs,
                progress: BackfillProgress,
                ct: ct);

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
            var origCols = OriginalColumnExpressions(snapshot, op.Table)
                .Where(e => !e.Equals(QuoteIdent(op.Column), StringComparison.Ordinal));
            var colExprs = origCols.Append($"{QuoteIdent(dupCol)} AS {QuoteIdent(op.Name ?? op.Column)}").ToList();
            await PgVersionSchemaManager.CreateVersionSchemaAsync(conn, _schemaName, migration.Name, op.Table, colExprs, ct);
        }
        catch
        {
            // Self-cleanup: undo everything StartAlterColumn has done so far so that retrying
            // start (or running pgroll-net rollback) works without manual intervention.
            await PgTriggerManager.DropTriggerAsync(conn, _schemaName, op.Table, op.Column, ct);
            await PgVersionSchemaManager.DropVersionSchemaAsync(conn, _schemaName, migration.Name, ct);
            await ExecAsync(conn,
                $"ALTER TABLE {Quote(_schemaName, op.Table)} DROP COLUMN IF EXISTS {QuoteIdent(dupCol)}", ct);
            throw;
        }
    }

    private async Task CompleteAlterColumn(
        AlterColumnOperation op, NpgsqlConnection conn, Migration migration, SchemaSnapshot snapshot, CancellationToken ct)
    {
        var dupCol = DupColPrefix + op.Column;
        var finalName = op.Name ?? op.Column;

        await PgTriggerManager.DropTriggerAsync(conn, _schemaName, op.Table, op.Column, ct);

        // Drop version schema first — it has views that reference both the original and dup columns
        await PgVersionSchemaManager.DropVersionSchemaAsync(conn, _schemaName, migration.Name, ct);

        // If the original column is part of the PK, capture PK info before dropping.
        // DROP COLUMN CASCADE will remove the PK constraint, so we recreate it afterwards.
        var tableInfo = snapshot.GetTable(op.Table);
        var origCol = tableInfo?.Columns.FirstOrDefault(c =>
            c.Name.Equals(op.Column, StringComparison.OrdinalIgnoreCase));
        PkInfo? pkInfo = null;
        if (origCol?.IsPrimaryKey == true)
            pkInfo = await ReadPrimaryKeyAsync(conn, _schemaName, op.Table, ct);

        // Drop original column and promote dup.
        // Use CASCADE so that FK constraints from other tables that reference this column are also dropped.
        await ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} DROP COLUMN {QuoteIdent(op.Column)} CASCADE", ct);
        await ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} RENAME COLUMN {QuoteIdent(dupCol)} TO {QuoteIdent(finalName)}", ct);

        // Recreate the PK if it was dropped via CASCADE (column was part of the primary key).
        if (pkInfo is not null)
        {
            var pkCols = pkInfo.Columns
                .Select(c => c.Equals(op.Column, StringComparison.OrdinalIgnoreCase) ? finalName : c)
                .Select(QuoteIdent);
            await ExecAsync(conn,
                $"ALTER TABLE {Quote(_schemaName, op.Table)} ADD CONSTRAINT {QuoteIdent(pkInfo.Name)} PRIMARY KEY ({string.Join(", ", pkCols)})", ct);
        }

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
        // Only CHECK and FOREIGN KEY need VALIDATE; UNIQUE is already fully enforced.
        // Use exception handling to gracefully skip if the constraint was cascade-dropped
        // by a concurrent alter_column Complete in the same migration.
        op.ConstraintType is "check" or "foreign_key"
            ? ExecAsync(conn, $"""
                DO $$ BEGIN
                  ALTER TABLE {Quote(_schemaName, op.Table)} VALIDATE CONSTRAINT {QuoteIdent(op.Name)};
                EXCEPTION WHEN undefined_object THEN NULL;
                END $$;
                """, ct)
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

    private Task StartDropConstraint(DropConstraintOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} DROP CONSTRAINT IF EXISTS {QuoteIdent(op.Name)}", ct);

    // ── rename_constraint ─────────────────────────────────────────────────────

    private Task CompleteRenameConstraint(RenameConstraintOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} RENAME CONSTRAINT {QuoteIdent(op.From)} TO {QuoteIdent(op.To)}", ct);

    // ── raw_sql ───────────────────────────────────────────────────────────────

    private Task StartRawSql(RawSqlOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn, op.Sql, ct);

    private Task RollbackRawSql(RawSqlOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        op.RollbackSql is not null ? ExecAsync(conn, op.RollbackSql, ct) : Task.CompletedTask;

    // ── set_not_null ──────────────────────────────────────────────────────────

    private Task StartSetNotNull(SetNotNullOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} ALTER COLUMN {QuoteIdent(op.Column)} SET NOT NULL", ct);

    private Task RollbackSetNotNull(SetNotNullOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} ALTER COLUMN {QuoteIdent(op.Column)} DROP NOT NULL", ct);

    // ── drop_not_null ─────────────────────────────────────────────────────────

    private Task StartDropNotNull(DropNotNullOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} ALTER COLUMN {QuoteIdent(op.Column)} DROP NOT NULL", ct);

    private Task RollbackDropNotNull(DropNotNullOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} ALTER COLUMN {QuoteIdent(op.Column)} SET NOT NULL", ct);

    // ── set_default ───────────────────────────────────────────────────────────

    private Task StartSetDefault(SetDefaultOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} ALTER COLUMN {QuoteIdent(op.Column)} SET DEFAULT {op.Value}", ct);

    private Task RollbackSetDefault(SetDefaultOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} ALTER COLUMN {QuoteIdent(op.Column)} DROP DEFAULT", ct);

    // ── drop_default ──────────────────────────────────────────────────────────

    private Task StartDropDefault(DropDefaultOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"ALTER TABLE {Quote(_schemaName, op.Table)} ALTER COLUMN {QuoteIdent(op.Column)} DROP DEFAULT", ct);

    // ── create_schema ─────────────────────────────────────────────────────────

    private Task StartCreateSchema(CreateSchemaOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn, $"CREATE SCHEMA IF NOT EXISTS {QuoteIdent(op.Schema)}", ct);

    private Task RollbackCreateSchema(CreateSchemaOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn, $"DROP SCHEMA IF EXISTS {QuoteIdent(op.Schema)} CASCADE", ct);

    // ── drop_schema ───────────────────────────────────────────────────────────

    private Task StartDropSchema(DropSchemaOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var softName = SoftDeletePrefix + op.Schema;
        return ExecAsync(conn,
            $"ALTER SCHEMA {QuoteIdent(op.Schema)} RENAME TO {QuoteIdent(softName)}", ct);
    }

    private Task CompleteDropSchema(DropSchemaOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var softName = SoftDeletePrefix + op.Schema;
        return ExecAsync(conn, $"DROP SCHEMA IF EXISTS {QuoteIdent(softName)} CASCADE", ct);
    }

    private Task RollbackDropSchema(DropSchemaOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var softName = SoftDeletePrefix + op.Schema;
        return ExecAsync(conn,
            $"ALTER SCHEMA {QuoteIdent(softName)} RENAME TO {QuoteIdent(op.Schema)}", ct);
    }

    // ── create_enum ───────────────────────────────────────────────────────────

    private Task StartCreateEnum(CreateEnumOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var values = string.Join(", ", op.Values.Select(v => $"'{v.Replace("'", "''")}'"));
        return ExecAsync(conn,
            $"CREATE TYPE {Quote(_schemaName, op.Name)} AS ENUM ({values})", ct);
    }

    private Task RollbackCreateEnum(CreateEnumOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn, $"DROP TYPE IF EXISTS {Quote(_schemaName, op.Name)}", ct);

    // ── drop_enum ─────────────────────────────────────────────────────────────

    private Task StartDropEnum(DropEnumOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var softName = SoftDeletePrefix + op.Name;
        return ExecAsync(conn,
            $"ALTER TYPE {Quote(_schemaName, op.Name)} RENAME TO {QuoteIdent(softName)}", ct);
    }

    private Task CompleteDropEnum(DropEnumOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var softName = SoftDeletePrefix + op.Name;
        return ExecAsync(conn, $"DROP TYPE IF EXISTS {Quote(_schemaName, softName)}", ct);
    }

    private Task RollbackDropEnum(DropEnumOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var softName = SoftDeletePrefix + op.Name;
        return ExecAsync(conn,
            $"ALTER TYPE {Quote(_schemaName, softName)} RENAME TO {QuoteIdent(op.Name)}", ct);
    }

    // ── create_view ───────────────────────────────────────────────────────────

    private Task StartCreateView(CreateViewOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn,
            $"CREATE VIEW {Quote(_schemaName, op.Name)} AS {op.Definition}", ct);

    private Task RollbackCreateView(CreateViewOperation op, NpgsqlConnection conn, CancellationToken ct) =>
        ExecAsync(conn, $"DROP VIEW IF EXISTS {Quote(_schemaName, op.Name)}", ct);

    // ── drop_view ─────────────────────────────────────────────────────────────

    private Task StartDropView(DropViewOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var softName = SoftDeletePrefix + op.Name;
        return ExecAsync(conn,
            $"ALTER VIEW {Quote(_schemaName, op.Name)} RENAME TO {QuoteIdent(softName)}", ct);
    }

    private Task CompleteDropView(DropViewOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var softName = SoftDeletePrefix + op.Name;
        return ExecAsync(conn, $"DROP VIEW IF EXISTS {Quote(_schemaName, softName)}", ct);
    }

    private Task RollbackDropView(DropViewOperation op, NpgsqlConnection conn, CancellationToken ct)
    {
        var softName = SoftDeletePrefix + op.Name;
        return ExecAsync(conn,
            $"ALTER VIEW {Quote(_schemaName, softName)} RENAME TO {QuoteIdent(op.Name)}", ct);
    }

    // ── Primary key helpers ───────────────────────────────────────────────────

    private sealed record PkInfo(string Name, IReadOnlyList<string> Columns);

    private static async Task<PkInfo?> ReadPrimaryKeyAsync(
        NpgsqlConnection conn, string schemaName, string tableName, CancellationToken ct)
    {
        const string sql = """
            SELECT c.conname, a.attname
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(c.conkey)
            WHERE n.nspname = $1 AND t.relname = $2 AND c.contype = 'p'
            ORDER BY a.attnum
            """;

        string? pkName = null;
        var cols = new List<string>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schemaName);
        cmd.Parameters.AddWithValue(tableName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            pkName = reader.GetString(0);
            cols.Add(reader.GetString(1));
        }
        return pkName is null ? null : new PkInfo(pkName, cols);
    }

    // ── Version schema helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Returns quoted column expressions for all original (non-pgroll temp) columns of a table.
    /// </summary>
    private static IEnumerable<string> OriginalColumnExpressions(SchemaSnapshot snapshot, string tableName)
    {
        var table = snapshot.GetTable(tableName);
        if (table is null)
            return Enumerable.Empty<string>();

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

    // ── Advisory lock helpers ─────────────────────────────────────────────────
    // Key: (classid=hashtext('pgroll'), objid=hashtext(schemaName))
    // Ensures concurrent StartAsync calls on the same schema fail fast instead of
    // racing to insert a duplicate active-migration record.

    private static async Task<bool> TryAcquireAdvisoryLockAsync(
        NpgsqlConnection conn, string schema, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(hashtext('pgroll'), hashtext($1))", conn);
        cmd.Parameters.AddWithValue(schema);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ReleaseAdvisoryLockAsync(
        NpgsqlConnection conn, string schema, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_advisory_unlock(hashtext('pgroll'), hashtext($1))", conn);
        cmd.Parameters.AddWithValue(schema);
        await cmd.ExecuteScalarAsync(ct);
    }

    private static string Quote(string schema, string name) =>
        $"{QuoteIdent(schema)}.{QuoteIdent(name)}";

    private static string QuoteIdent(string name) =>
        $"\"{name.Replace("\"", "\"\"")}\"";
}
