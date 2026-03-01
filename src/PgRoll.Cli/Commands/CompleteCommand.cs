using System.CommandLine;

namespace PgRoll.Cli.Commands;

public static class CompleteCommand
{
    public static Command Build(GlobalOptions g)
    {
        var cmd = new Command("complete", "Complete the active migration.");

        cmd.SetHandler(async (connection, schema, pgrollSchema, lockTimeout, role) =>
        {
            var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, role);
            await executor.CompleteAsync();
            Console.WriteLine("Migration completed successfully.");
        }, g.Connection, g.Schema, g.PgrollSchema, g.LockTimeout, g.Role);

        return cmd;
    }
}
