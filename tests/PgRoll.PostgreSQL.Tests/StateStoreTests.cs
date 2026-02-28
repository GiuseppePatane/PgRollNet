using FluentAssertions;
using Npgsql;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

[Collection("Postgres")]
public class StateStoreTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_state_{Guid.NewGuid():N}";
    private PgStateStore _store = null!;

    public async Task InitializeAsync()
    {
        _ds = await DatabaseFactory.CreateIsolatedDatabaseAsync(postgres.ConnectionString, _dbName);
        _store = new PgStateStore(_ds);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _ds.DisposeAsync();
        await DatabaseFactory.DropDatabaseAsync(postgres.ConnectionString, _dbName);
    }

    [Fact]
    public async Task Init_IsIdempotent()
    {
        // Running init a second time should not throw
        await _store.InitializeAsync();
    }

    [Fact]
    public async Task GetActiveMigration_WhenNone_ReturnsNull()
    {
        var result = await _store.GetActiveMigrationAsync("public");
        result.Should().BeNull();
    }

    [Fact]
    public async Task RecordStarted_ThenGetActive_ReturnsMigration()
    {
        var record = new PgRoll.Core.State.MigrationRecord(
            Schema: "public",
            Name: "test_migration",
            MigrationJson: """{"name":"test","operations":[]}""",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Parent: null,
            Done: false
        );

        await _store.RecordStartedAsync(record);

        var active = await _store.GetActiveMigrationAsync("public");
        active.Should().NotBeNull();
        active!.Name.Should().Be("test_migration");
        active.Done.Should().BeFalse();
    }

    [Fact]
    public async Task RecordCompleted_UpdatesDoneFlag()
    {
        var record = new PgRoll.Core.State.MigrationRecord(
            "public", "m1", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false);

        await _store.RecordStartedAsync(record);
        await _store.RecordCompletedAsync("public", "m1");

        var active = await _store.GetActiveMigrationAsync("public");
        active.Should().BeNull(); // no active migration after completion

        var history = await _store.GetHistoryAsync("public");
        history.Should().ContainSingle(r => r.Name == "m1" && r.Done);
    }

    [Fact]
    public async Task DeleteRecord_RemovesMigration()
    {
        var record = new PgRoll.Core.State.MigrationRecord(
            "public", "m_delete", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false);

        await _store.RecordStartedAsync(record);
        await _store.DeleteRecordAsync("public", "m_delete");

        var history = await _store.GetHistoryAsync("public");
        history.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistory_ReturnsAllMigrationsOrdered()
    {
        await _store.RecordStartedAsync(new("public", "m1", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
        await _store.RecordCompletedAsync("public", "m1");

        await _store.RecordStartedAsync(new("public", "m2", null,
            DateTimeOffset.UtcNow.AddSeconds(1), DateTimeOffset.UtcNow, "m1", false));

        var history = await _store.GetHistoryAsync("public");
        history.Should().HaveCount(2);
        history[0].Name.Should().Be("m1");
        history[1].Name.Should().Be("m2");
        history[1].Parent.Should().Be("m1");
    }
}
