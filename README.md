# pgroll.NET

[![CI](https://github.com/GiuseppePatane/PgRollNet/actions/workflows/ci.yml/badge.svg)](https://github.com/GiuseppePatane/PgRollNet/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PgRoll.PostgreSQL.svg)](https://www.nuget.org/packages/PgRoll.PostgreSQL)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Zero-downtime PostgreSQL schema migrations for .NET — a port of [pgroll](https://github.com/xataio/pgroll) to the .NET ecosystem.

Minimum verified PostgreSQL version: 14.


## Features

- **Zero downtime** — old and new application versions run simultaneously during migrations
- **Instant rollback** — roll back any migration without data loss
- **20+ operations** — create/drop tables, columns, indexes, constraints, enums, views, schemas, and more
- **EF Core integration** — convert EF Core migrations to pgroll JSON automatically
- **CLI tool** — `pgroll-net init|start|complete|rollback|status|validate|migrate|pending|pull|baseline|latest|doctor|plan|inspect-active|new`
- **YAML support** — write migrations in JSON or YAML
- **Offline validation** — validate migration files without a database connection
- **Drift detection** — applied migrations store checksums so renamed files stay safe but edited files are rejected
- **Operational tooling** — `doctor`, `plan`, `inspect-active`, batched backfill tuning, and verbose execution logs
- **Global flags** — `--schema`, `--pgroll-schema`, `--lock-timeout`, `--statement-timeout`, `--backfill-batch-size`, `--backfill-delay-ms`, `--role`, `--verbose`

## Installation

### CLI tool

```bash
dotnet tool install -g PgRoll.Cli
```

### NuGet packages

```bash
dotnet add package PgRoll.PostgreSQL
dotnet add package PgRoll.EntityFrameworkCore   # optional EF Core integration
```

## Quick Start

### CLI

```bash
# Initialize pgroll state in your database
pgroll-net init --connection "Host=localhost;Database=mydb;Username=postgres"

# Scaffold a new migration file
pgroll-net new 01_create_users --output ./migrations

# Start a migration
pgroll-net start ./migrations/01_create_users.json --connection "..."

# Complete (make permanent) or roll back
pgroll-net complete --connection "..."
pgroll-net rollback --connection "..."

# Apply all pending migrations from a directory (JSON and YAML supported)
pgroll-net migrate ./migrations --connection "..."

# Inspect the active migration before recovery/complete
pgroll-net inspect-active --connection "..."

# Check prerequisites and migration history integrity
pgroll-net doctor --connection "..." --migrations ./migrations

# Print a text or JSON execution plan without applying
pgroll-net plan ./migrations/01_create_users.json --format json

# Mark current database state as baseline (no-op migration)
pgroll-net baseline my_baseline --connection "..."

# Print the name of the latest applied migration
pgroll-net latest --connection "..."
```

### Global flags

All commands accept these global options:

| Flag | Default | Description |
|------|---------|-------------|
| `--connection` | — | PostgreSQL connection string (required for DB commands) |
| `--schema` | `public` | Target user schema |
| `--pgroll-schema` | `pgroll` | Internal pgroll state schema |
| `--lock-timeout` | `500` | DDL lock timeout in milliseconds |
| `--statement-timeout` | `0` | Statement timeout in milliseconds (`0` = PostgreSQL default) |
| `--backfill-batch-size` | `1000` | Batch size used for expand/contract backfills |
| `--backfill-delay-ms` | `0` | Delay between backfill batches in milliseconds |
| `--role` | — | PostgreSQL role to `SET` before executing DDL |
| `--verbose` | `false` | Enable verbose logging |

### Migration file format

```json
{
  "name": "01_create_users",
  "operations": [
    {
      "type": "create_table",
      "table": "users",
      "columns": [
        { "name": "id",    "type": "serial",       "primaryKey": true },
        { "name": "email", "type": "varchar(255)",  "unique": true, "nullable": false },
        { "name": "name",  "type": "text" }
      ]
    }
  ]
}
```

YAML format is also supported (`.yaml` / `.yml`):

```yaml
name: 01_create_users
operations:
  - type: create_table
    table: users
    columns:
      - name: id
        type: serial
        primaryKey: true
      - name: email
        type: varchar(255)
        unique: true
        nullable: false
      - name: name
        type: text
```

### .NET API

```csharp
using PgRoll.PostgreSQL;

var connectionString = "Host=localhost;Database=mydb;Username=postgres";

await using var executor = new PgMigrationExecutor(connectionString);

await executor.InitializeAsync();
await executor.StartAsync(migration);
await executor.CompleteAsync();
// or: await executor.RollbackAsync();
```

### EF Core integration

```bash
# Convert an EF Core migration assembly to pgroll JSON files
pgroll-net efcore convert --assembly MyApp.Migrations.dll --output ./pgroll-migrations
```

Or use the API directly:

```csharp
using PgRoll.EntityFrameworkCore;

var result = EfCoreMigrationConverter.Convert("AddUserTable", efCoreMigrationOperations);

// result.Migration  — pgroll Migration ready to execute
// result.Skipped    — list of operation types that couldn't be converted
```

## Supported Operations

| Operation | Description |
|-----------|-------------|
| `create_table` | Create a new table |
| `drop_table` | Drop a table (soft-delete during migration window) |
| `rename_table` | Rename a table |
| `add_column` | Add a column (with optional backfill via `up` expression) |
| `drop_column` | Drop a column |
| `rename_column` | Rename a column |
| `alter_column` | Change type, nullability, default, rename — with dual-write via triggers |
| `create_index` | Create an index (CONCURRENTLY) |
| `drop_index` | Drop an index (CONCURRENTLY) |
| `create_constraint` | Add CHECK or FOREIGN KEY constraint (NOT VALID + VALIDATE) |
| `drop_constraint` | Drop a constraint |
| `rename_constraint` | Rename a constraint |
| `set_not_null` | Add NOT NULL constraint |
| `drop_not_null` | Remove NOT NULL constraint |
| `set_default` | Set column default |
| `drop_default` | Drop column default |
| `create_schema` | Create a schema |
| `drop_schema` | Drop a schema |
| `create_enum` | Create an enum type |
| `drop_enum` | Drop an enum type |
| `create_view` | Create a view |
| `drop_view` | Drop a view |
| `raw_sql` | Execute arbitrary SQL with optional rollback SQL |

## Operational Notes

- Applied migrations are matched by migration `name`, not by filename.
- Renaming an already applied file is safe as long as the migration contents do not change.
- Changing the contents of an already applied migration is rejected by checksum validation in `pending`, `migrate`, and `doctor`.
- Use `doctor` before production rollouts to verify server version, schema privileges, pgroll state, and optional migration history integrity.
- Use `inspect-active` to see the active migration, its checksum, and the version schema that would be involved in recovery.

## License

MIT — see [LICENSE](LICENSE).
