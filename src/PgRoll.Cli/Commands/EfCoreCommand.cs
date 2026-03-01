using System.CommandLine;
using PgRoll.Cli.EfCore;
using PgRoll.Core.Models;

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

            // Load the full migration list first so we can assign positions that
            // reflect the real EF Core history order — even when --filter is used.
            IReadOnlyList<LoadedMigration> allMigrations;
            try
            {
                using var loader = MigrationAssemblyLoader.Create(assembly.FullName);
                allMigrations = loader.LoadMigrations();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: failed to load assembly — {ex.Message}");
                Environment.Exit(1);
                return;
            }

            // Map each migration name → its 1-based position in the full EF Core history.
            var positionMap = allMigrations
                .Select((m, i) => (m.Name, pos: i + 1))
                .ToDictionary(x => x.Name, x => x.pos);

            var migrations = filter is null
                ? allMigrations
                : allMigrations
                    .Where(m => m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (migrations.Count == 0)
            {
                Console.WriteLine(filter is null
                    ? "No EF Core migrations found in the assembly."
                    : $"No migrations matching '{filter}'.");
                return;
            }

            var filterNote = filter is null ? "" : $" (filtered from {allMigrations.Count} total)";
            Console.WriteLine($"Found {migrations.Count} migration(s){filterNote}\n");

            var written = 0;
            var totalSkipped = 0;
            var allSkippedTypes = new HashSet<string>();

            foreach (var m in migrations)
            {
                var position = positionMap[m.Name];
                var result = ReflectionConverter.Convert(m.Name, m.UpOperations);

                // Prefix both the filename and the migration name with the zero-padded
                // position so that alphabetical sort always yields the correct apply order,
                // regardless of EF Core naming convention (timestamp or not).
                var orderedName = $"{position:D4}_{m.Name}";
                var orderedMigration = new Migration { Name = orderedName, Operations = result.Migration.Operations };

                var path = Path.Combine(output.FullName, $"{orderedName}.json");
                File.WriteAllText(path, orderedMigration.Serialize());
                written++;

                totalSkipped += result.Skipped.Count;
                foreach (var s in result.Skipped)
                    allSkippedTypes.Add(s);

                var opsLabel = orderedMigration.Operations.Count switch
                {
                    0 => "(no schema ops)",
                    1 => $"1 op  → {orderedMigration.Operations[0].Type}",
                    var n => $"{n} ops"
                };

                Console.Write($"  ✓  [{position:D4}] {m.Name}  [{opsLabel}]");

                if (result.Skipped.Count > 0)
                {
                    var grouped = result.Skipped
                        .GroupBy(s => s)
                        .Select(g => g.Count() == 1 ? g.Key : $"{g.Key}×{g.Count()}");
                    Console.Write($"  skip: {string.Join(", ", grouped)}");
                }

                Console.WriteLine();

                // Print each pgroll operation on a sub-line when there are several
                if (orderedMigration.Operations.Count > 1)
                    foreach (var op in orderedMigration.Operations)
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
