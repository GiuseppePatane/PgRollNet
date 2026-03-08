using System.CommandLine;
using PgRoll.Core.Errors;
using PgRoll.PostgreSQL;

namespace PgRoll.Cli;

public sealed class GlobalOptions
{
    public readonly Option<string?> Connection = new("--connection", "PostgreSQL connection string.");
    public readonly Option<string> Schema = new("--schema", () => "public", "Migration schema (default: public).");
    public readonly Option<string> PgrollSchema = new("--pgroll-schema", () => "pgroll", "pgroll internal schema (default: pgroll).");
    public readonly Option<int> LockTimeout = new("--lock-timeout", () => 500, "DDL lock timeout in milliseconds (default: 500).");
    public readonly Option<string?> Role = new("--role", "Optional PostgreSQL role to set before executing DDL.");
    public readonly Option<bool> Verbose = new("--verbose", "Enable verbose logging.");

    public string RequireConnection(string? c)
    {
        if (string.IsNullOrWhiteSpace(c))
            throw new PgRollException("--connection is required for this command.");
        return c;
    }

    public PgMigrationExecutor BuildExecutor(string? connection, string schema,
        string pgrollSchema, int lockTimeout, string? role)
        => new(RequireConnection(connection), schema, pgrollSchema, lockTimeout, role);
}
