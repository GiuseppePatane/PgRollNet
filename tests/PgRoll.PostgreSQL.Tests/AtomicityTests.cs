using FluentAssertions;
using Npgsql;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Integration tests for the compensating-rollback atomicity in <see cref="PgMigrationExecutor.StartAsync"/>.
///
/// Strategy: build a multi-operation migration where validation passes on the initial snapshot
/// but a later operation fails at SQL execution time. The earlier (succeeded) operations must be
/// rolled back automatically, leaving the database in its pre-migration state.
///
/// The trigger: two consecutive <c>add_column</c> ops for the same column name on the same table.
/// Both pass <c>Validate()</c> because the snapshot is taken before any ops run (column absent).
/// Op1 executes and adds the column. Op2 then raises a PostgreSQL "column already exists" error.
/// The executor must roll back Op1 (DROP COLUMN) as part of its compensating rollback.
/// </summary>
[Collection("Postgres")]
public class AtomicityTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_atomicity_{Guid.NewGuid():N}";
    private PgMigrationExecutor _executor = null!;

    public async Task InitializeAsync()
    {
        _ds = await DatabaseFactory.CreateIsolatedDatabaseAsync(postgres.ConnectionString, _dbName);
        _executor = new PgMigrationExecutor(_ds);
        await _executor.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _ds.DisposeAsync();
        await DatabaseFactory.DropDatabaseAsync(postgres.ConnectionString, _dbName);
    }

    // ── DB helpers ────────────────────────────────────────────────────────────

    private async Task CreateTableAsync(string table)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"CREATE TABLE \"{table}\" (id serial PRIMARY KEY)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> ColumnExistsAsync(string table, string column)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name=$1 AND column_name=$2",
            conn);
        cmd.Parameters.AddWithValue(table);
        cmd.Parameters.AddWithValue(column);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_WhenSecondOpFails_RollsBackFirstOp()
    {
        // Both ops pass Validate() on the initial snapshot (column 'extra' not yet present).
        // Op1 adds the column; Op2 tries to add the same column → PostgreSQL error at runtime.
        // Atomicity: Op1 must be rolled back — column must NOT exist after the failed start.
        await CreateTableAsync("at_single");

        var migration = Migration.Deserialize("""
            {
              "name": "at_single_fail",
              "operations": [
                {"type":"add_column","table":"at_single","column":{"name":"extra","type":"text"}},
                {"type":"add_column","table":"at_single","column":{"name":"extra","type":"text"}}
              ]
            }
            """);

        Func<Task> act = () => _executor.StartAsync(migration);
        await act.Should().ThrowAsync<Exception>();

        (await ColumnExistsAsync("at_single", "extra"))
            .Should().BeFalse("Op1's add_column must have been rolled back");
    }

    [Fact]
    public async Task StartAsync_WhenThirdOpFails_RollsBackFirstAndSecondOps()
    {
        // Op1: add col_a to at_multi_a
        // Op2: add col_b to at_multi_b
        // Op3: add col_a to at_multi_a again → fails
        // Expected: Op1 and Op2 are both rolled back.
        await CreateTableAsync("at_multi_a");
        await CreateTableAsync("at_multi_b");

        var migration = Migration.Deserialize("""
            {
              "name": "at_multi_fail",
              "operations": [
                {"type":"add_column","table":"at_multi_a","column":{"name":"col_a","type":"text"}},
                {"type":"add_column","table":"at_multi_b","column":{"name":"col_b","type":"text"}},
                {"type":"add_column","table":"at_multi_a","column":{"name":"col_a","type":"text"}}
              ]
            }
            """);

        Func<Task> act = () => _executor.StartAsync(migration);
        await act.Should().ThrowAsync<Exception>();

        (await ColumnExistsAsync("at_multi_a", "col_a"))
            .Should().BeFalse("Op1 must have been rolled back");
        (await ColumnExistsAsync("at_multi_b", "col_b"))
            .Should().BeFalse("Op2 must have been rolled back");
    }

    [Fact]
    public async Task StartAsync_PartialFailure_NoMigrationRecordedInHistory()
    {
        // A migration that fails mid-start must not appear in the migration history.
        await CreateTableAsync("at_history");

        var migration = Migration.Deserialize("""
            {
              "name": "at_history_fail",
              "operations": [
                {"type":"add_column","table":"at_history","column":{"name":"extra","type":"text"}},
                {"type":"add_column","table":"at_history","column":{"name":"extra","type":"text"}}
              ]
            }
            """);

        try { await _executor.StartAsync(migration); }
        catch { /* expected */ }

        var history = await _executor.GetHistoryAsync();
        history.Should().NotContain(r => r.Name == "at_history_fail",
            "a failed migration must not be persisted to the state store");
    }

    [Fact]
    public async Task StartAsync_PartialFailure_SubsequentValidMigrationSucceeds()
    {
        // After a failed (rolled-back) start, the DB must be in a clean state so that
        // a subsequent valid migration on the same table can start without error.
        await CreateTableAsync("at_recovery");

        var failing = Migration.Deserialize("""
            {
              "name": "at_recovery_fail",
              "operations": [
                {"type":"add_column","table":"at_recovery","column":{"name":"extra","type":"text"}},
                {"type":"add_column","table":"at_recovery","column":{"name":"extra","type":"text"}}
              ]
            }
            """);

        try { await _executor.StartAsync(failing); } catch { /* expected */ }

        // No active migration should be stuck after the compensating rollback
        var status = await _executor.GetStatusAsync();
        status.Should().BeNull("no migration should be left active after a rolled-back start");

        // Valid migration on the same table must now succeed
        var valid = Migration.Deserialize("""
            {
              "name": "at_recovery_ok",
              "operations": [
                {"type":"add_column","table":"at_recovery","column":{"name":"extra","type":"text"}}
              ]
            }
            """);

        Func<Task> act = () => _executor.StartAsync(valid);
        await act.Should().NotThrowAsync();

        (await ColumnExistsAsync("at_recovery", "extra"))
            .Should().BeTrue("the valid migration's column must be present after a successful start");
    }

    [Fact]
    public async Task StartAsync_AllOpsSucceed_NothingRolledBack()
    {
        // Sanity check: when all ops succeed there is no unexpected rollback.
        await CreateTableAsync("at_success");

        var migration = Migration.Deserialize("""
            {
              "name": "at_success_ok",
              "operations": [
                {"type":"add_column","table":"at_success","column":{"name":"col1","type":"text"}},
                {"type":"add_column","table":"at_success","column":{"name":"col2","type":"integer"}}
              ]
            }
            """);

        Func<Task> act = () => _executor.StartAsync(migration);
        await act.Should().NotThrowAsync();

        (await ColumnExistsAsync("at_success", "col1")).Should().BeTrue();
        (await ColumnExistsAsync("at_success", "col2")).Should().BeTrue();
    }
}
