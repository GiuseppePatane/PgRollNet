using System.CommandLine;

namespace PgRoll.Cli.Commands;

public static class PullCommand
{
    public static Command Build(GlobalOptions g)
    {
        var dirArg = new Argument<DirectoryInfo>("directory", "Directory to write migration JSON files into");

        var cmd = new Command("pull", "Write completed migration history to JSON files.");
        cmd.AddArgument(dirArg);

        cmd.SetHandler(async (dir, connection, schema, pgrollSchema, lockTimeout, role) =>
        {
            var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, role);
            var history = await executor.GetHistoryAsync();
            var completed = history.Where(r => r.Done).ToList();

            if (completed.Count == 0)
            {
                Console.WriteLine("No completed migrations found.");
                return;
            }

            dir.Create();

            var written = 0;
            foreach (var record in completed)
            {
                if (record.MigrationJson is null)
                    continue;
                var fileName = Path.Combine(dir.FullName, $"{record.Name}.json");
                await File.WriteAllTextAsync(fileName, record.MigrationJson);
                written++;
            }

            Console.WriteLine($"Written {written} migration file(s) to '{dir.FullName}'.");
        }, dirArg, g.Connection, g.Schema, g.PgrollSchema, g.LockTimeout, g.Role);

        return cmd;
    }
}
