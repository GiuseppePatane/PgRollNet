---
title: Getting Started
description: Install pgroll-net, initialize your database, and run your first zero-downtime migration.
---

# Getting Started

## Prerequisites

- .NET 10 SDK
- PostgreSQL 14 or later
- Docker (for running tests)

## Installation

### As a dotnet global tool

```bash
dotnet tool install --global PgRoll.Cli
```

Or from source:

```bash
git clone https://github.com/GiuseppePatane/PgRollNet
cd PgRollNet
dotnet pack src/PgRoll.Cli -c Release -o ./nupkgs
dotnet tool install --global --add-source ./nupkgs PgRoll.Cli
```

### As a library

```bash
dotnet add package PgRoll.PostgreSQL
```

## Quick Start

### 1. Initialize pgroll

Before running any migration, initialize the pgroll state schema in your database:

```bash
pgroll-net init --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

This creates the `pgroll.migrations` table (idempotent — safe to run multiple times).

### 2. Scaffold a migration file

```bash
pgroll-net new create_users --output ./migrations
# → creates ./migrations/0001_create_users.json
```

The prefix (`0001_`) is computed automatically from the highest existing prefix in the directory, ensuring files always sort in apply order. Edit the generated file to add your operations.

### 3. Write your migration

Edit the generated file. Both JSON and YAML are supported.

**JSON (`001_create_users.json`):**

```json
{
  "name": "001_create_users",
  "operations": [
    {
      "type": "create_table",
      "table": "users",
      "columns": [
        { "name": "id", "type": "bigserial", "nullable": false, "primary_key": true },
        { "name": "email", "type": "text", "nullable": false },
        { "name": "created_at", "type": "timestamp with time zone", "nullable": false, "default": "now()" }
      ]
    }
  ]
}
```

**YAML (`001_create_users.yaml`):**

```yaml
name: 001_create_users
operations:
  - type: create_table
    table: users
    columns:
      - name: id
        type: bigserial
        nullable: false
        primary_key: true
      - name: email
        type: text
        nullable: false
      - name: created_at
        type: timestamp with time zone
        nullable: false
        default: "now()"
```

### 4. Validate (optional)

Check the migration against the live database schema before running it:

```bash
pgroll-net validate ./migrations/001_create_users.json \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

Use `--offline` to validate structure only without a database connection.

### 5. Start the migration

```bash
pgroll-net start ./migrations/001_create_users.json \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

During the Start phase the migration is applied to the database. For simple operations like `create_table` this is immediate. For operations that use the expand/contract pattern (e.g. `add_column` with an `up` expression) a temporary schema is created and data backfilled.

### 6. Complete the migration

When you are satisfied that the migration is correct and both old and new application versions are deployed:

```bash
pgroll-net complete \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

### 7. Rollback (if needed)

Before completing, you can always roll back to the pre-migration state:

```bash
pgroll-net rollback \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

## Batch Migrations

For applying a whole directory of migration files in sequence:

```bash
pgroll-net migrate ./migrations \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

Files are applied alphabetically. Already-completed migrations are skipped automatically. Both `.json`, `.yaml`, and `.yml` files are discovered.

Add `--continue-on-error` to skip failing migrations and continue instead of stopping:

```bash
pgroll-net migrate ./migrations \
  --connection "..." \
  --continue-on-error
```

## Global Flags

All commands that interact with the database accept these global options:

| Flag | Default | Description |
|------|---------|-------------|
| `--connection` | — | PostgreSQL connection string |
| `--schema` | `public` | Target user schema |
| `--pgroll-schema` | `pgroll` | Internal pgroll state schema |
| `--lock-timeout` | `500` | DDL lock timeout in milliseconds |
| `--role` | — | PostgreSQL role to `SET` before executing DDL |
| `--verbose` | `false` | Enable verbose logging |

## EF Core Users

If you are already using EF Core Migrations, convert your existing migrations to pgroll JSON files in a single command:

```bash
pgroll-net efcore convert \
  --assembly ./bin/Release/net8.0/MyApp.Migrations.dll \
  --output ./pgroll-migrations
```

Output files are prefixed with a 4-digit position index (`0001_InitialCreate.json`, `0002_AddUsers.json`, …) so alphabetical sort always matches EF Core's apply order.

See [EF Core Integration](efcore.md) for details.
