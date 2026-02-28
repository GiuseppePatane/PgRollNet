using System.CommandLine;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class RollbackCommand
{
    public static Command Build()
    {
        var connectionOpt = new Option<string>("--connection", "PostgreSQL connection string") { IsRequired = true };
        var schemaOpt = new Option<string>("--schema", () => "public", "Target schema name");

        var cmd = new Command("rollback", "Roll back the active migration.");
        cmd.AddOption(connectionOpt);
        cmd.AddOption(schemaOpt);

        cmd.SetHandler(async (connection, schema) =>
        {
            var executor = new PgMigrationExecutor(connection, schema);
            await executor.RollbackAsync();
            Console.WriteLine("Migration rolled back successfully.");
        }, connectionOpt, schemaOpt);

        return cmd;
    }
}
