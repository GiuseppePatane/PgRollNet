using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class DropEnumOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "drop_enum";

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    public string Describe() => $"drop enum type '{Name}'";

    public ValidationResult ValidateStructure() =>
        string.IsNullOrWhiteSpace(Name)
            ? ValidationResult.Failure("Enum name is required.")
            : ValidationResult.Success;

    public ValidationResult Validate(SchemaSnapshot schema) => ValidateStructure();

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");
}
