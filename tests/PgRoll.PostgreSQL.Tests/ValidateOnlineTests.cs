using FluentAssertions;
using Npgsql;
using PgRoll.Core.Models;
using PgRoll.Core.Operations;
using PgRoll.Core.Schema;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Integration tests for online schema validation — the path taken by
/// <c>pgroll validate &lt;file&gt;</c> (without <c>--offline</c>).
///
/// Uses <see cref="PgSchemaReader"/> to read a live schema snapshot then calls
/// <see cref="IMigrationOperation.Validate(SchemaSnapshot)"/> on each operation,
/// replicating exactly what <c>ValidateCommand</c> does in online mode.
/// </summary>
[Collection("Postgres")]
public class ValidateOnlineTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_val_{Guid.NewGuid():N}";
    private PgSchemaReader _reader = null!;
    private PgMigrationExecutor _executor = null!;

    public async Task InitializeAsync()
    {
        _ds = await DatabaseFactory.CreateIsolatedDatabaseAsync(postgres.ConnectionString, _dbName);
        _reader = new PgSchemaReader(_ds);
        _executor = new PgMigrationExecutor(_ds);
        await _executor.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _ds.DisposeAsync();
        await DatabaseFactory.DropDatabaseAsync(postgres.ConnectionString, _dbName);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task ExecSqlAsync(string sql)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<SchemaSnapshot> SnapshotAsync() =>
        await _reader.ReadSchemaAsync("public");

    // ── add_column ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddColumn_TableExists_Valid()
    {
        await ExecSqlAsync("CREATE TABLE val_users (id serial)");

        var snapshot = await SnapshotAsync();
        var op = new AddColumnOperation { Table = "val_users", Column = new ColumnDefinition { Name = "email", Type = "text" } };

        op.Validate(snapshot).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task AddColumn_TableMissing_Invalid()
    {
        var snapshot = await SnapshotAsync();
        var op = new AddColumnOperation { Table = "nonexistent", Column = new ColumnDefinition { Name = "email", Type = "text" } };

        var r = op.Validate(snapshot);
        r.IsValid.Should().BeFalse();
        r.Error.Should().Contain("nonexistent");
    }

    [Fact]
    public async Task AddColumn_ColumnAlreadyExists_Invalid()
    {
        await ExecSqlAsync("CREATE TABLE val_dupcol (id serial, email text)");

        var snapshot = await SnapshotAsync();
        var op = new AddColumnOperation { Table = "val_dupcol", Column = new ColumnDefinition { Name = "email", Type = "text" } };

        var r = op.Validate(snapshot);
        r.IsValid.Should().BeFalse();
        r.Error.Should().Contain("email");
    }

    // ── drop_column ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DropColumn_ColumnExists_Valid()
    {
        await ExecSqlAsync("CREATE TABLE val_dropcol (id serial, tmp text)");

        var snapshot = await SnapshotAsync();
        new DropColumnOperation { Table = "val_dropcol", Column = "tmp" }
            .Validate(snapshot).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task DropColumn_ColumnMissing_Invalid()
    {
        await ExecSqlAsync("CREATE TABLE val_dropcol2 (id serial)");

        var snapshot = await SnapshotAsync();
        new DropColumnOperation { Table = "val_dropcol2", Column = "ghost" }
            .Validate(snapshot).IsValid.Should().BeFalse();
    }

    // ── drop_table ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DropTable_TableExists_Valid()
    {
        await ExecSqlAsync("CREATE TABLE val_droptbl (id serial)");

        var snapshot = await SnapshotAsync();
        new DropTableOperation { Table = "val_droptbl" }
            .Validate(snapshot).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task DropTable_TableMissing_Invalid()
    {
        var snapshot = await SnapshotAsync();
        new DropTableOperation { Table = "ghost_table" }
            .Validate(snapshot).IsValid.Should().BeFalse();
    }

    // ── rename_table ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameTable_FromExists_ToFree_Valid()
    {
        await ExecSqlAsync("CREATE TABLE val_renold (id serial)");

        var snapshot = await SnapshotAsync();
        new RenameTableOperation { From = "val_renold", To = "val_rennew" }
            .Validate(snapshot).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task RenameTable_ToAlreadyExists_Invalid()
    {
        await ExecSqlAsync("CREATE TABLE val_rena (id serial)");
        await ExecSqlAsync("CREATE TABLE val_renb (id serial)");

        var snapshot = await SnapshotAsync();
        new RenameTableOperation { From = "val_rena", To = "val_renb" }
            .Validate(snapshot).IsValid.Should().BeFalse();
    }

    // ── create_table ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTable_NameFree_Valid()
    {
        var snapshot = await SnapshotAsync();
        new CreateTableOperation
        {
            Table = "brand_new_table",
            Columns = [new ColumnDefinition { Name = "id", Type = "serial" }]
        }.Validate(snapshot).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task CreateTable_AlreadyExists_Invalid()
    {
        await ExecSqlAsync("CREATE TABLE val_exists (id serial)");

        var snapshot = await SnapshotAsync();
        new CreateTableOperation
        {
            Table = "val_exists",
            Columns = [new ColumnDefinition { Name = "id", Type = "serial" }]
        }.Validate(snapshot).IsValid.Should().BeFalse();
    }

    // ── full migration validation (online mode round-trip) ─────────────────────

    [Fact]
    public async Task FullMigration_AllOpsValid_NoErrors()
    {
        await ExecSqlAsync("CREATE TABLE val_full (id serial)");

        var snapshot = await SnapshotAsync();
        var migration = Migration.Deserialize("""
            {"name":"val_m","operations":[
              {"type":"add_column","table":"val_full","column":{"name":"name","type":"text"}},
              {"type":"create_table","table":"val_new","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);

        var errors = migration.Operations
            .Select(op => op.Validate(snapshot))
            .Where(r => !r.IsValid)
            .ToList();

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task FullMigration_OneOpInvalid_ReturnsError()
    {
        var snapshot = await SnapshotAsync();

        // add_column for a table that doesn't exist
        var migration = Migration.Deserialize("""
            {"name":"val_bad","operations":[
              {"type":"add_column","table":"ghost_table","column":{"name":"col","type":"text"}}
            ]}
            """);

        var errors = migration.Operations
            .Select(op => op.Validate(snapshot))
            .Where(r => !r.IsValid)
            .ToList();

        errors.Should().HaveCount(1);
    }
}
