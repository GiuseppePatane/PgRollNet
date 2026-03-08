using System.CommandLine;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class StartCommand
{
    public static Command Build(GlobalOptions g)
    {
        var dryRunOpt = new Option<bool>("--dry-run", "Validate and describe the migration without executing it");
        var fileArg = new Argument<FileInfo>("file", "Path to the migration JSON file");

        var cmd = new Command("start", "Start a migration from a JSON file.");
        cmd.AddOption(dryRunOpt);
        cmd.AddArgument(fileArg);

        cmd.SetHandler(async (dryRun, file, connection, schema, pgrollSchema, lockTimeout, role) =>
        {
            var migration = await Migration.LoadAsync(file.FullName);

            if (dryRun)
            {
                await RunDryRunAsync(connection, schema, migration);
                return;
            }

            Console.WriteLine($"Starting migration '{migration.Name}' ({migration.Operations.Count} operation(s))...");
            for (var i = 0; i < migration.Operations.Count; i++)
                Console.WriteLine($"  [{i + 1}/{migration.Operations.Count}] {migration.Operations[i].Describe()}");
            Console.WriteLine();

            var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, role);

            long lastTotal = 0;
            string? lastTable = null;
            executor.BackfillProgress = new Progress<BackfillProgress>(p =>
            {
                lastTotal = p.TotalRowsUpdated;
                lastTable = p.Table;
                var line = $"  Backfilling {p.Table}: batch {p.BatchNumber}, {p.TotalRowsUpdated:N0} rows updated...";
                Console.Write($"\r{Pad(line)}");
            });

            await executor.StartAsync(migration);

            if (lastTable is not null)
            {
                Console.WriteLine($"\r{Pad($"  ✓ Backfill complete: {lastTotal:N0} rows updated on {lastTable}.")}");
                Console.WriteLine();
            }

            Console.WriteLine($"✓ Migration '{migration.Name}' started. Run 'pgroll-net complete' to finalize.");

        }, dryRunOpt, fileArg, g.Connection, g.Schema, g.PgrollSchema, g.LockTimeout, g.Role);

        return cmd;
    }

    private static async Task RunDryRunAsync(string? connection, string schema, Migration migration)
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

        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.WriteLine("Structural validation passed (no --connection provided; skipping online validation).");
            return;
        }

        // Online validation against the live schema
        var reader = new PgSchemaReader(connection);
        var snapshot = await reader.ReadSchemaAsync(schema);

        var errors = new List<string>();
        for (var i = 0; i < migration.Operations.Count; i++)
        {
            var op = migration.Operations[i];
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
            foreach (var e in errors)
                Console.WriteLine(e);
            Environment.Exit(1);
        }
    }

    private static string Pad(string line)
    {
        try
        { return line.PadRight(Console.WindowWidth - 1); }
        catch (IOException) { return line; }
    }
}
