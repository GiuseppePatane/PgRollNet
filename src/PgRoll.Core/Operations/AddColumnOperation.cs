using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class AddColumnOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "add_column";

    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("column")]
    public required ColumnDefinition Column { get; init; }

    /// <summary>
    /// Expression to compute the new column value from existing row data (enables expand/contract pattern).
    /// When null, column is added directly without backfill.
    /// </summary>
    [JsonPropertyName("up")]
    public string? Up { get; init; }

    /// <summary>
    /// Expression to write back from the new column to the old representation (optional).
    /// </summary>
    [JsonPropertyName("down")]
    public string? Down { get; init; }

    /// <summary>Requires autocommit when Up is specified (backfill uses dataSource connections).</summary>
    public bool RequiresConcurrentConnection => Up is not null;

    public ValidationResult Validate(SchemaSnapshot schema)
    {
        if (string.IsNullOrWhiteSpace(Table))
            return ValidationResult.Failure("Table name is required.");

        if (!schema.TableExists(Table))
            return ValidationResult.Failure($"Table '{Table}' does not exist.");

        if (string.IsNullOrWhiteSpace(Column.Name))
            return ValidationResult.Failure("Column name is required.");

        if (string.IsNullOrWhiteSpace(Column.Type))
            return ValidationResult.Failure($"Column '{Column.Name}' type cannot be empty.");

        if (schema.ColumnExists(Table, Column.Name))
            return ValidationResult.Failure($"Column '{Column.Name}' already exists in table '{Table}'.");

        return ValidationResult.Success;
    }

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");
}
