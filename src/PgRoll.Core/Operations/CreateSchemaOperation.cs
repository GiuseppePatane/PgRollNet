using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class CreateSchemaOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "create_schema";

    [JsonPropertyName("schema")]
    public required string Schema { get; init; }

    public string Describe() => $"create schema '{Schema}'";

    public ValidationResult ValidateStructure() =>
        string.IsNullOrWhiteSpace(Schema)
            ? ValidationResult.Failure("Schema name is required.")
            : ValidationResult.Success;

    public ValidationResult Validate(SchemaSnapshot schema) => ValidateStructure();

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");
}
