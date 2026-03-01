using System.CommandLine;

namespace PgRoll.Cli.Commands;

public static class RollbackCommand
{
    public static Command Build(GlobalOptions g)
    {
        var cmd = new Command("rollback", "Roll back the active migration.");

        cmd.SetHandler(async (connection, schema, pgrollSchema, lockTimeout, role) =>
        {
            var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, role);
            await executor.RollbackAsync();
            Console.WriteLine("Migration rolled back successfully.");
        }, g.Connection, g.Schema, g.PgrollSchema, g.LockTimeout, g.Role);

        return cmd;
    }
}
