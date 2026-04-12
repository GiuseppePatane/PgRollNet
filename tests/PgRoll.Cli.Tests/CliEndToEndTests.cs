using System.Diagnostics;
using System.Text;
using FluentAssertions;
using PgRoll.Cli;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL;
using Testcontainers.PostgreSql;

namespace PgRoll.Cli.Tests;

[Collection("Postgres")]
public class CliEndToEndTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string _dbName = $"pgroll_cli_{Guid.NewGuid():N}";
    private Npgsql.NpgsqlDataSource _ds = null!;
    private PgMigrationExecutor _executor = null!;
    private string _tempDir = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _ds = await DatabaseFactory.CreateIsolatedDatabaseAsync(postgres.ConnectionString, _dbName);
        _connectionString = new Npgsql.NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = _dbName
        }.ConnectionString;
        _executor = new PgMigrationExecutor(_ds);
        await _executor.InitializeAsync();

        _tempDir = Path.Combine(Path.GetTempPath(), "pgroll-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);

        await _ds.DisposeAsync();
        await DatabaseFactory.DropDatabaseAsync(postgres.ConnectionString, _dbName);
    }

    [Fact]
    public async Task ValidateOffline_WithValidMigration_ExitsZero()
    {
        var file = Path.Combine(_tempDir, "valid.json");
        await File.WriteAllTextAsync(file, """
            {
              "name": "0001_valid",
              "operations": [
                { "type": "create_table", "table": "users", "columns": [{ "name": "id", "type": "serial" }] }
              ]
            }
            """);

        var result = await RunCliAsync("validate", "--offline", file);

        result.ExitCode.Should().Be(0, $"stdout: {result.StdOut}\nstderr: {result.StdErr}");
        result.StdOut.Should().Contain("Migration '0001_valid' is valid");
        result.StdErr.Should().BeEmpty();
    }

    [Fact]
    public async Task Pending_UsesMigrationNameInsteadOfFileName()
    {
        var migration = Migration.Deserialize("""
            {
              "name": "0001_real_name",
              "operations": [
                { "type": "create_table", "table": "cli_pending_table", "columns": [{ "name": "id", "type": "serial" }] }
              ]
            }
            """);
        await _executor.StartAsync(migration);
        await _executor.CompleteAsync();

        var file = Path.Combine(_tempDir, "renamed_file.json");
        await File.WriteAllTextAsync(file, """
            {
              "name": "0001_real_name",
              "operations": []
            }
            """);

        var result = await RunCliAsync(
            "pending",
            _tempDir,
            "--connection",
            _connectionString);

        result.ExitCode.Should().Be(0, $"stdout: {result.StdOut}\nstderr: {result.StdErr}");
        result.StdOut.Should().Contain("Up to date. No pending migrations.");
    }

    [Fact]
    public async Task Migrate_DoesNotReapplyMigrationWhenOnlyFileNameDiffers()
    {
        var migration = Migration.Deserialize("""
            {
              "name": "0002_already_applied",
              "operations": [
                { "type": "create_table", "table": "cli_migrate_table", "columns": [{ "name": "id", "type": "serial" }] }
              ]
            }
            """);
        await _executor.StartAsync(migration);
        await _executor.CompleteAsync();

        var file = Path.Combine(_tempDir, "different_filename.json");
        await File.WriteAllTextAsync(file, """
            {
              "name": "0002_already_applied",
              "operations": [
                { "type": "create_table", "table": "cli_migrate_table", "columns": [{ "name": "id", "type": "serial" }] }
              ]
            }
            """);

        var result = await RunCliAsync(
            "migrate",
            _tempDir,
            "--connection",
            _connectionString);

        result.ExitCode.Should().Be(0, $"stdout: {result.StdOut}\nstderr: {result.StdErr}");
        result.StdOut.Should().Contain("All migrations already applied.");
    }

    [Fact]
    public async Task Init_WithVerbose_EmitsExecutorLogs()
    {
        var dbName = $"pgroll_cli_verbose_{Guid.NewGuid():N}";
        await using var initDs = await DatabaseFactory.CreateIsolatedDatabaseAsync(postgres.ConnectionString, dbName);
        var initConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = dbName
        }.ConnectionString;

        var result = await RunCliAsync(
            "init",
            "--connection",
            initConnectionString,
            "--verbose");

        result.ExitCode.Should().Be(0, $"stdout: {result.StdOut}\nstderr: {result.StdErr}");
        result.StdOut.Should().Contain("Initializing pgroll state schema.");
        result.StdOut.Should().Contain("pgroll initialized successfully.");

        await DatabaseFactory.DropDatabaseAsync(postgres.ConnectionString, dbName);
    }

    private static async Task<CliResult> RunCliAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(typeof(GlobalOptions).Assembly.Location);
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        return new CliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr);
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly string PostgresImage =
        Environment.GetEnvironmentVariable("PGROLL_TEST_POSTGRES_IMAGE") ?? "postgres:17-alpine";

    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder()
            .WithImage(PostgresImage)
            .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}

internal static class DatabaseFactory
{
    public static async Task<Npgsql.NpgsqlDataSource> CreateIsolatedDatabaseAsync(
        string adminConnectionString, string dbName, CancellationToken ct = default)
    {
        await using var adminConn = new Npgsql.NpgsqlConnection(adminConnectionString);
        await adminConn.OpenAsync(ct);

        await using (var dropCmd = new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\"", adminConn))
            await dropCmd.ExecuteNonQueryAsync(ct);

        await using (var createCmd = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", adminConn))
            await createCmd.ExecuteNonQueryAsync(ct);

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(adminConnectionString) { Database = dbName };
        return Npgsql.NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public static async Task DropDatabaseAsync(
        string adminConnectionString, string dbName, CancellationToken ct = default)
    {
        await using var adminConn = new Npgsql.NpgsqlConnection(adminConnectionString);
        await adminConn.OpenAsync(ct);

        await using (var killCmd = new Npgsql.NpgsqlCommand(
            $"""
            SELECT pg_terminate_backend(pg_stat_activity.pid)
            FROM pg_stat_activity
            WHERE pg_stat_activity.datname = '{dbName}'
              AND pid <> pg_backend_pid()
            """, adminConn))
            await killCmd.ExecuteNonQueryAsync(ct);

        await using (var dropCmd = new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\"", adminConn))
            await dropCmd.ExecuteNonQueryAsync(ct);
    }
}
