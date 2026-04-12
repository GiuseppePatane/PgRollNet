using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using PgRoll.Core.Models;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli.Commands;

public static class InspectActiveCommand
{
    public static Command Build(GlobalOptions g)
    {
        var jsonOpt = new Option<bool>("--json", "Emit JSON instead of text.");
        var cmd = new Command("inspect-active", "Inspect the currently active migration and print recovery-relevant details.");
        cmd.AddOption(jsonOpt);

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            var connection = ctx.ParseResult.GetValueForOption(g.Connection);
            var schema = ctx.ParseResult.GetValueForOption(g.Schema)!;
            var pgrollSchema = ctx.ParseResult.GetValueForOption(g.PgrollSchema)!;
            var lockTimeout = ctx.ParseResult.GetValueForOption(g.LockTimeout);
            var statementTimeout = ctx.ParseResult.GetValueForOption(g.StatementTimeout);
            var backfillBatchSize = ctx.ParseResult.GetValueForOption(g.BackfillBatchSize);
            var backfillDelayMs = ctx.ParseResult.GetValueForOption(g.BackfillDelayMs);
            var role = ctx.ParseResult.GetValueForOption(g.Role);
            var verbose = ctx.ParseResult.GetValueForOption(g.Verbose);
            await using var executor = g.BuildExecutor(connection, schema, pgrollSchema, lockTimeout, statementTimeout, backfillBatchSize, backfillDelayMs, role, verbose);
            var active = await executor.GetStatusAsync();

            if (active is null)
            {
                Console.WriteLine(json ? "{}" : "No active migration.");
                return;
            }

            var migration = Migration.Deserialize(active.MigrationJson!);
            var versionSchema = PgVersionSchemaManager.VersionSchemaName(schema, active.Name);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    active.Name,
                    active.Schema,
                    active.MigrationChecksum,
                    active.CreatedAt,
                    active.Parent,
                    versionSchema,
                    operations = migration.Operations.Select(op => new { type = op.Type, description = op.Describe() })
                }, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            Console.WriteLine($"Active migration : {active.Name}");
            Console.WriteLine($"Schema           : {active.Schema}");
            Console.WriteLine($"Started          : {active.CreatedAt:O}");
            Console.WriteLine($"Parent           : {active.Parent ?? "(none)"}");
            Console.WriteLine($"Checksum         : {active.MigrationChecksum ?? "(none)"}");
            Console.WriteLine($"Version schema   : {versionSchema}");
            Console.WriteLine("Operations:");
            foreach (var op in migration.Operations)
                Console.WriteLine($"  - {op.Describe()}");
        });

        return cmd;
    }
}
