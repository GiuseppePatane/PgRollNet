using FluentAssertions;
using Npgsql;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Integration tests for sequential multi-migration application — the same flow executed by
/// <c>pgroll migrate &lt;dir&gt;</c>: iterate pending files, call <c>StartAsync + CompleteAsync</c>
/// for each, skip already-applied ones.
/// </summary>
[Collection("Postgres")]
public class MigrateSequenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_migrate_{Guid.NewGuid():N}";
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

    /// Applies a migration file exactly as MigrateCommand does.
    private async Task ApplyAsync(Migration migration)
    {
        await _executor.StartAsync(migration);
        await _executor.CompleteAsync();
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migrate_ThreeSequentialMigrations_AllApplied()
    {
        // m1: create table
        await ApplyAsync(Migration.Deserialize("""
            {"name":"seq_001","operations":[
              {"type":"create_table","table":"seq_users","columns":[{"name":"id","type":"serial","primary_key":true}]}
            ]}
            """));

        // m2: add column
        await ApplyAsync(Migration.Deserialize("""
            {"name":"seq_002","operations":[
              {"type":"add_column","table":"seq_users","column":{"name":"email","type":"text"}}
            ]}
            """));

        // m3: create index
        await ApplyAsync(Migration.Deserialize("""
            {"name":"seq_003","operations":[
              {"type":"create_index","name":"idx_seq_email","table":"seq_users","columns":["email"]}
            ]}
            """));

        var history = await _executor.GetHistoryAsync();
        history.Should().HaveCount(3);
        history.Should().OnlyContain(r => r.Done);

        (await TableExistsAsync("seq_users")).Should().BeTrue();
        (await ColumnExistsAsync("seq_users", "email")).Should().BeTrue();

        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM pg_indexes WHERE schemaname='public' AND indexname='idx_seq_email'", conn);
        (await cmd.ExecuteScalarAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task Migrate_SkipsAlreadyApplied_NoDuplicateExecution()
    {
        var m1 = Migration.Deserialize("""
            {"name":"skip_001","operations":[
              {"type":"create_table","table":"skip_t","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);

        await ApplyAsync(m1);

        // Simulate MigrateCommand's "skip applied" logic:
        // m1 is already in history → should not be started again.
        var history = await _executor.GetHistoryAsync();
        var applied = history.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

        var toApply = new[] { "skip_001", "skip_002" }
            .Where(name => !applied.Contains(name))
            .ToList();

        toApply.Should().BeEquivalentTo(["skip_002"]);
    }

    [Fact]
    public async Task Migrate_MultipleOpsInOneMigration_AllTakeEffect()
    {
        // Single migration file with multiple independent create_table operations.
        // (Ops that depend on a prior op in the same migration can't validate against the
        // initial snapshot — use separate migrations for dependent operations.)
        await ApplyAsync(Migration.Deserialize("""
            {"name":"multi_op","operations":[
              {"type":"create_table","table":"multi_t1","columns":[{"name":"id","type":"serial"}]},
              {"type":"create_table","table":"multi_t2","columns":[{"name":"id","type":"serial"},{"name":"value","type":"integer"}]}
            ]}
            """));

        (await TableExistsAsync("multi_t1")).Should().BeTrue();
        (await TableExistsAsync("multi_t2")).Should().BeTrue();
        (await ColumnExistsAsync("multi_t2", "value")).Should().BeTrue();
    }

    [Fact]
    public async Task Migrate_FullBuildUp_FinalSchemaMatchesExpected()
    {
        // Build up a realistic schema through 4 migrations.
        var migrations = new[]
        {
            """{"name":"build_001","operations":[{"type":"create_table","table":"build_users","columns":[{"name":"id","type":"serial","primary_key":true},{"name":"username","type":"text"}]}]}""",
            """{"name":"build_002","operations":[{"type":"add_column","table":"build_users","column":{"name":"email","type":"text"}}]}""",
            """{"name":"build_003","operations":[{"type":"create_table","table":"build_posts","columns":[{"name":"id","type":"serial","primary_key":true},{"name":"user_id","type":"integer"},{"name":"title","type":"text"}]}]}""",
            """{"name":"build_004","operations":[{"type":"create_index","name":"idx_post_user","table":"build_posts","columns":["user_id"]}]}"""
        };

        foreach (var json in migrations)
            await ApplyAsync(Migration.Deserialize(json));

        var history = await _executor.GetHistoryAsync();
        history.Should().HaveCount(4);
        history.Select(r => r.Name).Should().BeEquivalentTo(
            ["build_001", "build_002", "build_003", "build_004"]);

        (await TableExistsAsync("build_users")).Should().BeTrue();
        (await TableExistsAsync("build_posts")).Should().BeTrue();
        (await ColumnExistsAsync("build_users", "email")).Should().BeTrue();
    }

    [Fact]
    public async Task Migrate_ParentFieldTracksChain()
    {
        // Each migration's Parent field should point to the previous completed migration.
        await ApplyAsync(Migration.Deserialize("""
            {"name":"chain_001","operations":[{"type":"create_table","table":"chain_t","columns":[{"name":"id","type":"serial"}]}]}
            """));
        await ApplyAsync(Migration.Deserialize("""
            {"name":"chain_002","operations":[{"type":"add_column","table":"chain_t","column":{"name":"val","type":"text"}}]}
            """));

        var history = await _executor.GetHistoryAsync();
        history.Should().HaveCount(2);

        var m2 = history.First(r => r.Name == "chain_002");
        m2.Parent.Should().Be("chain_001", "each migration records its predecessor");
    }
}
