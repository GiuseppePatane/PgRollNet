using System.CommandLine;

namespace PgRoll.Cli.Commands;

public static class LatestCommand
{
    public static Command Build(GlobalOptions g)
    {
        var cmd = new Command("latest", "Print the name of the most recently applied migration.");

        cmd.SetHandler(async (connection, schema, pgrollSchema, lockTimeout, role) =>
        {
            var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, role);
            var history = await executor.GetHistoryAsync();
            var latest = history.LastOrDefault();

            if (latest is null)
            {
                Console.Error.WriteLine("No migrations found.");
                Environment.Exit(1);
                return;
            }

            Console.WriteLine(latest.Name);
        }, g.Connection, g.Schema, g.PgrollSchema, g.LockTimeout, g.Role);

        return cmd;
    }
}
