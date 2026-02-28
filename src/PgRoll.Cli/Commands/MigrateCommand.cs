using System.CommandLine;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class MigrateCommand
{
    public static Command Build()
    {
        var connectionOpt = new Option<string>("--connection", "PostgreSQL connection string") { IsRequired = true };
        var schemaOpt = new Option<string>("--schema", () => "public", "Target schema name");
        var dirArg = new Argument<DirectoryInfo>("directory", "Directory containing migration JSON files");

        var cmd = new Command("migrate", "Apply all pending migrations from a directory.");
        cmd.AddOption(connectionOpt);
        cmd.AddOption(schemaOpt);
        cmd.AddArgument(dirArg);

        cmd.SetHandler(async (connection, schema, dir) =>
        {
            var executor = new PgMigrationExecutor(connection, schema);

            var migrationFiles = dir.GetFiles("*.json")
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
            foreach (var file in pending)
            {
                var json = await File.ReadAllTextAsync(file.FullName);
                var migration = Migration.Deserialize(json);
                Console.WriteLine($"  Applying '{migration.Name}'...");
                await executor.StartAsync(migration);
                await executor.CompleteAsync();
                Console.WriteLine($"  Done: '{migration.Name}'");
            }

            Console.WriteLine("All migrations applied.");
        }, connectionOpt, schemaOpt, dirArg);

        return cmd;
    }
}
