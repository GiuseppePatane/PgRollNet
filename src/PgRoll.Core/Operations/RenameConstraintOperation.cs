using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class RenameConstraintOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "rename_constraint";

    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    public ValidationResult Validate(SchemaSnapshot schema)
    {
        if (string.IsNullOrWhiteSpace(Table))
            return ValidationResult.Failure("Table name is required.");

        if (!schema.TableExists(Table))
            return ValidationResult.Failure($"Table '{Table}' does not exist.");

        if (string.IsNullOrWhiteSpace(From))
            return ValidationResult.Failure("Source constraint name ('from') is required.");

        if (!schema.ConstraintExists(Table, From))
            return ValidationResult.Failure($"Constraint '{From}' does not exist on table '{Table}'.");

        if (string.IsNullOrWhiteSpace(To))
            return ValidationResult.Failure("Target constraint name ('to') is required.");

        if (schema.ConstraintExists(Table, To))
            return ValidationResult.Failure($"Constraint '{To}' already exists on table '{Table}'.");

        return ValidationResult.Success;
    }

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new StartResult(ctx.SchemaName, RequiresComplete: true));

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;
}
