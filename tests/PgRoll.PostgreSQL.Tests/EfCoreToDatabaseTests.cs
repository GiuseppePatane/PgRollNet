using FluentAssertions;
using Npgsql;
using PgRoll.EntityFrameworkCore;
using PgRoll.PostgreSQL.Tests.Infrastructure;
using EfAddColumn = Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation;
using EfCreateTable = Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation;
using EfDropTable = Microsoft.EntityFrameworkCore.Migrations.Operations.DropTableOperation;
using EfRenameTable = Microsoft.EntityFrameworkCore.Migrations.Operations.RenameTableOperation;
using EfCreateIndex = Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation;
using EfDropColumn = Microsoft.EntityFrameworkCore.Migrations.Operations.DropColumnOperation;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// End-to-end tests that convert EF Core <see cref="MigrationOperation"/> instances to a pgroll
/// <see cref="PgRoll.Core.Models.Migration"/> via <see cref="EfCoreMigrationConverter"/>, then
/// execute them against a live PostgreSQL database and verify the resulting schema.
/// </summary>
[Collection("Postgres")]
public class EfCoreToDatabaseTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_efcore_{Guid.NewGuid():N}";
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

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name=$1", conn);
        cmd.Parameters.AddWithValue(table);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private async Task<bool> ColumnExistsAsync(string table, string column)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name=$1 AND column_name=$2", conn);
        cmd.Parameters.AddWithValue(table);
        cmd.Parameters.AddWithValue(column);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private async Task ApplyAsync(string name, params Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation[] efOps)
    {
        var result = EfCoreMigrationConverter.Convert(name, efOps);
        await _executor.StartAsync(result.Migration);
        await _executor.CompleteAsync();
    }

    // ── create_table ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EfCreateTable_ExecutedAgainstDb_TableCreated()
    {
        await ApplyAsync("ef_create",
            new EfCreateTable
            {
                Name = "ef_users",
                Schema = "public",
                Columns = { new EfAddColumn { Name = "id", ClrType = typeof(int), ColumnType = "serial" },
                             new EfAddColumn { Name = "name", ClrType = typeof(string), ColumnType = "text", IsNullable = true } }
            });

        (await TableExistsAsync("ef_users")).Should().BeTrue();
        (await ColumnExistsAsync("ef_users", "id")).Should().BeTrue();
        (await ColumnExistsAsync("ef_users", "name")).Should().BeTrue();
    }

    // ── add_column ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EfAddColumn_ExecutedAgainstDb_ColumnAdded()
    {
        // First create the table manually
        await ApplyAsync("ef_base",
            new EfCreateTable
            {
                Name = "ef_items",
                Schema = "public",
                Columns = { new EfAddColumn { Name = "id", ClrType = typeof(int), ColumnType = "serial" } }
            });

        // Then add a column via EF Core op
        await ApplyAsync("ef_addcol",
            new EfAddColumn
            {
                Table = "ef_items",
                Name = "price",
                ClrType = typeof(decimal),
                ColumnType = "numeric",
                IsNullable = true
            });

        (await ColumnExistsAsync("ef_items", "price")).Should().BeTrue();
    }

    // ── drop_table ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EfDropTable_ExecutedAgainstDb_TableRemoved()
    {
        await ApplyAsync("ef_before_drop",
            new EfCreateTable
            {
                Name = "ef_to_drop",
                Schema = "public",
                Columns = { new EfAddColumn { Name = "id", ClrType = typeof(int), ColumnType = "serial" } }
            });

        (await TableExistsAsync("ef_to_drop")).Should().BeTrue();

        await ApplyAsync("ef_drop",
            new EfDropTable { Name = "ef_to_drop", Schema = "public" });

        (await TableExistsAsync("ef_to_drop")).Should().BeFalse();
    }

    // ── drop_column ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EfDropColumn_ExecutedAgainstDb_ColumnRemoved()
    {
        await ApplyAsync("ef_dropcol_base",
            new EfCreateTable
            {
                Name = "ef_dropcol_t",
                Schema = "public",
                Columns = {
                    new EfAddColumn { Name = "id", ClrType = typeof(int), ColumnType = "serial" },
                    new EfAddColumn { Name = "tmp", ClrType = typeof(string), ColumnType = "text", IsNullable = true }
                }
            });

        await ApplyAsync("ef_dropcol",
            new EfDropColumn { Table = "ef_dropcol_t", Name = "tmp" });

        (await ColumnExistsAsync("ef_dropcol_t", "tmp")).Should().BeFalse();
        (await ColumnExistsAsync("ef_dropcol_t", "id")).Should().BeTrue();
    }

    // ── create_index ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EfCreateIndex_ExecutedAgainstDb_IndexCreated()
    {
        await ApplyAsync("ef_idx_base",
            new EfCreateTable
            {
                Name = "ef_indexed",
                Schema = "public",
                Columns = {
                    new EfAddColumn { Name = "id", ClrType = typeof(int), ColumnType = "serial" },
                    new EfAddColumn { Name = "email", ClrType = typeof(string), ColumnType = "text", IsNullable = true }
                }
            });

        await ApplyAsync("ef_idx",
            new EfCreateIndex
            {
                Name = "idx_ef_email",
                Table = "ef_indexed",
                Columns = ["email"],
                IsUnique = false
            });

        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM pg_indexes WHERE schemaname='public' AND indexname='idx_ef_email'", conn);
        (await cmd.ExecuteScalarAsync()).Should().NotBeNull();
    }

    // ── round-trip: Convert → Serialize → Deserialize → Execute ──────────────

    [Fact]
    public async Task EfRoundTrip_SerializeDeserialize_ProducesSameResult()
    {
        var efOps = new EfCreateTable[]
        {
            new EfCreateTable
            {
                Name = "rt_table",
                Schema = "public",
                Columns = { new EfAddColumn { Name = "id", ClrType = typeof(int), ColumnType = "serial" } }
            }
        };

        var result = EfCoreMigrationConverter.Convert("rt_migration",
            efOps.Cast<Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation>());

        // Serialize → deserialize round-trip
        var json = result.Migration.Serialize();
        var deserialized = PgRoll.Core.Models.Migration.Deserialize(json);

        // Execute the deserialized migration
        await _executor.StartAsync(deserialized);
        await _executor.CompleteAsync();

        (await TableExistsAsync("rt_table")).Should().BeTrue();
    }
}
