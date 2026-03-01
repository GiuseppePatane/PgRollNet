using System.CommandLine;

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

        cmd.SetHandler(async (dir, connection, schema, pgrollSchema, lockTimeout, role) =>
        {
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

            var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, role);
            var history  = await executor.GetHistoryAsync();
            var applied  = history.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

            var pending = files
                .Where(f => !applied.Contains(Path.GetFileNameWithoutExtension(f.Name)))
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

        }, dirArg, g.Connection, g.Schema, g.PgrollSchema, g.LockTimeout, g.Role);

        return cmd;
    }
}
