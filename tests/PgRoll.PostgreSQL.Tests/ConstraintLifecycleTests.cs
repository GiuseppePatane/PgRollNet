using FluentAssertions;
using Npgsql;
using PgRoll.Core.Errors;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Integration tests for constraint operation lifecycle (create/drop/rename).
/// </summary>
[Collection("Postgres")]
public class ConstraintLifecycleTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_constraints_{Guid.NewGuid():N}";
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

    private async Task ExecSqlAsync(string sql)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> ConstraintExistsAsync(string tableName, string constraintName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT 1 FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = 'public' AND t.relname = $1 AND c.conname = $2
            """, conn);
        cmd.Parameters.AddWithValue(tableName);
        cmd.Parameters.AddWithValue(constraintName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    // ── create_constraint (check) ─────────────────────────────────────────────

    [Fact]
    public async Task CreateCheckConstraint_StartComplete_ConstraintValid()
    {
        await ExecSqlAsync("CREATE TABLE chk_users (id serial PRIMARY KEY, age int)");
        await ExecSqlAsync("INSERT INTO chk_users (age) VALUES (25)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_create_chk",
              "operations": [{
                "type": "create_constraint",
                "table": "chk_users",
                "name": "chk_age_positive",
                "constraint_type": "check",
                "check": "age > 0"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        // After Start: constraint added NOT VALID
        (await ConstraintExistsAsync("chk_users", "chk_age_positive")).Should().BeTrue();

        await _executor.CompleteAsync();
        // After Complete: constraint validated
        (await ConstraintExistsAsync("chk_users", "chk_age_positive")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateCheckConstraint_StartRollback_ConstraintRemoved()
    {
        await ExecSqlAsync("CREATE TABLE chk_rb (id serial PRIMARY KEY, score int)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_chk_rb",
              "operations": [{
                "type": "create_constraint",
                "table": "chk_rb",
                "name": "chk_score",
                "constraint_type": "check",
                "check": "score >= 0"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        (await ConstraintExistsAsync("chk_rb", "chk_score")).Should().BeTrue();

        await _executor.RollbackAsync();
        (await ConstraintExistsAsync("chk_rb", "chk_score")).Should().BeFalse();
    }

    // ── create_constraint (unique) ────────────────────────────────────────────

    [Fact]
    public async Task CreateUniqueConstraint_StartComplete_ConstraintExists()
    {
        await ExecSqlAsync("CREATE TABLE uniq_users (id serial PRIMARY KEY, email text)");
        await ExecSqlAsync("INSERT INTO uniq_users (email) VALUES ('a@b.com')");

        var migration = Migration.Deserialize("""
            {
              "name": "m_create_uniq",
              "operations": [{
                "type": "create_constraint",
                "table": "uniq_users",
                "name": "uniq_email",
                "constraint_type": "unique",
                "columns": ["email"]
              }]
            }
            """);

        await _executor.StartAsync(migration);
        await _executor.CompleteAsync();

        (await ConstraintExistsAsync("uniq_users", "uniq_email")).Should().BeTrue();
    }

    // ── drop_constraint ────────────────────────────────────────────────────────

    [Fact]
    public async Task DropConstraint_StartComplete_ConstraintDropped()
    {
        await ExecSqlAsync("""
            CREATE TABLE drop_chk (id serial PRIMARY KEY, val int,
              CONSTRAINT chk_val CHECK (val > 0))
            """);

        var migration = Migration.Deserialize("""
            {
              "name": "m_drop_chk",
              "operations": [{
                "type": "drop_constraint",
                "table": "drop_chk",
                "name": "chk_val"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        // Start is no-op — constraint still there
        (await ConstraintExistsAsync("drop_chk", "chk_val")).Should().BeTrue();

        await _executor.CompleteAsync();
        (await ConstraintExistsAsync("drop_chk", "chk_val")).Should().BeFalse();
    }

    [Fact]
    public async Task DropConstraint_StartRollback_ConstraintPreserved()
    {
        await ExecSqlAsync("""
            CREATE TABLE drop_rb (id serial PRIMARY KEY, n int,
              CONSTRAINT chk_n CHECK (n >= 0))
            """);

        var migration = Migration.Deserialize("""
            {
              "name": "m_drop_rb",
              "operations": [{
                "type": "drop_constraint",
                "table": "drop_rb",
                "name": "chk_n"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();

        (await ConstraintExistsAsync("drop_rb", "chk_n")).Should().BeTrue();
    }

    // ── rename_constraint ─────────────────────────────────────────────────────

    [Fact]
    public async Task RenameConstraint_StartComplete_ConstraintRenamed()
    {
        await ExecSqlAsync("""
            CREATE TABLE ren_chk (id serial PRIMARY KEY, age int,
              CONSTRAINT chk_age CHECK (age > 0))
            """);

        var migration = Migration.Deserialize("""
            {
              "name": "m_rename_chk",
              "operations": [{
                "type": "rename_constraint",
                "table": "ren_chk",
                "from": "chk_age",
                "to": "chk_positive_age"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        (await ConstraintExistsAsync("ren_chk", "chk_age")).Should().BeTrue(); // Start = no-op

        await _executor.CompleteAsync();
        (await ConstraintExistsAsync("ren_chk", "chk_age")).Should().BeFalse();
        (await ConstraintExistsAsync("ren_chk", "chk_positive_age")).Should().BeTrue();
    }

    [Fact]
    public async Task RenameConstraint_StartRollback_OriginalNamePreserved()
    {
        await ExecSqlAsync("""
            CREATE TABLE ren_rb (id serial PRIMARY KEY, x int,
              CONSTRAINT chk_x CHECK (x > 0))
            """);

        var migration = Migration.Deserialize("""
            {
              "name": "m_ren_rb",
              "operations": [{
                "type": "rename_constraint",
                "table": "ren_rb",
                "from": "chk_x",
                "to": "chk_new_x"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();

        (await ConstraintExistsAsync("ren_rb", "chk_x")).Should().BeTrue();
    }

    // ── Validation errors ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateConstraint_AlreadyExists_ThrowsOnStart()
    {
        await ExecSqlAsync("""
            CREATE TABLE dup_chk (id serial PRIMARY KEY, n int,
              CONSTRAINT chk_n CHECK (n > 0))
            """);

        var migration = Migration.Deserialize("""
            {
              "name": "m_dup",
              "operations": [{
                "type": "create_constraint",
                "table": "dup_chk",
                "name": "chk_n",
                "constraint_type": "check",
                "check": "n > 0"
              }]
            }
            """);

        var act = async () => await _executor.StartAsync(migration);
        await act.Should().ThrowAsync<InvalidMigrationError>();
    }

    [Fact]
    public async Task DropConstraint_DoesNotExist_ThrowsOnStart()
    {
        await ExecSqlAsync("CREATE TABLE no_chk (id serial PRIMARY KEY, n int)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_nodrop",
              "operations": [{
                "type": "drop_constraint",
                "table": "no_chk",
                "name": "chk_missing"
              }]
            }
            """);

        var act = async () => await _executor.StartAsync(migration);
        await act.Should().ThrowAsync<InvalidMigrationError>();
    }
}
