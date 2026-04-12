using FluentAssertions;
using Npgsql;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

[Collection("Postgres")]
public class OperationalFailureTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_ops_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        _ds = await DatabaseFactory.CreateIsolatedDatabaseAsync(postgres.ConnectionString, _dbName);
        await using var executor = new PgMigrationExecutor(_ds);
        await executor.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _ds.DisposeAsync();
        await DatabaseFactory.DropDatabaseAsync(postgres.ConnectionString, _dbName);
    }

    [Fact]
    public async Task StartAsync_WithRestrictedRole_FailsWithPermissionDenied()
    {
        await using (var conn = await _ds.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("""
                CREATE SCHEMA restricted;
                CREATE ROLE limited_role NOLOGIN;
                GRANT limited_role TO CURRENT_USER;
                GRANT USAGE ON SCHEMA restricted TO limited_role;
                GRANT USAGE ON SCHEMA pgroll TO limited_role;
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE pgroll.migrations TO limited_role;
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var executor = new PgMigrationExecutor(_ds, schemaName: "restricted", role: "limited_role");
        var migration = Migration.Deserialize("""
            {
              "name": "restricted_create_table",
              "operations": [
                { "type": "create_table", "table": "should_fail", "columns": [{ "name": "id", "type": "serial" }] }
              ]
            }
            """);

        var act = async () => await executor.StartAsync(migration);

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.MessageText.Should().Contain("permission denied");
    }

    [Fact]
    public async Task CompleteAsync_WhenDeferredDdlFails_LeavesMigrationActiveUntilRollback()
    {
        await using (var conn = await _ds.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("CREATE TABLE rename_source(id serial PRIMARY KEY)", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var executor = new PgMigrationExecutor(_ds);
        var migration = Migration.Deserialize("""
            {
              "name": "rename_with_external_tamper",
              "operations": [
                { "type": "rename_table", "from": "rename_source", "to": "rename_target" }
              ]
            }
            """);

        await executor.StartAsync(migration);

        await using (var conn = await _ds.OpenConnectionAsync())
        {
            await using var cmd = new NpgsqlCommand("ALTER TABLE rename_source RENAME TO rename_source_external", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var complete = async () => await executor.CompleteAsync();
        await complete.Should().ThrowAsync<PostgresException>();

        var active = await executor.GetStatusAsync();
        active.Should().NotBeNull();
        active!.Name.Should().Be("rename_with_external_tamper");

        await executor.RollbackAsync();
        (await executor.GetStatusAsync()).Should().BeNull();
    }
}
