using System.CommandLine;
using Npgsql;

namespace PgRoll.Cli.Commands;

public static class DoctorCommand
{
    public static Command Build(GlobalOptions g)
    {
        var migrationsDir = new Option<DirectoryInfo?>(
            "--migrations",
            "Optional migrations directory used to verify applied migration checksums.");

        var cmd = new Command("doctor", "Check PostgreSQL compatibility, permissions, pgroll state, and optional migration history integrity.");
        cmd.AddOption(migrationsDir);

        cmd.SetHandler(async (dir, connection, schema, pgrollSchema) =>
        {
            var cs = g.RequireConnection(connection);
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();

            var issues = new List<string>();

            var version = (string)(await new NpgsqlCommand("SHOW server_version", conn).ExecuteScalarAsync())!;
            var currentUser = (string)(await new NpgsqlCommand("SELECT current_user", conn).ExecuteScalarAsync())!;
            var canCreate = (bool)(await new NpgsqlCommand("SELECT has_schema_privilege(current_user, $1, 'CREATE')", conn)
            {
                Parameters = { new() { Value = schema } }
            }.ExecuteScalarAsync())!;

            var schemaExists = (bool)(await new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = $1)", conn)
            {
                Parameters = { new() { Value = schema } }
            }.ExecuteScalarAsync())!;

            var pgrollExists = (bool)(await new NpgsqlCommand("""
                SELECT EXISTS (
                  SELECT 1
                  FROM information_schema.tables
                  WHERE table_schema = $1 AND table_name = 'migrations'
                )
                """, conn)
            {
                Parameters = { new() { Value = pgrollSchema } }
            }.ExecuteScalarAsync())!;

            Console.WriteLine($"server_version : {version}");
            Console.WriteLine($"current_user   : {currentUser}");
            Console.WriteLine($"target_schema  : {schema}");
            Console.WriteLine($"pgroll_schema  : {pgrollSchema}");

            if (!Version.TryParse(version.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], out var parsed) || parsed.Major < 14)
                issues.Add($"PostgreSQL {version} is below the minimum verified version 14.");
            if (!schemaExists)
                issues.Add($"Target schema '{schema}' does not exist.");
            if (!canCreate)
                issues.Add($"Current user '{currentUser}' lacks CREATE privilege on schema '{schema}'.");
            if (!pgrollExists)
                issues.Add($"pgroll state table '{pgrollSchema}.migrations' was not found. Run 'pgroll-net init'.");

            await using (var executor = g.BuildExecutor(connection, schema, pgrollSchema, 500, 0, 1000, 0, null, false))
            {
                var active = await executor.GetStatusAsync();
                if (active is not null)
                    Console.WriteLine($"active_migration: {active.Name}");
            }

            if (dir is not null && dir.Exists && pgrollExists)
            {
                await using var executor = g.BuildExecutor(connection, schema, pgrollSchema, 500, 0, 1000, 0, null, false);
                var history = await executor.GetHistoryAsync();
                var files = await MigrationDiagnostics.LoadMigrationsAsync(dir);
                var mismatches = MigrationDiagnostics.CompareChecksums(files, history);
                if (mismatches.Count > 0)
                    issues.Add($"Applied migration checksum mismatch: {string.Join(", ", mismatches.Select(m => m.Name))}");
                else
                    Console.WriteLine("history_checksums: ok");
            }

            if (issues.Count == 0)
            {
                Console.WriteLine("doctor: ok");
                return;
            }

            Console.WriteLine("doctor: issues detected");
            foreach (var issue in issues)
                Console.WriteLine($"  - {issue}");
            Environment.Exit(1);
        }, migrationsDir, g.Connection, g.Schema, g.PgrollSchema);

        return cmd;
    }
}
