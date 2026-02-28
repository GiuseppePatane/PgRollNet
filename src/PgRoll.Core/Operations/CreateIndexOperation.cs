using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class CreateIndexOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "create_index";

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("columns")]
    public required IReadOnlyList<string> Columns { get; init; }

    [JsonPropertyName("unique")]
    public bool Unique { get; init; }

    public bool RequiresConcurrentConnection => true;

    public ValidationResult Validate(SchemaSnapshot schema)
    {
        if (string.IsNullOrWhiteSpace(Name))
            return ValidationResult.Failure("Index name is required.");

        if (string.IsNullOrWhiteSpace(Table))
            return ValidationResult.Failure("Table name is required.");

        if (Columns is null || Columns.Count == 0)
            return ValidationResult.Failure("At least one column is required for an index.");

        if (!schema.TableExists(Table))
            return ValidationResult.Failure($"Table '{Table}' does not exist.");

        if (schema.IndexExists(Name))
            return ValidationResult.Failure($"Index '{Name}' already exists.");

        foreach (var col in Columns)
        {
            if (!schema.ColumnExists(Table, col))
                return ValidationResult.Failure($"Column '{col}' does not exist in table '{Table}'.");
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
