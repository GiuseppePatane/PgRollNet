using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class DropDefaultOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "drop_default";

    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("column")]
    public required string Column { get; init; }

    public string Describe() => $"drop DEFAULT on '{Table}.{Column}'";

    /// <remarks>
    /// Rollback is intentionally a no-op: the original default value is not preserved
    /// during Start, so it cannot be restored on Rollback.
    /// </remarks>
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
        var r = ValidateStructure();
        if (!r.IsValid) return r;
        if (!schema.TableExists(Table))
            return ValidationResult.Failure($"Table '{Table}' does not exist.");
        if (!schema.ColumnExists(Table, Column))
            return ValidationResult.Failure($"Column '{Column}' does not exist in table '{Table}'.");
        return ValidationResult.Success;
    }

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;
}
