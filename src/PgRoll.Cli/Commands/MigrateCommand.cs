using System.CommandLine;
using System.CommandLine.Invocation;
using PgRoll.Core.Models;

namespace PgRoll.Cli.Commands;

public static class MigrateCommand
{
    public static Command Build(GlobalOptions g)
    {
        var dirArg = new Argument<DirectoryInfo>("directory", "Directory containing migration JSON files");
        var continueOnError = new Option<bool>("--continue-on-error", "Skip failed migrations and continue instead of stopping.");

        var cmd = new Command("migrate", "Apply all pending migrations from a directory.");
        cmd.AddArgument(dirArg);
        cmd.AddOption(continueOnError);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var dir = ctx.ParseResult.GetValueForArgument(dirArg);
            var skipOnError = ctx.ParseResult.GetValueForOption(continueOnError);
            var connection = ctx.ParseResult.GetValueForOption(g.Connection);
            var schema = ctx.ParseResult.GetValueForOption(g.Schema)!;
            var pgrollSchema = ctx.ParseResult.GetValueForOption(g.PgrollSchema)!;
            var lockTimeout = ctx.ParseResult.GetValueForOption(g.LockTimeout);
            var statementTimeout = ctx.ParseResult.GetValueForOption(g.StatementTimeout);
            var backfillBatchSize = ctx.ParseResult.GetValueForOption(g.BackfillBatchSize);
            var backfillDelayMs = ctx.ParseResult.GetValueForOption(g.BackfillDelayMs);
            var role = ctx.ParseResult.GetValueForOption(g.Role);
            var verbose = ctx.ParseResult.GetValueForOption(g.Verbose);
            await using var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, statementTimeout, backfillBatchSize, backfillDelayMs, role, verbose);

            var migrationFiles = dir.GetFiles("*.json")
                .Concat(dir.GetFiles("*.yaml"))
                .Concat(dir.GetFiles("*.yml"))
                .OrderBy(f => f.Name)
                .ToList();

            if (migrationFiles.Count == 0)
            {
                Console.WriteLine("No migration files found.");
                return;
            }

            var loadedMigrations = new List<(FileInfo File, Migration Migration)>();
            foreach (var file in migrationFiles)
                loadedMigrations.Add((file, await Migration.LoadAsync(file.FullName)));

            var history = await executor.GetHistoryAsync();
            var mismatches = MigrationDiagnostics.CompareChecksums(loadedMigrations, history);
            if (mismatches.Count > 0)
                throw new PgRoll.Core.Errors.PgRollException($"Checksum mismatch for applied migration(s): {string.Join(", ", mismatches.Select(m => m.Name))}");
            var applied = history.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

            var pending = loadedMigrations
                .Where(x => !applied.Contains(x.Migration.Name))
                .ToList();

            if (pending.Count == 0)
            {
                Console.WriteLine("All migrations already applied.");
                return;
            }

            Console.WriteLine($"Applying {pending.Count} migration(s)...");
            var failed = new List<string>();
            foreach (var (_, migration) in pending)
            {
                if (migration.Operations.Count == 0)
                {
                    Console.WriteLine($"  Skipping '{migration.Name}' (no operations)");
                    continue;
                }
                Console.WriteLine($"  Applying '{migration.Name}'...");
                foreach (var warning in MigrationDiagnostics.GetWarnings(migration).Distinct())
                    Console.WriteLine($"  Warning: {warning}");
                try
                {
                    await executor.StartAsync(migration);
                    await executor.CompleteAsync();
                    Console.WriteLine($"  Done: '{migration.Name}'");
                }
                catch (Exception ex) when (skipOnError)
                {
                    Console.Error.WriteLine($"  WARNING: '{migration.Name}' failed: {ex.Message}");
                    failed.Add(migration.Name);
                    // StartAsync atomicity cleans up on partial failure;
                    // if CompleteAsync failed the active migration is still in state — rollback it.
                    try
                    { await executor.RollbackAsync(); }
                    catch (Exception rollbackEx)
                    {
                        Console.Error.WriteLine($"  WARNING: Rollback also failed: {rollbackEx.Message}");
                    }
                }
            }

            if (failed.Count > 0)
                Console.WriteLine($"Finished with {failed.Count} skipped migration(s): {string.Join(", ", failed)}");
            else
                Console.WriteLine("All migrations applied.");
        });

        return cmd;
    }
}
