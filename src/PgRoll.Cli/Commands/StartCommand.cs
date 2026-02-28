using System.CommandLine;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class StartCommand
{
    public static Command Build()
    {
        var connectionOpt = new Option<string>("--connection", "PostgreSQL connection string") { IsRequired = true };
        var schemaOpt     = new Option<string>("--schema", () => "public", "Target schema name");
        var dryRunOpt     = new Option<bool>("--dry-run", "Validate and describe the migration without executing it");
        var fileArg       = new Argument<FileInfo>("file", "Path to the migration JSON file");

        var cmd = new Command("start", "Start a migration from a JSON file.");
        cmd.AddOption(connectionOpt);
        cmd.AddOption(schemaOpt);
        cmd.AddOption(dryRunOpt);
        cmd.AddArgument(fileArg);

        cmd.SetHandler(async (connection, schema, dryRun, file) =>
        {
            var json = await File.ReadAllTextAsync(file.FullName);
            var migration = Migration.Deserialize(json);

            if (dryRun)
            {
                await RunDryRunAsync(connection, schema, migration);
                return;
            }

            var executor = new PgMigrationExecutor(connection, schema);
            await executor.StartAsync(migration);
            Console.WriteLine($"Migration '{migration.Name}' started. Run 'pgroll complete' to finalize.");

        }, connectionOpt, schemaOpt, dryRunOpt, fileArg);

        return cmd;
    }

    private static async Task RunDryRunAsync(string connection, string schema, Migration migration)
    {
        Console.WriteLine($"Dry run: '{migration.Name}' ({migration.Operations.Count} operation(s)) — no changes will be made.");
        Console.WriteLine();

        // Structural validation (offline — no DB required)
        var structuralErrors = migration.Operations
            .Select(op => (op, r: op.ValidateStructure()))
            .Where(x => !x.r.IsValid)
            .ToList();

        if (structuralErrors.Count > 0)
        {
            Console.WriteLine("Structural validation FAILED:");
            foreach (var (op, r) in structuralErrors)
                Console.WriteLine($"  [{op.Type}] {r.Error}");
            Environment.Exit(1);
            return;
        }

        // Online validation against the live schema
        var reader   = new PgSchemaReader(connection);
        var snapshot = await reader.ReadSchemaAsync(schema);

        var errors = new List<string>();
        for (var i = 0; i < migration.Operations.Count; i++)
        {
            var op     = migration.Operations[i];
            var result = op.Validate(snapshot);
            var status = result.IsValid ? "[OK  ]" : "[FAIL]";
            Console.WriteLine($"  {status} [{i + 1}/{migration.Operations.Count}] {op.Describe()}");
            if (!result.IsValid)
                errors.Add($"              {result.Error}");
        }

        Console.WriteLine();
        if (errors.Count == 0)
        {
            Console.WriteLine($"Validation passed — ready to apply '{migration.Name}'.");
        }
        else
        {
            Console.WriteLine($"Validation failed ({errors.Count} error(s)):");
            foreach (var e in errors) Console.WriteLine(e);
            Environment.Exit(1);
        }
    }
}
