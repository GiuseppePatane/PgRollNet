using FluentAssertions;
using Npgsql;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Integration tests for the <c>down</c> expression on <c>alter_column</c>.
///
/// The down expression propagates writes made by the <em>new</em> app (via the version schema)
/// back to the original column, keeping old-app readers consistent during a rolling deployment.
///
/// Trigger logic:
/// <list type="bullet">
///   <item>Old-app path (base schema): <c>dup_col = up_expr</c></item>
///   <item>New-app path (version schema): <c>original_col = down_expr</c></item>
/// </list>
/// </summary>
[Collection("Postgres")]
public class DownExpressionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_down_{Guid.NewGuid():N}";
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

    private async Task ExecSqlAsync(string sql)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? default : (T?)result;
    }

    // ── Down expression for alter_column (rename) ─────────────────────────────

    /// <summary>
    /// Old-app writes (base schema path) must still propagate UP to the dup column.
    /// </summary>
    [Fact]
    public async Task AlterColumn_WithDown_OldAppWrite_PropagatesUp()
    {
        await ExecSqlAsync("CREATE TABLE staff (id serial PRIMARY KEY, name text)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_down_up",
              "operations": [{
                "type": "alter_column",
                "table": "staff",
                "column": "name",
                "name": "full_name",
                "up":   "UPPER(name)",
                "down": "LOWER(full_name)"
              }]
            }
            """);

        await _executor.StartAsync(migration);

        // Old app inserts via base schema (search_path = public)
        await ExecSqlAsync("INSERT INTO staff (name) VALUES ('alice')");

        var dupValue = await ScalarAsync<string>(
            "SELECT _pgroll_dup_name FROM staff WHERE name = 'alice'");
        dupValue.Should().Be("ALICE", "UP trigger must convert old-app write");

        await _executor.RollbackAsync();
    }

    /// <summary>
    /// New-app writes (version-schema path) must propagate DOWN to the original column.
    /// </summary>
    [Fact]
    public async Task AlterColumn_WithDown_NewAppWrite_PropagatesDown()
    {
        await ExecSqlAsync("CREATE TABLE contacts (id serial PRIMARY KEY, name text)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_down_new",
              "operations": [{
                "type": "alter_column",
                "table": "contacts",
                "column": "name",
                "name": "full_name",
                "up":   "UPPER(name)",
                "down": "LOWER(full_name)"
              }]
            }
            """);

        await _executor.StartAsync(migration);

        // New app inserts via version schema view: setting full_name → triggers DOWN
        // (search_path = public_m_down_new makes the trigger take the ELSE branch)
        await ExecSqlAsync("""
            SET search_path TO 'public_m_down_new';
            INSERT INTO contacts (full_name) VALUES ('BOB');
            SET search_path TO DEFAULT;
            """);

        // DOWN trigger: name = LOWER(full_name) = LOWER('BOB') = 'bob'
        var nameValue = await ScalarAsync<string>(
            "SELECT name FROM contacts WHERE _pgroll_dup_name = 'BOB'");
        nameValue.Should().Be("bob", "DOWN trigger must propagate new-app write to original column");

        await _executor.RollbackAsync();
    }

    /// <summary>
    /// Both old and new apps can write simultaneously; triggers keep both columns in sync.
    /// </summary>
    [Fact]
    public async Task AlterColumn_WithDown_BothAppsWrite_BothColumnsInSync()
    {
        await ExecSqlAsync("CREATE TABLE members (id serial PRIMARY KEY, code text)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_down_both",
              "operations": [{
                "type": "alter_column",
                "table": "members",
                "column": "code",
                "name": "tag",
                "up":   "UPPER(code)",
                "down": "LOWER(tag)"
              }]
            }
            """);

        await _executor.StartAsync(migration);

        // Old-app insert: code = 'x' → dup_code (tag) = 'X'
        await ExecSqlAsync("INSERT INTO members (id, code) VALUES (1, 'x')");

        // New-app insert via version schema: tag = 'Y' → code = 'y'
        await ExecSqlAsync("""
            SET search_path TO 'public_m_down_both';
            INSERT INTO members (id, tag) VALUES (2, 'Y');
            SET search_path TO DEFAULT;
            """);

        var row1Dup  = await ScalarAsync<string>("SELECT _pgroll_dup_code FROM members WHERE id = 1");
        var row2Code = await ScalarAsync<string>("SELECT code FROM members WHERE id = 2");

        row1Dup.Should().Be("X");
        row2Code.Should().Be("y");

        await _executor.RollbackAsync();
    }

    /// <summary>
    /// Rollback with a Down expression must still clean up correctly.
    /// </summary>
    [Fact]
    public async Task AlterColumn_WithDown_Rollback_CleanState()
    {
        await ExecSqlAsync("CREATE TABLE widgets (id serial PRIMARY KEY, label text)");

        var migration = Migration.Deserialize("""
            {
              "name": "m_down_rb",
              "operations": [{
                "type": "alter_column",
                "table": "widgets",
                "column": "label",
                "name": "title",
                "up":   "UPPER(label)",
                "down": "LOWER(title)"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();

        // After rollback: original column intact, dup column gone
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'widgets' AND table_schema = 'public'",
            conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));

        columns.Should().Contain("label");
        columns.Should().NotContain("_pgroll_dup_label");
        columns.Should().NotContain("title");
    }

    /// <summary>
    /// Complete with a Down expression: original column replaced, Down trigger removed.
    /// </summary>
    [Fact]
    public async Task AlterColumn_WithDown_StartComplete_FinalStateCorrect()
    {
        await ExecSqlAsync("CREATE TABLE orders (id serial PRIMARY KEY, ref text)");
        await ExecSqlAsync("INSERT INTO orders (ref) VALUES ('a'), ('b')");

        var migration = Migration.Deserialize("""
            {
              "name": "m_down_complete",
              "operations": [{
                "type": "alter_column",
                "table": "orders",
                "column": "ref",
                "name": "reference",
                "up":   "UPPER(ref)",
                "down": "LOWER(reference)"
              }]
            }
            """);

        await _executor.StartAsync(migration);
        await _executor.CompleteAsync();

        // After complete: only 'reference' column present, old 'ref' and dup gone
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'orders' AND table_schema = 'public'",
            conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));

        columns.Should().Contain("reference");
        columns.Should().NotContain("ref");
        columns.Should().NotContain("_pgroll_dup_ref");

        // Backfilled values: 'a' → 'A', 'b' → 'B'
        var val = await ScalarAsync<string>("SELECT reference FROM orders WHERE id = 1");
        val.Should().Be("A");
    }
}
