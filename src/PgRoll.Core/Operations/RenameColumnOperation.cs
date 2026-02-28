using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class RenameColumnOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "rename_column";

    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    public string Describe() => $"rename column '{From}' \u2192 '{To}' in '{Table}'";

    public ValidationResult ValidateStructure()
    {
        if (string.IsNullOrWhiteSpace(Table))
            return ValidationResult.Failure("Table name is required.");
        if (string.IsNullOrWhiteSpace(From))
            return ValidationResult.Failure("Source column name ('from') is required.");
        if (string.IsNullOrWhiteSpace(To))
            return ValidationResult.Failure("Target column name ('to') is required.");
        return ValidationResult.Success;
    }

    public ValidationResult Validate(SchemaSnapshot schema)
    {
        if (string.IsNullOrWhiteSpace(Table))
            return ValidationResult.Failure("Table name is required.");

        if (!schema.TableExists(Table))
            return ValidationResult.Failure($"Table '{Table}' does not exist.");

        if (string.IsNullOrWhiteSpace(From))
            return ValidationResult.Failure("Source column name ('from') is required.");

        if (string.IsNullOrWhiteSpace(To))
            return ValidationResult.Failure("Target column name ('to') is required.");

        if (!schema.ColumnExists(Table, From))
            return ValidationResult.Failure($"Column '{From}' does not exist in table '{Table}'.");

        if (schema.ColumnExists(Table, To))
            return ValidationResult.Failure($"Column '{To}' already exists in table '{Table}'.");

        return ValidationResult.Success;
    }

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new StartResult(ctx.SchemaName, RequiresComplete: true));

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;
}
