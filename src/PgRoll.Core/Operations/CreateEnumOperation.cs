using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class CreateEnumOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "create_enum";

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("values")]
    public required IReadOnlyList<string> Values { get; init; }

    public string Describe() => $"create enum type '{Name}'";

    public ValidationResult ValidateStructure()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return ValidationResult.Failure("Enum name is required.");
        if (Values is null || Values.Count == 0)
            return ValidationResult.Failure("Enum must have at least one value.");
        var distinct = Values.Distinct(StringComparer.Ordinal).Count();
        if (distinct != Values.Count)
            return ValidationResult.Failure("Enum values must be unique.");
        return ValidationResult.Success;
    }

    public ValidationResult Validate(SchemaSnapshot schema) => ValidateStructure();

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");
}
