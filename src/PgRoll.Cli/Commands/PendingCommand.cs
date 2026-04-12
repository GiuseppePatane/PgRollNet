using System.CommandLine;
using System.CommandLine.Invocation;
using PgRoll.Core.Models;

namespace PgRoll.Cli.Commands;

public static class PendingCommand
{
    public static Command Build(GlobalOptions g)
    {
        var dirArg = new Argument<DirectoryInfo>("directory", "Directory containing migration JSON files");

        var cmd = new Command("pending",
            "List migration files that have not yet been applied. " +
            "Exits with code 1 if there are pending migrations, 0 if everything is up to date.");
        cmd.AddArgument(dirArg);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var dir = ctx.ParseResult.GetValueForArgument(dirArg);
            var connection = ctx.ParseResult.GetValueForOption(g.Connection);
            var schema = ctx.ParseResult.GetValueForOption(g.Schema)!;
            var pgrollSchema = ctx.ParseResult.GetValueForOption(g.PgrollSchema)!;
            var lockTimeout = ctx.ParseResult.GetValueForOption(g.LockTimeout);
            var statementTimeout = ctx.ParseResult.GetValueForOption(g.StatementTimeout);
            var backfillBatchSize = ctx.ParseResult.GetValueForOption(g.BackfillBatchSize);
            var backfillDelayMs = ctx.ParseResult.GetValueForOption(g.BackfillDelayMs);
            var role = ctx.ParseResult.GetValueForOption(g.Role);
            var verbose = ctx.ParseResult.GetValueForOption(g.Verbose);
            if (!dir.Exists)
            {
                Console.Error.WriteLine($"error: directory not found: {dir.FullName}");
                Environment.Exit(2);
                return;
            }

            var files = dir.GetFiles("*.json")
                .Concat(dir.GetFiles("*.yaml"))
                .Concat(dir.GetFiles("*.yml"))
                .OrderBy(f => f.Name)
                .ToList();

            if (files.Count == 0)
            {
                Console.WriteLine("No migration files found.");
                // Exit 0 — nothing to apply is not an error
                return;
            }

            var loadedMigrations = new List<(FileInfo File, Migration Migration)>();
            foreach (var file in files)
                loadedMigrations.Add((file, await Migration.LoadAsync(file.FullName)));

            await using var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, statementTimeout, backfillBatchSize, backfillDelayMs, role, verbose);
            var history = await executor.GetHistoryAsync();
            var mismatches = MigrationDiagnostics.CompareChecksums(loadedMigrations, history);
            if (mismatches.Count > 0)
                throw new PgRoll.Core.Errors.PgRollException($"Checksum mismatch for applied migration(s): {string.Join(", ", mismatches.Select(m => m.Name))}");
            var applied = history.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

            var pending = loadedMigrations
                .Where(x => !applied.Contains(x.Migration.Name))
                .Select(x => x.File)
                .ToList();

            if (pending.Count == 0)
            {
                Console.WriteLine("Up to date. No pending migrations.");
                // Exit 0 — CD can skip the migration step
                return;
            }

            Console.WriteLine($"Pending migrations ({pending.Count}):");
            foreach (var f in pending)
                Console.WriteLine($"  {f.Name}");

            // Exit 1 — signals the CD pipeline that migrations must be applied
            Environment.Exit(1);

        });

        return cmd;
    }
}
