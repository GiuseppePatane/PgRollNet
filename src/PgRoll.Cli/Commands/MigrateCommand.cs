using System.CommandLine;
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

        cmd.SetHandler(async (dir, skipOnError, connection, schema, pgrollSchema, lockTimeout, role) =>
        {
            var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, role);

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

            var history = await executor.GetHistoryAsync();
            var applied = history.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

            var pending = migrationFiles
                .Where(f => !applied.Contains(Path.GetFileNameWithoutExtension(f.Name)))
                .ToList();

            if (pending.Count == 0)
            {
                Console.WriteLine("All migrations already applied.");
                return;
            }

            Console.WriteLine($"Applying {pending.Count} migration(s)...");
            var failed = new List<string>();
            foreach (var file in pending)
            {
                var migration = await Migration.LoadAsync(file.FullName);
                if (migration.Operations.Count == 0)
                {
                    Console.WriteLine($"  Skipping '{migration.Name}' (no operations)");
                    continue;
                }
                Console.WriteLine($"  Applying '{migration.Name}'...");
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
        }, dirArg, continueOnError, g.Connection, g.Schema, g.PgrollSchema, g.LockTimeout, g.Role);

        return cmd;
    }
}
