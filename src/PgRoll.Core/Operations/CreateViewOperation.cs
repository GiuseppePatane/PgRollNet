using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class CreateViewOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "create_view";

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("definition")]
    public required string Definition { get; init; }

    public string Describe() => $"create view '{Name}'";

    public ValidationResult ValidateStructure()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return ValidationResult.Failure("View name is required.");
        if (string.IsNullOrWhiteSpace(Definition))
            return ValidationResult.Failure("View definition is required.");
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
