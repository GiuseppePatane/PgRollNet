using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class ColumnDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("nullable")]
    public bool Nullable { get; init; } = true;

    [JsonPropertyName("default")]
    public string? Default { get; init; }

    [JsonPropertyName("primary_key")]
    public bool PrimaryKey { get; init; }

    [JsonPropertyName("unique")]
    public bool Unique { get; init; }

    [JsonPropertyName("references")]
    public string? References { get; init; }
}

public sealed class CreateTableOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "create_table";

    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("columns")]
    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }

    public string Describe() => $"create table '{Table}' ({Columns.Count} column(s))";

    public ValidationResult ValidateStructure()
    {
        if (string.IsNullOrWhiteSpace(Table))
            return ValidationResult.Failure("Table name is required.");

        if (Columns is null || Columns.Count == 0)
            return ValidationResult.Failure("At least one column is required.");

        foreach (var col in Columns)
        {
            if (string.IsNullOrWhiteSpace(col.Name))
                return ValidationResult.Failure("Column name cannot be empty.");
            if (string.IsNullOrWhiteSpace(col.Type))
                return ValidationResult.Failure($"Column '{col.Name}' type cannot be empty.");
        }

        return ValidationResult.Success;
    }

    public ValidationResult Validate(SchemaSnapshot schema)
    {
        if (string.IsNullOrWhiteSpace(Table))
            return ValidationResult.Failure("Table name is required.");

        if (Columns is null || Columns.Count == 0)
            return ValidationResult.Failure("At least one column is required.");

        if (schema.TableExists(Table))
            return ValidationResult.Failure($"Table '{Table}' already exists.");

        foreach (var col in Columns)
        {
            if (string.IsNullOrWhiteSpace(col.Name))
                return ValidationResult.Failure("Column name cannot be empty.");
            if (string.IsNullOrWhiteSpace(col.Type))
                return ValidationResult.Failure($"Column '{col.Name}' type cannot be empty.");
        }

        return ValidationResult.Success;
    }

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");
}
