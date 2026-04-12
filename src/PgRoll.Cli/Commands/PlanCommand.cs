using System.CommandLine;
using System.Text.Json;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class PlanCommand
{
    public static Command Build(GlobalOptions g)
    {
        var fileArg = new Argument<FileInfo>("file", "Path to the migration file.");
        var formatOpt = new Option<string>("--format", () => "text", "Output format: text or json.");

        var cmd = new Command("plan", "Render a machine-readable or text plan for a migration, including warnings and online validation when possible.");
        cmd.AddArgument(fileArg);
        cmd.AddOption(formatOpt);

        cmd.SetHandler(async (file, format, connection, schema) =>
        {
            var migration = await Migration.LoadAsync(file.FullName);
            var warnings = MigrationDiagnostics.GetWarnings(migration).Distinct().ToList();
            var operations = migration.Operations.Select((op, idx) => new
            {
                index = idx + 1,
                type = op.Type,
                description = op.Describe(),
                requiresConcurrentConnection = op.RequiresConcurrentConnection
            }).ToList();

            bool? onlineValid = null;
            List<string>? validationErrors = null;
            if (!string.IsNullOrWhiteSpace(connection))
            {
                await using var reader = new PgSchemaReader(connection);
                var snapshot = await reader.ReadSchemaAsync(schema);
                validationErrors = migration.Operations
                    .Select(op => (op, result: op.Validate(snapshot)))
                    .Where(x => !x.result.IsValid)
                    .Select(x => $"[{x.op.Type}] {x.result.Error}")
                    .ToList();
                onlineValid = validationErrors.Count == 0;
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    migration = migration.Name,
                    operations,
                    warnings,
                    onlineValid,
                    validationErrors
                }, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            Console.WriteLine($"Migration: {migration.Name}");
            Console.WriteLine($"Operations: {operations.Count}");
            foreach (var op in operations)
                Console.WriteLine($"  [{op.index}] {op.description}{(op.requiresConcurrentConnection ? " [concurrent]" : "")}");

            if (warnings.Count > 0)
            {
                Console.WriteLine("Warnings:");
                foreach (var warning in warnings)
                    Console.WriteLine($"  - {warning}");
            }

            if (onlineValid is not null)
            {
                Console.WriteLine(onlineValid.Value ? "Online validation: ok" : "Online validation: failed");
                if (validationErrors is not null)
                    foreach (var error in validationErrors)
                        Console.WriteLine($"  - {error}");
            }
        }, fileArg, formatOpt, g.Connection, g.Schema);

        return cmd;
    }
}
