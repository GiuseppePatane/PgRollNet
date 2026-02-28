using FluentAssertions;
using Npgsql;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// End-to-end integration tests for a non-public PostgreSQL schema.
/// Verifies that all schema-qualified identifiers, state records, version schemas,
/// and isolation between two schemas work correctly.
/// </summary>
[Collection("Postgres")]
public class MultiSchemaTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_ms_{Guid.NewGuid():N}";
    private PgMigrationExecutor _pub = null!;   // executor on schema "public"
    private PgMigrationExecutor _app = null!;   // executor on schema "app"

    public async Task InitializeAsync()
    {
        _ds = await DatabaseFactory.CreateIsolatedDatabaseAsync(postgres.ConnectionString, _dbName);

        // Create a custom schema
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd  = new NpgsqlCommand("CREATE SCHEMA app", conn);
        await cmd.ExecuteNonQueryAsync();

        _pub = new PgMigrationExecutor(_ds, "public");
        _app = new PgMigrationExecutor(_ds, "app");

        await _pub.InitializeAsync();
        await _app.InitializeAsync();   // idempotent — same pgroll.migrations table
    }

    public async Task DisposeAsync()
    {
        await _ds.DisposeAsync();
        await DatabaseFactory.DropDatabaseAsync(postgres.ConnectionString, _dbName);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<bool> TableExistsAsync(string schema, string table)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd  = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.tables WHERE table_schema=$1 AND table_name=$2", conn);
        cmd.Parameters.AddWithValue(schema);
        cmd.Parameters.AddWithValue(table);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private async Task<bool> ColumnExistsAsync(string schema, string table, string column)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd  = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.columns WHERE table_schema=$1 AND table_name=$2 AND column_name=$3", conn);
        cmd.Parameters.AddWithValue(schema);
        cmd.Parameters.AddWithValue(table);
        cmd.Parameters.AddWithValue(column);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private async Task<bool> SchemaExistsAsync(string schemaName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd  = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.schemata WHERE schema_name=$1", conn);
        cmd.Parameters.AddWithValue(schemaName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CustomSchema_CreateTable_CreatesInCorrectSchema()
    {
        var m = Migration.Deserialize("""
            {"name":"ms_create","operations":[
              {"type":"create_table","table":"ms_users","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);

        await _app.StartAsync(m);
        await _app.CompleteAsync();

        (await TableExistsAsync("app", "ms_users")).Should().BeTrue();
        (await TableExistsAsync("public", "ms_users")).Should().BeFalse("table must be schema-isolated");
    }

    [Fact]
    public async Task CustomSchema_HistoryIsolated_FromOtherSchema()
    {
        var mApp = Migration.Deserialize("""
            {"name":"ms_app_hist","operations":[
              {"type":"create_table","table":"ms_app_t","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);
        var mPub = Migration.Deserialize("""
            {"name":"ms_pub_hist","operations":[
              {"type":"create_table","table":"ms_pub_t","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);

        await _app.StartAsync(mApp);
        await _app.CompleteAsync();
        await _pub.StartAsync(mPub);
        await _pub.CompleteAsync();

        var appHistory = await _app.GetHistoryAsync();
        var pubHistory = await _pub.GetHistoryAsync();

        appHistory.Should().HaveCount(1).And.OnlyContain(r => r.Name == "ms_app_hist");
        pubHistory.Should().HaveCount(1).And.OnlyContain(r => r.Name == "ms_pub_hist");
    }

    [Fact]
    public async Task CustomSchema_AddColumn_CreatesInCorrectSchema()
    {
        var mCreate = Migration.Deserialize("""
            {"name":"ms_base","operations":[
              {"type":"create_table","table":"ms_items","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);
        await _app.StartAsync(mCreate);
        await _app.CompleteAsync();

        var mAdd = Migration.Deserialize("""
            {"name":"ms_addcol","operations":[
              {"type":"add_column","table":"ms_items","column":{"name":"name","type":"text"}}
            ]}
            """);
        await _app.StartAsync(mAdd);
        await _app.CompleteAsync();

        (await ColumnExistsAsync("app", "ms_items", "name")).Should().BeTrue();
        (await ColumnExistsAsync("public", "ms_items", "name")).Should().BeFalse();
    }

    [Fact]
    public async Task CustomSchema_VersionSchema_NamedAfterCustomSchema()
    {
        var mCreate = Migration.Deserialize("""
            {"name":"ms_vs_base","operations":[
              {"type":"create_table","table":"ms_vs_t","columns":[{"name":"id","type":"serial"},{"name":"code","type":"text"}]}
            ]}
            """);
        await _app.StartAsync(mCreate);
        await _app.CompleteAsync();

        // alter_column creates a version schema named "{schema}_{migration}"
        var mAlter = Migration.Deserialize("""
            {"name":"ms_vs_alter","operations":[
              {"type":"alter_column","table":"ms_vs_t","column":"code","up":"UPPER(code)"}
            ]}
            """);
        await _app.StartAsync(mAlter);

        // Version schema should be "app_ms_vs_alter", not "public_ms_vs_alter"
        (await SchemaExistsAsync("app_ms_vs_alter")).Should().BeTrue();
        (await SchemaExistsAsync("public_ms_vs_alter")).Should().BeFalse();

        await _app.RollbackAsync();
    }

    [Fact]
    public async Task CustomSchema_Rollback_CleansUpInCorrectSchema()
    {
        var m = Migration.Deserialize("""
            {"name":"ms_rb","operations":[
              {"type":"create_table","table":"ms_rb_t","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);

        await _app.StartAsync(m);
        await _app.RollbackAsync();

        (await TableExistsAsync("app", "ms_rb_t")).Should().BeFalse("table must be removed on rollback");
    }

    [Fact]
    public async Task CustomSchema_TwoMigrations_BothTrackedIndependently()
    {
        var m1 = Migration.Deserialize("""
            {"name":"ms_chain1","operations":[
              {"type":"create_table","table":"ms_chain_t","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);
        var m2 = Migration.Deserialize("""
            {"name":"ms_chain2","operations":[
              {"type":"add_column","table":"ms_chain_t","column":{"name":"val","type":"integer"}}
            ]}
            """);

        await _app.StartAsync(m1);
        await _app.CompleteAsync();
        await _app.StartAsync(m2);
        await _app.CompleteAsync();

        var history = await _app.GetHistoryAsync();
        history.Should().HaveCount(2);
        history.First(r => r.Name == "ms_chain2").Parent.Should().Be("ms_chain1");
    }
}
