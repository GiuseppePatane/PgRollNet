using FluentAssertions;
using Npgsql;
using PgRoll.Core.Errors;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Integration tests for the advisory-lock concurrent-safety mechanism in
/// <see cref="PgMigrationExecutor.StartAsync"/>.
///
/// <c>pg_try_advisory_lock(hashtext('pgroll'), hashtext(schema))</c> is acquired at the
/// top of <c>StartAsync</c>. If the lock is already held (another process is in the
/// middle of a start), the call throws <see cref="MigrationLockError"/> immediately.
/// </summary>
[Collection("Postgres")]
public class ConcurrentSafetyTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_concur_{Guid.NewGuid():N}";
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

    /// Acquires the same advisory lock that StartAsync uses for the given schema.
    private static async Task<NpgsqlConnection> HoldAdvisoryLockAsync(
        NpgsqlDataSource ds, string schema)
    {
        var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtext('pgroll'), hashtext($1))", conn);
        cmd.Parameters.AddWithValue(schema);
        await cmd.ExecuteScalarAsync();
        return conn;  // caller must dispose to release the lock
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_LockAlreadyHeld_ThrowsMigrationLockError()
    {
        // Hold the advisory lock manually — simulates another process mid-start.
        await using var lockConn = await HoldAdvisoryLockAsync(_ds, "public");

        var migration = Migration.Deserialize("""
            {"name":"concur_blocked","operations":[
              {"type":"create_table","table":"concur_t1","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);

        Func<Task> act = () => _executor.StartAsync(migration);
        await act.Should().ThrowAsync<MigrationLockError>()
            .WithMessage("*public*");
    }

    [Fact]
    public async Task StartAsync_AfterLockReleased_Succeeds()
    {
        // Hold the lock, try (fails), release the lock, retry (succeeds).
        var lockConn = await HoldAdvisoryLockAsync(_ds, "public");

        var migration = Migration.Deserialize("""
            {"name":"concur_retry","operations":[
              {"type":"create_table","table":"concur_retry_t","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);

        // First attempt: blocked → MigrationLockError
        await Assert.ThrowsAsync<MigrationLockError>(() => _executor.StartAsync(migration));

        // Explicitly unlock before disposing — Npgsql connection pooling keeps
        // the session alive, so session-level advisory locks persist until
        // pg_advisory_unlock is called (dispose alone is insufficient).
        await using var unlockCmd = new NpgsqlCommand(
            "SELECT pg_advisory_unlock(hashtext('pgroll'), hashtext($1))", lockConn);
        unlockCmd.Parameters.AddWithValue("public");
        await unlockCmd.ExecuteScalarAsync();
        await lockConn.DisposeAsync();

        // Second attempt: lock is now free → should succeed
        Func<Task> act2 = () => _executor.StartAsync(migration);
        await act2.Should().NotThrowAsync();

        await _executor.CompleteAsync();
    }

    [Fact]
    public async Task StartAsync_DifferentSchemas_DoNotBlockEachOther()
    {
        // Holding the lock for schema "alpha" must not prevent StartAsync on "beta".
        await using var alphaConn = await _ds.OpenConnectionAsync();
        await using var alphaLockCmd = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtext('pgroll'), hashtext('alpha'))", alphaConn);
        await alphaLockCmd.ExecuteScalarAsync();

        // Create "beta" schema and executor
        await using (var conn = await _ds.OpenConnectionAsync())
        {
            await using var createCmd = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS beta", conn);
            await createCmd.ExecuteNonQueryAsync();
        }

        var betaExecutor = new PgMigrationExecutor(_ds, "beta");
        await betaExecutor.InitializeAsync();

        var migration = Migration.Deserialize("""
            {"name":"beta_migration","operations":[
              {"type":"create_table","table":"beta_t","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);

        // beta schema must not be blocked by alpha's lock
        Func<Task> act = () => betaExecutor.StartAsync(migration);
        await act.Should().NotThrowAsync("different schema locks are independent");

        await betaExecutor.CompleteAsync();
    }

    [Fact]
    public async Task StartAsync_ConcurrentAttempts_OnlyOneSucceeds()
    {
        // Both tasks start simultaneously; advisory lock ensures only one proceeds.
        var migration1 = Migration.Deserialize("""
            {"name":"concur_race","operations":[
              {"type":"create_table","table":"concur_race_t","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);
        var migration2 = Migration.Deserialize("""
            {"name":"concur_race","operations":[
              {"type":"create_table","table":"concur_race_t","columns":[{"name":"id","type":"serial"}]}
            ]}
            """);

        var executor2 = new PgMigrationExecutor(_ds);  // second "process"

        var results = await Task.WhenAll(
            TryStartAsync(_executor, migration1),
            TryStartAsync(executor2, migration2));

        var successes = results.Count(r => r);
        var failures  = results.Count(r => !r);

        // Exactly one should have won the lock race.
        successes.Should().Be(1, "advisory lock must allow only one concurrent start");
        failures.Should().Be(1);

        // Clean up
        await _executor.CompleteAsync();
    }

    private static async Task<bool> TryStartAsync(PgMigrationExecutor exec, Migration migration)
    {
        try
        {
            await exec.StartAsync(migration);
            return true;
        }
        catch (Exception ex) when (
            ex is MigrationLockError
            or PgRoll.Core.Errors.MigrationAlreadyActiveError
            or PgRoll.Core.Errors.MigrationNameConflictError)
        {
            return false;
        }
    }
}
