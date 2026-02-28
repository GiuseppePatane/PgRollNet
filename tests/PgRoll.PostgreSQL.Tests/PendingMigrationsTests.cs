using FluentAssertions;
using Npgsql;
using PgRoll.Core.Models;
using PgRoll.Core.State;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Integration tests for the pending-migration detection logic used by <c>pgroll pending</c>.
/// Tests use <see cref="PgMigrationExecutor.GetHistoryAsync"/> directly, replicating the same
/// set-comparison logic as PendingCommand without going through the CLI layer.
/// </summary>
[Collection("Postgres")]
public class PendingMigrationsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_pending_{Guid.NewGuid():N}";
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

    /// Mirrors the set-comparison in PendingCommand: file base-name vs applied migration names.
    private static IReadOnlyList<string> DetectPending(
        IEnumerable<string> fileNames,
        IReadOnlyList<MigrationRecord> history)
    {
        var applied = history.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        return fileNames
            .Where(n => !applied.Contains(Path.GetFileNameWithoutExtension(n)))
            .ToList();
    }

    // ── GetHistoryAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetHistory_FreshDb_ReturnsEmpty()
    {
        var history = await _executor.GetHistoryAsync();
        history.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistory_AfterStartComplete_ReturnsDoneRecord()
    {
        var m = Migration.Deserialize("""
            {"name":"hist_m1","operations":[{"type":"create_table","table":"hist_tbl","columns":[{"name":"id","type":"serial"}]}]}
            """);
        await _executor.StartAsync(m);
        await _executor.CompleteAsync();

        var history = await _executor.GetHistoryAsync();
        history.Should().HaveCount(1);
        history[0].Name.Should().Be("hist_m1");
        history[0].Done.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistory_ActiveMigration_IsIncludedNotDone()
    {
        var m = Migration.Deserialize("""
            {"name":"hist_active","operations":[{"type":"create_table","table":"hist_active_tbl","columns":[{"name":"id","type":"serial"}]}]}
            """);
        await _executor.StartAsync(m);
        // intentionally NOT completing

        var history = await _executor.GetHistoryAsync();
        history.Should().HaveCount(1);
        history[0].Name.Should().Be("hist_active");
        history[0].Done.Should().BeFalse();
    }

    [Fact]
    public async Task GetHistory_MultipleCompleted_ReturnsAll()
    {
        var m1 = Migration.Deserialize("""
            {"name":"hist_seq1","operations":[{"type":"create_table","table":"hist_seq_tbl","columns":[{"name":"id","type":"serial"}]}]}
            """);
        var m2 = Migration.Deserialize("""
            {"name":"hist_seq2","operations":[{"type":"add_column","table":"hist_seq_tbl","column":{"name":"name","type":"text"}}]}
            """);

        await _executor.StartAsync(m1);
        await _executor.CompleteAsync();
        await _executor.StartAsync(m2);
        await _executor.CompleteAsync();

        var history = await _executor.GetHistoryAsync();
        history.Should().HaveCount(2);
        history.Should().Contain(r => r.Name == "hist_seq1" && r.Done);
        history.Should().Contain(r => r.Name == "hist_seq2" && r.Done);
    }

    // ── pending detection logic ───────────────────────────────────────────────

    [Fact]
    public async Task PendingLogic_NothingApplied_AllFilesPending()
    {
        var history = await _executor.GetHistoryAsync();

        var files = new[] { "001_create_users.json", "002_add_email.json" };
        var pending = DetectPending(files, history);

        pending.Should().HaveCount(2);
        pending.Should().Contain("001_create_users.json");
        pending.Should().Contain("002_add_email.json");
    }

    [Fact]
    public async Task PendingLogic_AllApplied_NoPending()
    {
        var m1 = Migration.Deserialize("""
            {"name":"001_create_pend_users","operations":[{"type":"create_table","table":"pend_users","columns":[{"name":"id","type":"serial"}]}]}
            """);
        var m2 = Migration.Deserialize("""
            {"name":"002_add_pend_email","operations":[{"type":"add_column","table":"pend_users","column":{"name":"email","type":"text"}}]}
            """);

        await _executor.StartAsync(m1);
        await _executor.CompleteAsync();
        await _executor.StartAsync(m2);
        await _executor.CompleteAsync();

        var history = await _executor.GetHistoryAsync();
        var pending = DetectPending(
            ["001_create_pend_users.json", "002_add_pend_email.json"],
            history);

        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task PendingLogic_PartiallyApplied_CorrectPendingList()
    {
        var m1 = Migration.Deserialize("""
            {"name":"001_create_orders","operations":[{"type":"create_table","table":"pend_orders","columns":[{"name":"id","type":"serial"}]}]}
            """);
        await _executor.StartAsync(m1);
        await _executor.CompleteAsync();

        var history = await _executor.GetHistoryAsync();
        var pending = DetectPending(
            ["001_create_orders.json", "002_add_status.json", "003_add_index.json"],
            history);

        pending.Should().HaveCount(2);
        pending.Should().NotContain("001_create_orders.json");
        pending.Should().Contain("002_add_status.json");
        pending.Should().Contain("003_add_index.json");
    }

    [Fact]
    public async Task PendingLogic_NameComparison_IsCaseSensitive()
    {
        var m = Migration.Deserialize("""
            {"name":"MyMigration","operations":[{"type":"create_table","table":"case_tbl","columns":[{"name":"id","type":"serial"}]}]}
            """);
        await _executor.StartAsync(m);
        await _executor.CompleteAsync();

        var history = await _executor.GetHistoryAsync();

        // Wrong case → still pending (comparison is ordinal / case-sensitive)
        DetectPending(["mymigration.json"], history).Should().HaveCount(1);

        // Correct case → not pending
        DetectPending(["MyMigration.json"], history).Should().BeEmpty();
    }

    [Fact]
    public async Task PendingLogic_EmptyFileList_NoPending()
    {
        var history = await _executor.GetHistoryAsync();
        var pending = DetectPending([], history);
        pending.Should().BeEmpty();
    }
}
