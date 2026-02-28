using System.CommandLine;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class StartCommand
{
    public static Command Build()
    {
        var connectionOpt = new Option<string>("--connection", "PostgreSQL connection string") { IsRequired = true };
        var schemaOpt = new Option<string>("--schema", () => "public", "Target schema name");
        var fileArg = new Argument<FileInfo>("file", "Path to the migration JSON file");

        var cmd = new Command("start", "Start a migration from a JSON file.");
        cmd.AddOption(connectionOpt);
        cmd.AddOption(schemaOpt);
        cmd.AddArgument(fileArg);

        cmd.SetHandler(async (connection, schema, file) =>
        {
            var json = await File.ReadAllTextAsync(file.FullName);
            var migration = Migration.Deserialize(json);
            var executor = new PgMigrationExecutor(connection, schema);
            await executor.StartAsync(migration);
            Console.WriteLine($"Migration '{migration.Name}' started. Run 'pgroll complete' to finalize.");
        }, connectionOpt, schemaOpt, fileArg);

        return cmd;
    }
}
