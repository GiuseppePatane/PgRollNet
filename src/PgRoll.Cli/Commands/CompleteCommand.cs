using System.CommandLine;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class CompleteCommand
{
    public static Command Build()
    {
        var connectionOpt = new Option<string>("--connection", "PostgreSQL connection string") { IsRequired = true };
        var schemaOpt = new Option<string>("--schema", () => "public", "Target schema name");

        var cmd = new Command("complete", "Complete the active migration.");
        cmd.AddOption(connectionOpt);
        cmd.AddOption(schemaOpt);

        cmd.SetHandler(async (connection, schema) =>
        {
            var executor = new PgMigrationExecutor(connection, schema);
            await executor.CompleteAsync();
            Console.WriteLine("Migration completed successfully.");
        }, connectionOpt, schemaOpt);

        return cmd;
    }
}
