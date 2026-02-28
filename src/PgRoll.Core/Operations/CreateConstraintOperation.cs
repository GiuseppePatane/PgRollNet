using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class CreateConstraintOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "create_constraint";

    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>"check", "unique", or "foreign_key"</summary>
    [JsonPropertyName("constraint_type")]
    public required string ConstraintType { get; init; }

    /// <summary>CHECK expression (required when constraint_type = "check").</summary>
    [JsonPropertyName("check")]
    public string? Check { get; init; }

    /// <summary>Columns for UNIQUE constraint.</summary>
    [JsonPropertyName("columns")]
    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>Referenced table for foreign key.</summary>
    [JsonPropertyName("references_table")]
    public string? ReferencesTable { get; init; }

    /// <summary>Referenced columns for foreign key.</summary>
    [JsonPropertyName("references_columns")]
    public IReadOnlyList<string>? ReferencesColumns { get; init; }

    public ValidationResult Validate(SchemaSnapshot schema)
    {
        if (string.IsNullOrWhiteSpace(Table))
            return ValidationResult.Failure("Table name is required.");

        if (!schema.TableExists(Table))
            return ValidationResult.Failure($"Table '{Table}' does not exist.");

        if (string.IsNullOrWhiteSpace(Name))
            return ValidationResult.Failure("Constraint name is required.");

        if (schema.ConstraintExists(Table, Name))
            return ValidationResult.Failure($"Constraint '{Name}' already exists on table '{Table}'.");

        return ConstraintType switch
        {
            "check" when string.IsNullOrWhiteSpace(Check) =>
                ValidationResult.Failure("'check' expression is required for check constraints."),
            "unique" when Columns is null || Columns.Count == 0 =>
                ValidationResult.Failure("'columns' is required for unique constraints."),
            "foreign_key" when string.IsNullOrWhiteSpace(ReferencesTable) =>
                ValidationResult.Failure("'references_table' is required for foreign key constraints."),
            "check" or "unique" or "foreign_key" => ValidationResult.Success,
            _ => ValidationResult.Failure($"Unknown constraint_type '{ConstraintType}'.")
        };
    }

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");
}
