using System.Text.Json.Serialization;
using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed class RawSqlOperation : IMigrationOperation
{
    [JsonPropertyName("type")]
    public string Type => "raw_sql";

    [JsonPropertyName("sql")]
    public required string Sql { get; init; }

    [JsonPropertyName("rollback_sql")]
    public string? RollbackSql { get; init; }

    public string Describe() => "execute raw SQL";

    public ValidationResult ValidateStructure() =>
        string.IsNullOrWhiteSpace(Sql)
            ? ValidationResult.Failure("sql is required.")
            : ValidationResult.Success;

    public ValidationResult Validate(SchemaSnapshot schema) => ValidateStructure();

    public Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");

    public Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in PgRoll.PostgreSQL layer.");
}
