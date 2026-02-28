using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class DropIndexOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "drop_index";

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    public bool RequiresConcurrentConnection => true;

    public ValidationResult ValidateStructure() =>
        string.IsNullOrWhiteSpace(Name)
            ? ValidationResult.Failure("Index name is required.")
            : ValidationResult.Success;

    public ValidationResult Validate(SchemaSnapshot schema)
    {
        if (string.IsNullOrWhiteSpace(Name))
            return ValidationResult.Failure("Index name is required.");

        if (!schema.IndexExists(Name))
            return ValidationResult.Failure($"Index '{Name}' does not exist.");

        return ValidationResult.Success;
    }

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new StartResult(ctx.SchemaName, RequiresComplete: true));

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;
}
