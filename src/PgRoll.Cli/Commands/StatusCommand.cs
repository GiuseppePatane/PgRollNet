using System.CommandLine;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class StatusCommand
{
    public static Command Build()
    {
        var connectionOpt = new Option<string>("--connection", "PostgreSQL connection string") { IsRequired = true };
        var schemaOpt = new Option<string>("--schema", () => "public", "Target schema name");

        var cmd = new Command("status", "Show the currently active migration.");
        cmd.AddOption(connectionOpt);
        cmd.AddOption(schemaOpt);

        cmd.SetHandler(async (connection, schema) =>
        {
            var executor = new PgMigrationExecutor(connection, schema);
            var active = await executor.GetStatusAsync();

            if (active is null)
                Console.WriteLine("No active migration.");
            else
                Console.WriteLine($"Active migration: {active.Name} (started {active.CreatedAt:u})");
        }, connectionOpt, schemaOpt);

        return cmd;
    }
}
