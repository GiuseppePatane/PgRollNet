namespace PgRoll.Core.State;

public sealed record MigrationRecord(
    string Schema,
    string Name,
    string? MigrationJson,
    string? MigrationChecksum,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Parent,
    bool Done
);
