using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class DropColumnOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "drop_column";

    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("column")]
    public required string Column { get; init; }

    /// <summary>Expression to write data back from the new schema to the old column on rollback.</summary>
    [JsonPropertyName("down")]
    public string? Down { get; init; }

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

        return ValidationResult.Success;
    }

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new StartResult(ctx.SchemaName, RequiresComplete: true));

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;
}
