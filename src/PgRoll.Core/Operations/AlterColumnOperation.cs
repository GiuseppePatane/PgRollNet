using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class AlterColumnOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "alter_column";

    [JsonPropertyName("table")]
    public required string Table { get; init; }

    /// <summary>Original column name.</summary>
    [JsonPropertyName("column")]
    public required string Column { get; init; }

    /// <summary>Expression computing the new column value from old data (required when DataType changes).</summary>
    [JsonPropertyName("up")]
    public string? Up { get; init; }

    /// <summary>Expression computing the old column value from new data (for back-compat writes).</summary>
    [JsonPropertyName("down")]
    public string? Down { get; init; }

    /// <summary>Rename the column to this name on complete.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Change the column data type.</summary>
    [JsonPropertyName("data_type")]
    public string? DataType { get; init; }

    /// <summary>Add or remove NOT NULL constraint.</summary>
    [JsonPropertyName("not_null")]
    public bool? NotNull { get; init; }

    /// <summary>Add a UNIQUE constraint on the column.</summary>
    [JsonPropertyName("unique")]
    public bool? Unique { get; init; }

    /// <summary>Set a new column default expression.</summary>
    [JsonPropertyName("default")]
    public string? Default { get; init; }

    /// <summary>Add a CHECK constraint expression.</summary>
    [JsonPropertyName("check")]
    public string? Check { get; init; }

    /// <summary>Always uses autocommit — backfill uses dataSource connections and Unique may use CONCURRENTLY.</summary>
    public bool RequiresConcurrentConnection => true;

    public string Describe() => $"alter column '{Column}' in '{Table}'{(Name is not null ? $" \u2192 '{Name}'" : "")}{(DataType is not null ? $" type:{DataType}" : "")}";

    public ValidationResult ValidateStructure()
    {
        if (string.IsNullOrWhiteSpace(Table))
            return ValidationResult.Failure("Table name is required.");
        if (string.IsNullOrWhiteSpace(Column))
            return ValidationResult.Failure("Column name is required.");
        return ValidationResult.Success;
    }

    public ValidationResult Validate(SchemaSnapshot schema)
    {
        if (string.IsNullOrWhiteSpace(Table))
            return ValidationResult.Failure("Table name is required.");

        if (!schema.TableExists(Table))
            return ValidationResult.Failure($"Table '{Table}' does not exist.");

        if (string.IsNullOrWhiteSpace(Column))
            return ValidationResult.Failure("Column name is required.");

        if (!schema.ColumnExists(Table, Column))
            return ValidationResult.Failure($"Column '{Column}' does not exist in table '{Table}'.");

        if (Name is not null && schema.ColumnExists(Table, Name))
            return ValidationResult.Failure($"Column '{Name}' already exists in table '{Table}'.");

        return ValidationResult.Success;
    }

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");
}
