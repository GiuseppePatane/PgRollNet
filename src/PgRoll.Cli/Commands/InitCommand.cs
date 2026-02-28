using System.CommandLine;
using Npgsql;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class InitCommand
{
    public static Command Build()
    {
        var connectionOpt = new Option<string>("--connection", "PostgreSQL connection string") { IsRequired = true };
        var schemaOpt = new Option<string>("--schema", () => "public", "Target schema name");

        var cmd = new Command("init", "Initialize the pgroll state schema in the target database.");
        cmd.AddOption(connectionOpt);
        cmd.AddOption(schemaOpt);

        cmd.SetHandler(async (connection, schema) =>
        {
            var executor = new PgMigrationExecutor(connection, schema);
            await executor.InitializeAsync();
            Console.WriteLine("pgroll initialized successfully.");
        }, connectionOpt, schemaOpt);

        return cmd;
    }
}
