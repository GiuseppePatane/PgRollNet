using System.CommandLine;

namespace PgRoll.Cli.Commands;

public static class StatusCommand
{
    public static Command Build(GlobalOptions g)
    {
        var cmd = new Command("status", "Show the currently active migration.");

        cmd.SetHandler(async (connection, schema, pgrollSchema, lockTimeout, role, verbose) =>
        {
            await using var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, role, verbose);
            var active = await executor.GetStatusAsync();

            if (active is null)
                Console.WriteLine("No active migration.");
            else
                Console.WriteLine($"Active migration: {active.Name} (started {active.CreatedAt:u})");
        }, g.Connection, g.Schema, g.PgrollSchema, g.LockTimeout, g.Role, g.Verbose);

        return cmd;
    }
}
