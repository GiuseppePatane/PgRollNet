using FluentAssertions;
using Npgsql;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Tests for PgVersionSchemaManager — create/drop version schemas with views.
/// </summary>
[Collection("Postgres")]
public class VersionSchemaTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private NpgsqlConnection _conn = null!;
    private readonly string _dbName = $"pgroll_vstest_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        _ds = await DatabaseFactory.CreateIsolatedDatabaseAsync(postgres.ConnectionString, _dbName);
        _conn = await _ds.OpenConnectionAsync();

        // Create a base table for view tests
        await using var cmd = new NpgsqlCommand(
            "CREATE TABLE public.items (id serial PRIMARY KEY, name text, code text)", _conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _conn.DisposeAsync();
        await _ds.DisposeAsync();
        await DatabaseFactory.DropDatabaseAsync(postgres.ConnectionString, _dbName);
    }

    [Fact]
    public async Task CreateVersionSchema_CreatesSchemaAndView()
    {
        var colExprs = new[] { "\"id\"", "\"name\"", "\"code\"" };
        await PgVersionSchemaManager.CreateVersionSchemaAsync(_conn, "public", "m_test", "items", colExprs);

        var schemaExists = await ScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM information_schema.schemata WHERE schema_name = 'public_m_test')");
        schemaExists.Should().BeTrue();

        var viewExists = await ScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM information_schema.views WHERE table_schema = 'public_m_test' AND table_name = 'items')");
        viewExists.Should().BeTrue();
    }

    [Fact]
    public async Task CreateVersionSchema_ViewReturnsCorrectColumns()
    {
        var colExprs = new[] { "\"id\"", "\"name\"" };
        await PgVersionSchemaManager.CreateVersionSchemaAsync(_conn, "public", "m_view", "items", colExprs);

        // Insert a row and query via the view
        await using var insertCmd = new NpgsqlCommand(
            "INSERT INTO public.items (name, code) VALUES ('test', 'T001')", _conn);
        await insertCmd.ExecuteNonQueryAsync();

        await using var selectCmd = new NpgsqlCommand(
            """SELECT name FROM "public_m_view"."items" """, _conn);
        var result = await selectCmd.ExecuteScalarAsync();
        result.Should().Be("test");
    }

    [Fact]
    public async Task DropVersionSchema_RemovesSchemaAndView()
    {
        var colExprs = new[] { "\"id\"" };
        await PgVersionSchemaManager.CreateVersionSchemaAsync(_conn, "public", "m_drop", "items", colExprs);
        await PgVersionSchemaManager.DropVersionSchemaAsync(_conn, "public", "m_drop");

        var schemaExists = await ScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM information_schema.schemata WHERE schema_name = 'public_m_drop')");
        schemaExists.Should().BeFalse();
    }

    [Fact]
    public void VersionSchemaName_ReturnsExpectedFormat()
    {
        PgVersionSchemaManager.VersionSchemaName("public", "add_email_col")
            .Should().Be("public_add_email_col");
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, _conn);
        var result = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(result!, typeof(T));
    }
}
