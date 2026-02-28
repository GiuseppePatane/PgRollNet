using FluentAssertions;
using Npgsql;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Integration tests for alter_column lifecycle (Start → Complete / Start → Rollback).
/// </summary>
[Collection("Postgres")]
public class AlterColumnLifecycleTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_altercol_{Guid.NewGuid():N}";
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task ExecSqlAsync(string sql)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> ColumnExistsAsync(string tableName, string columnName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name=$1 AND column_name=$2",
            conn);
        cmd.Parameters.AddWithValue(tableName);
        cmd.Parameters.AddWithValue(columnName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private async Task<string?> GetColumnTypeAsync(string tableName, string columnName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT data_type FROM information_schema.columns WHERE table_schema='public' AND table_name=$1 AND column_name=$2",
            conn);
        cmd.Parameters.AddWithValue(tableName);
        cmd.Parameters.AddWithValue(columnName);
        return (string?)(await cmd.ExecuteScalarAsync());
    }

    private async Task<bool> SchemaExistsAsync(string schemaName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.schemata WHERE schema_name=$1", conn);
        cmd.Parameters.AddWithValue(schemaName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    // ── alter_column (rename) ─────────────────────────────────────────────────

    [Fact]
    public async Task AlterColumn_Rename_StartComplete_ColumnRenamed()
    {
        await ExecSqlAsync("CREATE TABLE employees (id serial PRIMARY KEY, old_name text)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_alter_rename",
              "operations": [{
                "type": "alter_column",
                "table": "employees",
                "column": "old_name",
                "name": "full_name",
                "up": "old_name"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        // During Start, dup column exists
        (await ColumnExistsAsync("employees", "_pgroll_dup_old_name")).Should().BeTrue();

        await _executor.CompleteAsync();
        // After Complete: original dropped, dup renamed to full_name
        (await ColumnExistsAsync("employees", "old_name")).Should().BeFalse();
        (await ColumnExistsAsync("employees", "full_name")).Should().BeTrue();
        (await ColumnExistsAsync("employees", "_pgroll_dup_old_name")).Should().BeFalse();
    }

    [Fact]
    public async Task AlterColumn_Rename_StartRollback_OriginalPreserved()
    {
        await ExecSqlAsync("CREATE TABLE items2 (id serial PRIMARY KEY, title text)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_alter_rb",
              "operations": [{
                "type": "alter_column",
                "table": "items2",
                "column": "title",
                "name": "label",
                "up": "title"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();

        (await ColumnExistsAsync("items2", "title")).Should().BeTrue();
        (await ColumnExistsAsync("items2", "_pgroll_dup_title")).Should().BeFalse();
        (await ColumnExistsAsync("items2", "label")).Should().BeFalse();
    }

    // ── alter_column (type change) ────────────────────────────────────────────

    [Fact]
    public async Task AlterColumn_ChangeType_StartComplete_TypeChanged()
    {
        await ExecSqlAsync("CREATE TABLE scores (id serial PRIMARY KEY, points text)");
        await ExecSqlAsync("INSERT INTO scores (points) VALUES ('42'), ('100')");

        var migration = Migration.Deserialize("""
            {
              "name": "m_alter_type",
              "operations": [{
                "type": "alter_column",
                "table": "scores",
                "column": "points",
                "data_type": "integer",
                "up": "points::integer"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        (await ColumnExistsAsync("scores", "_pgroll_dup_points")).Should().BeTrue();

        await _executor.CompleteAsync();
        (await ColumnExistsAsync("scores", "points")).Should().BeTrue();
        (await ColumnExistsAsync("scores", "_pgroll_dup_points")).Should().BeFalse();
        (await GetColumnTypeAsync("scores", "points")).Should().Be("integer");
    }

    // ── alter_column (version schema) ─────────────────────────────────────────

    [Fact]
    public async Task AlterColumn_Start_CreatesVersionSchema()
    {
        await ExecSqlAsync("CREATE TABLE tagged (id serial PRIMARY KEY, tag text)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_vs_alter",
              "operations": [{
                "type": "alter_column",
                "table": "tagged",
                "column": "tag",
                "name": "label",
                "up": "tag"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        (await SchemaExistsAsync("public_m_vs_alter")).Should().BeTrue();

        await _executor.CompleteAsync();
        (await SchemaExistsAsync("public_m_vs_alter")).Should().BeFalse();
    }

    // ── alter_column (backfill) ────────────────────────────────────────────────

    [Fact]
    public async Task AlterColumn_Start_BackfillsExistingRows()
    {
        await ExecSqlAsync("CREATE TABLE products (id serial PRIMARY KEY, code text)");
        await ExecSqlAsync("INSERT INTO products (code) VALUES ('a'), ('b'), ('c')");

        var migration = Migration.Deserialize("""
            {
              "name": "m_bf_alter",
              "operations": [{
                "type": "alter_column",
                "table": "products",
                "column": "code",
                "up": "UPPER(code)"
              }]
            }
            """);

        await _executor.StartAsync(migration);

        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM products WHERE _pgroll_dup_code IS NOT NULL", conn);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(3);

        await _executor.RollbackAsync();
    }
}
