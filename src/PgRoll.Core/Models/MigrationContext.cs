using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Models;

public sealed class MigrationContext
{
    public required string ConnectionString { get; init; }
    public required string SchemaName { get; init; }
    public SchemaSnapshot Schema { get; set; } = SchemaSnapshot.Empty;
    public ILogger Logger { get; init; } = NullLogger.Instance;
}
