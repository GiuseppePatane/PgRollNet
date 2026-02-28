using System.CommandLine;
using Npgsql;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class ValidateCommand
{
    public static Command Build()
    {
        var connectionOpt = new Option<string>("--connection", "PostgreSQL connection string") { IsRequired = true };
        var schemaOpt = new Option<string>("--schema", () => "public", "Target schema name");
        var fileArg = new Argument<FileInfo>("file", "Path to the migration JSON file");

        var cmd = new Command("validate", "Validate a migration file without executing it.");
        cmd.AddOption(connectionOpt);
        cmd.AddOption(schemaOpt);
        cmd.AddArgument(fileArg);

        cmd.SetHandler(async (connection, schema, file) =>
        {
            var json = await File.ReadAllTextAsync(file.FullName);
            var migration = Migration.Deserialize(json);

            var reader = new PgSchemaReader(connection);
            var snapshot = await reader.ReadSchemaAsync(schema);

            var errors = new List<string>();
            foreach (var op in migration.Operations)
            {
                var result = op.Validate(snapshot);
                if (!result.IsValid)
                    errors.Add($"  [{op.Type}] {result.Error}");
            }

            if (errors.Count == 0)
                Console.WriteLine($"Migration '{migration.Name}' is valid.");
            else
            {
                Console.WriteLine($"Migration '{migration.Name}' has {errors.Count} validation error(s):");
                foreach (var err in errors)
                    Console.WriteLine(err);
                Environment.Exit(1);
            }
        }, connectionOpt, schemaOpt, fileArg);

        return cmd;
    }
}
