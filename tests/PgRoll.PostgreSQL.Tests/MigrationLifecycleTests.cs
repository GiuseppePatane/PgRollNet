using FluentAssertions;
using Npgsql;
using PgRoll.Core.Errors;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Full lifecycle integration tests (Start → Complete / Start → Rollback) for all 8 operations.
/// Each test gets its own isolated database.
/// </summary>
[Collection("Postgres")]
public class MigrationLifecycleTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_lifecycle_{Guid.NewGuid():N}";
    private PgMigrationExecutor _executor = null!;
    private PgSchemaReader _reader = null!;

    public async Task InitializeAsync()
    {
        _ds = await DatabaseFactory.CreateIsolatedDatabaseAsync(postgres.ConnectionString, _dbName);
        _executor = new PgMigrationExecutor(_ds);
        _reader = new PgSchemaReader(_ds);
        await _executor.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _ds.DisposeAsync();
        await DatabaseFactory.DropDatabaseAsync(postgres.ConnectionString, _dbName);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task ExecSqlAsync(string sql)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> TableExistsAsync(string tableName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name=$1",
            conn);
        cmd.Parameters.AddWithValue(tableName);
        return await cmd.ExecuteScalarAsync() is not null;
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

    private async Task<bool> IndexExistsAsync(string indexName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM pg_indexes WHERE schemaname='public' AND indexname=$1",
            conn);
        cmd.Parameters.AddWithValue(indexName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    // ── create_table ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTable_StartComplete_TableExists()
    {
        var migration = Migration.Deserialize("""
            {
              "name": "m_create_users",
              "operations": [{
                "type": "create_table",
                "table": "users",
                "columns": [
                  { "name": "id", "type": "serial", "nullable": false, "primary_key": true },
                  { "name": "email", "type": "text", "nullable": false }
                ]
              }]
            }
            """);

        await _executor.StartAsync(migration);
        (await TableExistsAsync("users")).Should().BeTrue();

        await _executor.CompleteAsync();
        (await TableExistsAsync("users")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateTable_StartRollback_TableRemoved()
    {
        var migration = Migration.Deserialize("""
            {
              "name": "m_create_rollback",
              "operations": [{
                "type": "create_table",
                "table": "rollback_me",
                "columns": [{ "name": "id", "type": "serial", "nullable": false }]
              }]
            }
            """);

        await _executor.StartAsync(migration);
        (await TableExistsAsync("rollback_me")).Should().BeTrue();

        await _executor.RollbackAsync();
        (await TableExistsAsync("rollback_me")).Should().BeFalse();
    }

    // ── drop_table ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DropTable_StartComplete_TableDropped()
    {
        await ExecSqlAsync("CREATE TABLE victims (id serial PRIMARY KEY)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_drop_victims",
              "operations": [{ "type": "drop_table", "table": "victims" }]
            }
            """);

        await _executor.StartAsync(migration);
        // After start, original name is gone (renamed to _pgroll_del_victims)
        (await TableExistsAsync("victims")).Should().BeFalse();

        await _executor.CompleteAsync();
        // After complete, soft-delete table is also gone
        (await TableExistsAsync("_pgroll_del_victims")).Should().BeFalse();
    }

    [Fact]
    public async Task DropTable_StartRollback_TableRestored()
    {
        await ExecSqlAsync("CREATE TABLE survivors (id serial PRIMARY KEY)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_drop_rollback",
              "operations": [{ "type": "drop_table", "table": "survivors" }]
            }
            """);

        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();

        (await TableExistsAsync("survivors")).Should().BeTrue();
    }

    // ── rename_table ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameTable_StartComplete_TableRenamed()
    {
        await ExecSqlAsync("CREATE TABLE old_name (id serial PRIMARY KEY)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_rename",
              "operations": [{ "type": "rename_table", "from": "old_name", "to": "new_name" }]
            }
            """);

        await _executor.StartAsync(migration);
        // start is no-op — table still has old name
        (await TableExistsAsync("old_name")).Should().BeTrue();

        await _executor.CompleteAsync();
        (await TableExistsAsync("old_name")).Should().BeFalse();
        (await TableExistsAsync("new_name")).Should().BeTrue();
    }

    // ── add_column ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddColumn_StartComplete_ColumnExists()
    {
        await ExecSqlAsync("CREATE TABLE people (id serial PRIMARY KEY)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_add_col",
              "operations": [{
                "type": "add_column",
                "table": "people",
                "column": { "name": "email", "type": "text", "nullable": true }
              }]
            }
            """);

        await _executor.StartAsync(migration);
        (await ColumnExistsAsync("people", "email")).Should().BeTrue();

        await _executor.CompleteAsync();
        (await ColumnExistsAsync("people", "email")).Should().BeTrue();
    }

    [Fact]
    public async Task AddColumn_StartRollback_ColumnRemoved()
    {
        await ExecSqlAsync("CREATE TABLE things (id serial PRIMARY KEY)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_add_col_rb",
              "operations": [{
                "type": "add_column",
                "table": "things",
                "column": { "name": "temp_col", "type": "text", "nullable": true }
              }]
            }
            """);

        await _executor.StartAsync(migration);
        (await ColumnExistsAsync("things", "temp_col")).Should().BeTrue();

        await _executor.RollbackAsync();
        (await ColumnExistsAsync("things", "temp_col")).Should().BeFalse();
    }

    // ── drop_column ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DropColumn_StartComplete_ColumnDropped()
    {
        await ExecSqlAsync("CREATE TABLE widgets (id serial PRIMARY KEY, old_col text)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_drop_col",
              "operations": [{ "type": "drop_column", "table": "widgets", "column": "old_col" }]
            }
            """);

        await _executor.StartAsync(migration);
        // Start drops the column immediately
        (await ColumnExistsAsync("widgets", "old_col")).Should().BeFalse();

        await _executor.CompleteAsync();
        // Complete is a no-op — column was already dropped in Start
        (await ColumnExistsAsync("widgets", "old_col")).Should().BeFalse();
    }

    // ── rename_column ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameColumn_StartComplete_ColumnRenamed()
    {
        await ExecSqlAsync("CREATE TABLE orders (id serial PRIMARY KEY, old_field text)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_rename_col",
              "operations": [{
                "type": "rename_column",
                "table": "orders",
                "from": "old_field",
                "to": "new_field"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        // Start renames the column immediately
        (await ColumnExistsAsync("orders", "old_field")).Should().BeFalse();
        (await ColumnExistsAsync("orders", "new_field")).Should().BeTrue();

        await _executor.CompleteAsync();
        // Complete is a no-op — column was already renamed in Start
        (await ColumnExistsAsync("orders", "old_field")).Should().BeFalse();
        (await ColumnExistsAsync("orders", "new_field")).Should().BeTrue();
    }

    // ── create_index ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateIndex_StartComplete_IndexExists()
    {
        await ExecSqlAsync("CREATE TABLE catalog (id serial PRIMARY KEY, sku text)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_create_idx",
              "operations": [{
                "type": "create_index",
                "name": "idx_catalog_sku",
                "table": "catalog",
                "columns": ["sku"],
                "unique": false
              }]
            }
            """);

        await _executor.StartAsync(migration);
        (await IndexExistsAsync("idx_catalog_sku")).Should().BeTrue();

        await _executor.CompleteAsync();
        (await IndexExistsAsync("idx_catalog_sku")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateIndex_StartRollback_IndexRemoved()
    {
        await ExecSqlAsync("CREATE TABLE inv (id serial PRIMARY KEY, code text)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_idx_rb",
              "operations": [{
                "type": "create_index",
                "name": "idx_inv_code",
                "table": "inv",
                "columns": ["code"]
              }]
            }
            """);

        await _executor.StartAsync(migration);
        (await IndexExistsAsync("idx_inv_code")).Should().BeTrue();

        await _executor.RollbackAsync();
        (await IndexExistsAsync("idx_inv_code")).Should().BeFalse();
    }

    // ── drop_index ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DropIndex_StartComplete_IndexDropped()
    {
        await ExecSqlAsync("""
            CREATE TABLE specs (id serial PRIMARY KEY, code text);
            CREATE INDEX idx_specs_code ON specs(code);
            """);

        var migration = Migration.Deserialize("""
            {
              "name": "m_drop_idx",
              "operations": [{ "type": "drop_index", "name": "idx_specs_code" }]
            }
            """);

        await _executor.StartAsync(migration);
        // Start drops the index immediately
        (await IndexExistsAsync("idx_specs_code")).Should().BeFalse();

        await _executor.CompleteAsync();
        // Complete is a no-op — index was already dropped in Start
        (await IndexExistsAsync("idx_specs_code")).Should().BeFalse();
    }

    // ── Error cases ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_WithActiveMigration_Throws()
    {
        await ExecSqlAsync("CREATE TABLE foo (id serial PRIMARY KEY)");
        var m1 = Migration.Deserialize("""
            {"name":"m1","operations":[{"type":"drop_table","table":"foo"}]}
            """);
        await _executor.StartAsync(m1);

        var m2 = Migration.Deserialize("""
            {"name":"m2","operations":[{"type":"create_table","table":"bar","columns":[{"name":"id","type":"serial"}]}]}
            """);

        var act = async () => await _executor.StartAsync(m2);
        await act.Should().ThrowAsync<MigrationAlreadyActiveError>();
    }

    [Fact]
    public async Task Complete_WithNoActiveMigration_Throws()
    {
        var act = async () => await _executor.CompleteAsync();
        await act.Should().ThrowAsync<NoActiveMigrationError>();
    }

    [Fact]
    public async Task Rollback_WithNoActiveMigration_Throws()
    {
        var act = async () => await _executor.RollbackAsync();
        await act.Should().ThrowAsync<NoActiveMigrationError>();
    }

    [Fact]
    public async Task Start_InvalidOperation_Throws()
    {
        // Table doesn't exist, drop_table should fail validation
        var migration = Migration.Deserialize("""
            {"name":"bad","operations":[{"type":"drop_table","table":"nonexistent"}]}
            """);

        var act = async () => await _executor.StartAsync(migration);
        await act.Should().ThrowAsync<InvalidMigrationError>();
    }
}
