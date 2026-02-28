namespace PgRoll.Core.Models;

public sealed record StartResult(
    string MigrationName,
    bool RequiresComplete
);
