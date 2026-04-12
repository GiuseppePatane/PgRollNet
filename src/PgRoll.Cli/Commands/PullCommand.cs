using System.CommandLine;
using System.CommandLine.Invocation;

namespace PgRoll.Cli.Commands;

public static class PullCommand
{
    public static Command Build(GlobalOptions g)
    {
        var dirArg = new Argument<DirectoryInfo>("directory", "Directory to write migration JSON files into");

        var cmd = new Command("pull", "Write completed migration history to JSON files.");
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
            await using var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, statementTimeout, backfillBatchSize, backfillDelayMs, role, verbose);
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
        });

        return cmd;
    }
}
