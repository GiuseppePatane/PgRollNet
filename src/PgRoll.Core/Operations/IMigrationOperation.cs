using PgRoll.Core.Models;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Operations;

public sealed record ValidationResult(bool IsValid, string? Error = null)
{
    public static ValidationResult Success { get; } = new(true);
    public static ValidationResult Failure(string error) => new(false, error);
}

public interface IMigrationOperation
{
    /// <summary>
    /// The operation type discriminator used in JSON serialization (e.g. "create_table").
    /// </summary>
    string Type { get; }

    Task<StartResult> StartAsync(MigrationContext ctx, CancellationToken ct = default);
    Task CompleteAsync(MigrationContext ctx, CancellationToken ct = default);
    Task RollbackAsync(MigrationContext ctx, CancellationToken ct = default);
    ValidationResult Validate(SchemaSnapshot schema);

    /// <summary>
    /// True if StartAsync uses CREATE/DROP INDEX CONCURRENTLY, which requires autocommit.
    /// </summary>
    bool RequiresConcurrentConnection => false;
}
