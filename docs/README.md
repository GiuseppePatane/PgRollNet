# pgroll.NET Documentation

**pgroll.NET** is a zero-downtime PostgreSQL schema migration tool for .NET, ported from the [pgroll](https://github.com/xataio/pgroll) Go project.

Instead of applying schema changes in a single blocking transaction, pgroll uses an **expand/contract pattern**: the old and new schemas coexist during deployment, so you can roll out new application code and database changes independently — without downtime, without locking, without coordination windows.

## How it works

Every migration has three phases:

| Phase | What happens |
|-------|-------------|
| **Start** | New schema is created alongside the old one. Existing rows are backfilled via trigger. Both old and new application versions work. |
| **Complete** | Old schema is removed. Cut-over is complete. |
| **Rollback** | New schema is removed. Database returns to pre-Start state. |

## Contents

| Document | Description |
|----------|-------------|
| [Getting Started](getting-started.md) | Install, initialize, and run your first migration |
| [CLI Reference](cli-reference.md) | All CLI commands and options |
| [Migration Format](migration-format.md) | JSON file format and column definition schema |
| [Operations](operations.md) | All 24 supported operations with examples |
| [EF Core Integration](efcore.md) | Convert EF Core migrations and use the .NET library |
| [CD Integration](cd-integration.md) | GitHub Actions, Azure DevOps, Kubernetes — pipeline examples |
| [Architecture](architecture.md) | Internals: expand/contract, version schemas, backfill, state storage |
| [Production Readiness](production-readiness.md) | Verified matrix, operational checks, rollout expectations |

## Quick Example

```bash
# 1. Initialize pgroll in your database
pgroll-net init --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"

# 2. Start a migration
pgroll-net start 001_add_email_verified.json --connection "..."

# 3. Deploy your new application version
# (old version still works — both schemas coexist)

# 4. Complete the migration
pgroll-net complete --connection "..."
```

```json
{
  "name": "001_add_email_verified",
  "operations": [
    {
      "type": "add_column",
      "table": "users",
      "column": {
        "name": "email_verified",
        "type": "boolean",
        "nullable": false,
        "default": "false"
      }
    }
  ]
}
```

## EF Core Users

Convert your existing EF Core migrations to pgroll JSON in a single command:

```bash
pgroll-net efcore convert \
  --assembly bin/Release/net8.0/MyApp.Migrations.dll \
  --output pgroll-migrations
```

See [EF Core Integration](efcore.md) for the full adoption guide.

## Stack

- .NET 10, PostgreSQL 14+ (minimum verified version: PostgreSQL 14; CI matrix covers 14, 15, 16, and 17)
- `PgRoll.Core` — database-agnostic operation model
- `PgRoll.PostgreSQL` — PostgreSQL implementation (Npgsql)
- `PgRoll.Cli` — .NET global tool package (`pgroll-net`)
- `PgRoll.EntityFrameworkCore` — EF Core converter library
