using System.CommandLine;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class PullCommand
{
    public static Command Build()
    {
        var connectionOpt = new Option<string>("--connection", "PostgreSQL connection string") { IsRequired = true };
        var schemaOpt = new Option<string>("--schema", () => "public", "Target schema name");
        var dirArg = new Argument<DirectoryInfo>("directory", "Directory to write migration JSON files into");

        var cmd = new Command("pull", "Write completed migration history to JSON files.");
        cmd.AddOption(connectionOpt);
        cmd.AddOption(schemaOpt);
        cmd.AddArgument(dirArg);

        cmd.SetHandler(async (connection, schema, dir) =>
        {
            var executor = new PgMigrationExecutor(connection, schema);
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
                if (record.MigrationJson is null) continue;
                var fileName = Path.Combine(dir.FullName, $"{record.Name}.json");
                await File.WriteAllTextAsync(fileName, record.MigrationJson);
                written++;
            }

            Console.WriteLine($"Written {written} migration file(s) to '{dir.FullName}'.");
        }, connectionOpt, schemaOpt, dirArg);

        return cmd;
    }
}
