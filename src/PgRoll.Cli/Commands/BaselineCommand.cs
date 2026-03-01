using System.CommandLine;

namespace PgRoll.Cli.Commands;

public static class BaselineCommand
{
    public static Command Build(GlobalOptions g)
    {
        var nameArg = new Argument<string>("name", "Name for the baseline migration.");

        var cmd = new Command("baseline",
            "Mark the current database state as a known baseline without running any operations.");
        cmd.AddArgument(nameArg);

        cmd.SetHandler(async (name, connection, schema, pgrollSchema, lockTimeout, role) =>
        {
            var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, role);
            await executor.CreateBaselineAsync(name);
            Console.WriteLine($"Baseline migration '{name}' created for schema '{schema}'.");
        }, nameArg, g.Connection, g.Schema, g.PgrollSchema, g.LockTimeout, g.Role);

        return cmd;
    }
}
