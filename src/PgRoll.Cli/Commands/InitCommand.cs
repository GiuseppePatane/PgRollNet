using System.CommandLine;

namespace PgRoll.Cli.Commands;

public static class InitCommand
{
    public static Command Build(GlobalOptions g)
    {
        var cmd = new Command("init", "Initialize the pgroll state schema in the target database.");

        cmd.SetHandler(async (connection, schema, pgrollSchema, lockTimeout, role, verbose) =>
        {
            await using var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, role, verbose);
            await executor.InitializeAsync();
            Console.WriteLine("pgroll initialized successfully.");
        }, g.Connection, g.Schema, g.PgrollSchema, g.LockTimeout, g.Role, g.Verbose);

        return cmd;
    }
}
