using FluentAssertions;
using Npgsql;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Integration tests for <see cref="PgBackfillBatcher"/> progress reporting
/// and <see cref="PgMigrationExecutor.BackfillProgress"/> propagation.
/// </summary>
[Collection("Postgres")]
public class BackfillProgressTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_bf_{Guid.NewGuid():N}";
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

    // ── BackfillBatcher direct tests ──────────────────────────────────────────

    [Fact]
    public async Task BackfillBatcher_ReportsProgressPerBatch()
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var create = new NpgsqlCommand(
            "CREATE TABLE bf_direct (id serial, old_val text, new_val text)", conn);
        await create.ExecuteNonQueryAsync();

        // Insert 25 rows
        await using var insert = new NpgsqlCommand(
            "INSERT INTO bf_direct (old_val) SELECT 'val_' || g FROM generate_series(1,25) g", conn);
        await insert.ExecuteNonQueryAsync();

        var reports = new List<BackfillProgress>();
        var progress = new Progress<BackfillProgress>(p => reports.Add(p));

        var total = await PgBackfillBatcher.BackfillAsync(
            _ds, "public", "bf_direct", "new_val", "UPPER(old_val)",
            batchSize: 10, progress: progress);

        total.Should().Be(25);
        reports.Should().HaveCountGreaterOrEqualTo(3, "25 rows with batchSize=10 requires at least 3 batches");
        reports.Should().OnlyContain(p => p.RowsUpdatedThisBatch > 0);
        reports.Last().TotalRowsUpdated.Should().Be(25);
        reports.Should().OnlyContain(p => p.Table == "bf_direct" && p.Schema == "public");

        // Batch numbers must be sequential
        for (var i = 0; i < reports.Count; i++)
            reports[i].BatchNumber.Should().Be(i + 1);
    }

    [Fact]
    public async Task BackfillBatcher_NoRows_NoProgressReported()
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var create = new NpgsqlCommand(
            "CREATE TABLE bf_empty (id serial, val text)", conn);
        await create.ExecuteNonQueryAsync();

        var reports = new List<BackfillProgress>();
        var progress = new Progress<BackfillProgress>(p => reports.Add(p));

        var total = await PgBackfillBatcher.BackfillAsync(
            _ds, "public", "bf_empty", "val", "'default'", progress: progress);

        total.Should().Be(0);
        reports.Should().BeEmpty("no rows means no progress events");
    }

    [Fact]
    public async Task BackfillBatcher_NullProgress_DoesNotThrow()
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var create = new NpgsqlCommand(
            "CREATE TABLE bf_null_p (id serial, val text)", conn);
        await create.ExecuteNonQueryAsync();

        await using var insert = new NpgsqlCommand(
            "INSERT INTO bf_null_p (val) VALUES ('a'), ('b')", conn);
        await insert.ExecuteNonQueryAsync();

        Func<Task> act = () => PgBackfillBatcher.BackfillAsync(
            _ds, "public", "bf_null_p", "val", "UPPER(val)", progress: null);

        await act.Should().NotThrowAsync("null progress must be handled gracefully");
    }

    // ── Executor-level progress propagation ────────────────────────────────────

    [Fact]
    public async Task Executor_BackfillProgress_ReceivedDuringAddColumn()
    {
        // Create a table with existing rows — backfill will be triggered by Up expression.
        await using var conn = await _ds.OpenConnectionAsync();
        await using var create = new NpgsqlCommand(
            "CREATE TABLE bf_exec_t (id serial PRIMARY KEY, name text)", conn);
        await create.ExecuteNonQueryAsync();

        await using var insert = new NpgsqlCommand(
            "INSERT INTO bf_exec_t (name) SELECT 'item_' || g FROM generate_series(1,50) g", conn);
        await insert.ExecuteNonQueryAsync();

        var reports = new List<BackfillProgress>();
        _executor.BackfillProgress = new Progress<BackfillProgress>(p => reports.Add(p));

        var migration = Migration.Deserialize("""
            {
              "name": "bf_add_col",
              "operations": [{
                "type": "add_column",
                "table": "bf_exec_t",
                "column": { "name": "upper_name", "type": "text" },
                "up": "UPPER(name)"
              }]
            }
            """);

        await _executor.StartAsync(migration);

        reports.Should().NotBeEmpty("progress must be reported during backfill");
        reports.Last().TotalRowsUpdated.Should().Be(50);

        await _executor.RollbackAsync();
    }

    [Fact]
    public async Task Executor_BackfillProgress_ReceivedDuringAlterColumn()
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var create = new NpgsqlCommand(
            "CREATE TABLE bf_alter_t (id serial PRIMARY KEY, code text)", conn);
        await create.ExecuteNonQueryAsync();

        await using var insert = new NpgsqlCommand(
            "INSERT INTO bf_alter_t (code) SELECT 'code_' || g FROM generate_series(1,30) g", conn);
        await insert.ExecuteNonQueryAsync();

        var reports = new List<BackfillProgress>();
        _executor.BackfillProgress = new Progress<BackfillProgress>(p => reports.Add(p));

        var migration = Migration.Deserialize("""
            {
              "name": "bf_alter_col",
              "operations": [{
                "type": "alter_column",
                "table": "bf_alter_t",
                "column": "code",
                "up": "UPPER(code)"
              }]
            }
            """);

        await _executor.StartAsync(migration);

        reports.Should().NotBeEmpty();
        reports.Last().TotalRowsUpdated.Should().Be(30);

        await _executor.RollbackAsync();
    }
}
