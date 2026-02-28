using FluentAssertions;
using Npgsql;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Tests for PgBackfillBatcher and PgTriggerManager.
/// </summary>
[Collection("Postgres")]
public class BackfillAndTriggerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private NpgsqlConnection _conn = null!;
    private readonly string _dbName = $"pgroll_bftest_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        _ds = await DatabaseFactory.CreateIsolatedDatabaseAsync(postgres.ConnectionString, _dbName);
        _conn = await _ds.OpenConnectionAsync();
    }

    public async Task DisposeAsync()
    {
        await _conn.DisposeAsync();
        await _ds.DisposeAsync();
        await DatabaseFactory.DropDatabaseAsync(postgres.ConnectionString, _dbName);
    }

    // ── Backfill ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_NullRows_AreUpdated()
    {
        await ExecAsync("CREATE TABLE words (id serial PRIMARY KEY, word text, upper_word text)");
        await ExecAsync("INSERT INTO words (word) VALUES ('hello'), ('world'), ('foo')");

        var total = await PgBackfillBatcher.BackfillAsync(
            _ds, "public", "words", "upper_word", "UPPER(word)");

        total.Should().Be(3);

        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM words WHERE upper_word IS NOT NULL", _conn);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(3);
    }

    [Fact]
    public async Task Backfill_EmptyTable_Returns0()
    {
        await ExecAsync("CREATE TABLE empty_t (id serial PRIMARY KEY, val text, computed text)");

        var total = await PgBackfillBatcher.BackfillAsync(
            _ds, "public", "empty_t", "computed", "UPPER(val)");

        total.Should().Be(0);
    }

    [Fact]
    public async Task Backfill_AlreadyFilledRows_AreSkipped()
    {
        await ExecAsync("CREATE TABLE partial (id serial PRIMARY KEY, val text, comp text)");
        await ExecAsync("INSERT INTO partial (val, comp) VALUES ('a', 'A'), ('b', NULL)");

        var total = await PgBackfillBatcher.BackfillAsync(
            _ds, "public", "partial", "comp", "UPPER(val)");

        total.Should().Be(1);

        await using var cmd = new NpgsqlCommand("SELECT comp FROM partial WHERE val = 'b'", _conn);
        var result = (string?)(await cmd.ExecuteScalarAsync());
        result.Should().Be("B");
    }

    [Fact]
    public async Task Backfill_BatchSizeRespected()
    {
        await ExecAsync("CREATE TABLE batched (id serial PRIMARY KEY, v int, computed int)");
        for (var i = 1; i <= 5; i++)
            await ExecAsync($"INSERT INTO batched (v) VALUES ({i})");

        var total = await PgBackfillBatcher.BackfillAsync(
            _ds, "public", "batched", "computed", "v * 2", batchSize: 2);

        total.Should().Be(5);
    }

    // ── Trigger ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTrigger_PropagatesInsert()
    {
        await ExecAsync("CREATE TABLE trigtest (id serial PRIMARY KEY, name text, name_upper text)");
        // Version schema exposes original cols except "name" (replaced by name_upper alias)
        await PgVersionSchemaManager.CreateVersionSchemaAsync(_conn, "public", "m_trigger", "trigtest",
            ["\"id\"", "\"name_upper\" AS \"name\""]);

        await PgTriggerManager.CreateTriggerAsync(_conn, "public", "trigtest", "name",
            "name_upper", "UPPER(name)", "public_m_trigger");

        await ExecAsync("INSERT INTO public.trigtest (name) VALUES ('alice')");

        await using var cmd = new NpgsqlCommand("SELECT name_upper FROM trigtest WHERE name = 'alice'", _conn);
        var result = (string?)(await cmd.ExecuteScalarAsync());
        result.Should().Be("ALICE");
    }

    [Fact]
    public async Task CreateTrigger_PropagatesUpdate()
    {
        await ExecAsync("CREATE TABLE trigupdate (id serial PRIMARY KEY, val text, val_up text)");
        // Version schema exposes original cols except "val" (replaced by val_up alias)
        await PgVersionSchemaManager.CreateVersionSchemaAsync(_conn, "public", "m_trigupdate", "trigupdate",
            ["\"id\"", "\"val_up\" AS \"val\""]);

        await PgTriggerManager.CreateTriggerAsync(_conn, "public", "trigupdate", "val",
            "val_up", "UPPER(val)", "public_m_trigupdate");

        await ExecAsync("INSERT INTO public.trigupdate (val) VALUES ('old')");
        await ExecAsync("UPDATE public.trigupdate SET val = 'new'");

        await using var cmd = new NpgsqlCommand("SELECT val_up FROM trigupdate WHERE val = 'new'", _conn);
        var result = (string?)(await cmd.ExecuteScalarAsync());
        result.Should().Be("NEW");
    }

    [Fact]
    public async Task DropTrigger_RemovesTriggerAndFunction()
    {
        await ExecAsync("CREATE TABLE trigtodrop (id serial PRIMARY KEY, x text, y text)");
        await PgVersionSchemaManager.CreateVersionSchemaAsync(_conn, "public", "m_droptr", "trigtodrop",
            ["\"id\"", "\"y\" AS \"x\""]);

        await PgTriggerManager.CreateTriggerAsync(_conn, "public", "trigtodrop", "x",
            "y", "UPPER(x)", "public_m_droptr");
        await PgTriggerManager.DropTriggerAsync(_conn, "public", "trigtodrop", "x");

        // After drop, trigger should not exist
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.triggers WHERE trigger_schema='public' AND event_object_table='trigtodrop'",
            _conn);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(0);
    }

    private async Task ExecAsync(string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, _conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
