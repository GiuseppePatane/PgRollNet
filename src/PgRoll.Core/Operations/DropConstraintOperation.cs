using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class DropConstraintOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "drop_constraint";

    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    public string Describe() => $"drop constraint '{Name}' from '{Table}'";

    public ValidationResult ValidateStructure()
    {
        if (string.IsNullOrWhiteSpace(Table))
            return ValidationResult.Failure("Table name is required.");
        if (string.IsNullOrWhiteSpace(Name))
            return ValidationResult.Failure("Constraint name is required.");
        return ValidationResult.Success;
    }

    public ValidationResult Validate(SchemaSnapshot schema)
    {
        if (string.IsNullOrWhiteSpace(Table))
            return ValidationResult.Failure("Table name is required.");

        if (!schema.TableExists(Table))
            return ValidationResult.Failure($"Table '{Table}' does not exist.");

        if (string.IsNullOrWhiteSpace(Name))
            return ValidationResult.Failure("Constraint name is required.");

        // Constraint already absent (e.g. cascade-dropped by a prior alter_column) — treat as no-op.
        if (!schema.ConstraintExists(Table, Name))
            return ValidationResult.Success;

        return ValidationResult.Success;
    }

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new StartResult(ctx.SchemaName, RequiresComplete: true));

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;
}
