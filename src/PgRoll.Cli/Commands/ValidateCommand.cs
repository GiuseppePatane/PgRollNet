using System.CommandLine;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class ValidateCommand
{
    public static Command Build(GlobalOptions g)
    {
        var offlineOpt = new Option<bool>("--offline", "Validate required fields only, without connecting to the database");
        var fileArg    = new Argument<FileInfo>("file", "Path to the migration JSON file");

        var cmd = new Command("validate", "Validate a migration file without executing it.");
        cmd.AddOption(offlineOpt);
        cmd.AddArgument(fileArg);

        cmd.SetHandler(async (offline, file, connection, schema, pgrollSchema, lockTimeout, role) =>
        {
            if (!file.Exists)
            {
                await Console.Error.WriteLineAsync($"error: file not found: {file.FullName}");
                Environment.Exit(2);
                return;
            }

            if (!offline && string.IsNullOrWhiteSpace(connection))
            {
                await Console.Error.WriteLineAsync("error: --connection is required unless --offline is specified.");
                Environment.Exit(2);
                return;
            }

            Migration migration;
            try
            {
                migration = await Migration.LoadAsync(file.FullName);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"error: failed to parse migration file — {ex.Message}");
                Environment.Exit(1);
                return;
            }

            var errors = new List<string>();

            if (offline)
            {
                // Structural validation only — no DB required
                foreach (var op in migration.Operations)
                {
                    var result = op.ValidateStructure();
                    if (!result.IsValid)
                        errors.Add($"  [{op.Type}] {result.Error}");
                }
            }
            else
            {
                // Full validation against live schema
                var reader   = new PgSchemaReader(connection!);
                var snapshot = await reader.ReadSchemaAsync(schema);

                foreach (var op in migration.Operations)
                {
                    var result = op.Validate(snapshot);
                    if (!result.IsValid)
                        errors.Add($"  [{op.Type}] {result.Error}");
                }
            }

            if (errors.Count == 0)
            {
                var mode = offline ? " (offline)" : "";
                Console.WriteLine($"Migration '{migration.Name}' is valid{mode}.");
            }
            else
            {
                Console.WriteLine($"Migration '{migration.Name}' has {errors.Count} validation error(s):");
                foreach (var err in errors)
                    Console.WriteLine(err);
                Environment.Exit(1);
            }
        }, offlineOpt, fileArg, g.Connection, g.Schema, g.PgrollSchema, g.LockTimeout, g.Role);

        return cmd;
    }
}
