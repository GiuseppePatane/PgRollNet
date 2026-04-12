using System.CommandLine;
using System.CommandLine.Invocation;

namespace PgRoll.Cli.Commands;

public static class BaselineCommand
{
    public static Command Build(GlobalOptions g)
    {
        var nameArg = new Argument<string>("name", "Name for the baseline migration.");

        var cmd = new Command("baseline",
            "Mark the current database state as a known baseline without running any operations.");
        cmd.AddArgument(nameArg);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var name = ctx.ParseResult.GetValueForArgument(nameArg);
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
            await executor.CreateBaselineAsync(name);
            Console.WriteLine($"Baseline migration '{name}' created for schema '{schema}'.");
        });

        return cmd;
    }
}
