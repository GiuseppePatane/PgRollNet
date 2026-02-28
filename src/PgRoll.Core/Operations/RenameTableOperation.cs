using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class RenameTableOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "rename_table";

    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    public ValidationResult Validate(SchemaSnapshot schema)
    {
        if (string.IsNullOrWhiteSpace(From))
            return ValidationResult.Failure("Source table name ('from') is required.");

        if (string.IsNullOrWhiteSpace(To))
            return ValidationResult.Failure("Target table name ('to') is required.");

        if (!schema.TableExists(From))
            return ValidationResult.Failure($"Table '{From}' does not exist.");

        if (schema.TableExists(To))
            return ValidationResult.Failure($"Table '{To}' already exists.");

        return ValidationResult.Success;
    }

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new StartResult(ctx.SchemaName, RequiresComplete: true));

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;
}
