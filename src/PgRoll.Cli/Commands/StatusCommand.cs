using System.CommandLine;
using System.CommandLine.Invocation;

namespace PgRoll.Cli.Commands;

public static class StatusCommand
{
    public static Command Build(GlobalOptions g)
    {
        var cmd = new Command("status", "Show the currently active migration.");

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
            var active = await executor.GetStatusAsync();

            if (active is null)
                Console.WriteLine("No active migration.");
            else
                Console.WriteLine($"Active migration: {active.Name} (started {active.CreatedAt:u})");
        });

        return cmd;
    }
}
