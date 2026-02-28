using System.CommandLine;
using PgRoll.Cli.EfCore;

namespace PgRoll.Cli.Commands;

public static class EfCoreCommand
{
    public static Command Build()
    {
        var efCore = new Command("efcore", "EF Core integration commands.");
        efCore.AddCommand(BuildConvert());
        return efCore;
    }

    // ── pgroll efcore convert ─────────────────────────────────────────────────

    private static Command BuildConvert()
    {
        var assemblyOpt = new Option<FileInfo>(
            "--assembly",
            "Path to the compiled EF Core migrations assembly (.dll).")
        { IsRequired = true };

        var outputOpt = new Option<DirectoryInfo>(
            "--output",
            () => new DirectoryInfo("pgroll-migrations"),
            "Output directory for pgroll JSON files (created if it does not exist).");

        var filterOpt = new Option<string?>(
            "--filter",
            "Only convert migrations whose name contains this string (case-insensitive).");

        var cmd = new Command(
            "convert",
            "Discover all EF Core migrations in a compiled assembly and convert them to pgroll JSON files.");

        cmd.AddOption(assemblyOpt);
        cmd.AddOption(outputOpt);
        cmd.AddOption(filterOpt);

        cmd.SetHandler((assembly, output, filter) =>
        {
            if (!assembly.Exists)
            {
                Console.Error.WriteLine($"error: assembly not found: {assembly.FullName}");
                Environment.Exit(1);
            }

            output.Create();

            Console.WriteLine($"Assembly : {assembly.FullName}");
            Console.WriteLine($"Output   : {Path.GetFullPath(output.FullName)}");
            Console.WriteLine();

            IReadOnlyList<LoadedMigration> migrations;
            try
            {
                using var loader = MigrationAssemblyLoader.Create(assembly.FullName);
                migrations = loader.LoadMigrations();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: failed to load assembly — {ex.Message}");
                Environment.Exit(1);
                return;
            }

            if (filter is not null)
                migrations = migrations
                    .Where(m => m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (migrations.Count == 0)
            {
                Console.WriteLine(filter is null
                    ? "No EF Core migrations found in the assembly."
                    : $"No migrations matching '{filter}'.");
                return;
            }

            Console.WriteLine($"Found {migrations.Count} migration(s)\n");

            var written = 0;
            var totalSkipped = 0;
            var allSkippedTypes = new HashSet<string>();

            foreach (var m in migrations)
            {
                var result = ReflectionConverter.Convert(m.Name, m.UpOperations);

                var path = Path.Combine(output.FullName, $"{m.Name}.json");
                File.WriteAllText(path, result.Migration.Serialize());
                written++;

                totalSkipped += result.Skipped.Count;
                foreach (var s in result.Skipped)
                    allSkippedTypes.Add(s);

                var opsLabel = result.Migration.Operations.Count switch
                {
                    0 => "(no schema ops)",
                    1 => $"1 op  → {result.Migration.Operations[0].Type}",
                    var n => $"{n} ops"
                };

                Console.Write($"  ✓  {m.Name}  [{opsLabel}]");

                if (result.Skipped.Count > 0)
                {
                    var grouped = result.Skipped
                        .GroupBy(s => s)
                        .Select(g => g.Count() == 1 ? g.Key : $"{g.Key}×{g.Count()}");
                    Console.Write($"  skip: {string.Join(", ", grouped)}");
                }

                Console.WriteLine();

                // Print each pgroll operation on a sub-line when there are several
                if (result.Migration.Operations.Count > 1)
                    foreach (var op in result.Migration.Operations)
                        Console.WriteLine($"          • {op.Type}");
            }

            Console.WriteLine();
            Console.WriteLine($"Written  : {written} file(s) → {Path.GetFullPath(output.FullName)}");

            if (allSkippedTypes.Count > 0)
            {
                Console.WriteLine($"Skipped  : {totalSkipped} unsupported operation(s)" +
                    $" ({string.Join(", ", allSkippedTypes.Order())})");
                Console.WriteLine("           Sql ops (stored procedures, raw DDL) have no pgroll equivalent.");
            }

        }, assemblyOpt, outputOpt, filterOpt);

        return cmd;
    }
}
