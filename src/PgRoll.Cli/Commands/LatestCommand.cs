using System.CommandLine;
using System.CommandLine.Invocation;

namespace PgRoll.Cli.Commands;

public static class LatestCommand
{
    public static Command Build(GlobalOptions g)
    {
        var cmd = new Command("latest", "Print the name of the most recently applied migration.");

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
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
            var latest = history.LastOrDefault();

            if (latest is null)
            {
                Console.Error.WriteLine("No migrations found.");
                Environment.Exit(1);
                return;
            }

            Console.WriteLine(latest.Name);
        });

        return cmd;
    }
}
