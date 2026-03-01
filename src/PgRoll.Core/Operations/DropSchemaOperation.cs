using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class DropSchemaOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "drop_schema";

    [JsonPropertyName("schema")]
    public required string Schema { get; init; }

    public string Describe() => $"drop schema '{Schema}'";

    public ValidationResult ValidateStructure() =>
        string.IsNullOrWhiteSpace(Schema)
            ? ValidationResult.Failure("Schema name is required.")
            : ValidationResult.Success;

    public ValidationResult Validate(SchemaSnapshot schema) => ValidateStructure();

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");
}
