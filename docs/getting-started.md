---
title: Getting Started
description: Install pgroll, initialize your database, and run your first zero-downtime migration.
---

# Getting Started

## Prerequisites

- .NET 10 SDK
- PostgreSQL 14 or later
- Docker (for running tests)

## Installation

### As a dotnet tool (local)

```bash
dotnet tool install --global pgroll
```

Or from source:

```bash
git clone https://github.com/your-org/pgrool
cd pgrool
dotnet pack src/PgRoll.Cli
dotnet tool install --global --add-source ./src/PgRoll.Cli/nupkg pgroll
```

### As a library

```bash
dotnet add package PgRoll.PostgreSQL
```

## Quick Start

### 1. Initialize pgroll

Before running any migration, initialize the pgroll state schema in your database:

```bash
pgroll init --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

This creates the `pgroll.migrations` table (idempotent — safe to run multiple times).

### 2. Write a migration

Create a file `001_create_users.json`:

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

### 3. Start the migration

```bash
pgroll start 001_create_users.json \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

During the Start phase the migration is applied to the database. For simple operations like `create_table` this is immediate. For operations that use the expand/contract pattern (e.g. `add_column` with an `up` expression) a temporary schema is created and data backfilled.

### 4. Complete the migration

When you are satisfied that the migration is correct and both old and new application versions are deployed:

```bash
pgroll complete \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

### 5. Rollback (if needed)

Before completing, you can always roll back to the pre-migration state:

```bash
pgroll rollback \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

## Batch Migrations

For applying a whole directory of migration files in sequence:

```bash
pgroll migrate ./migrations \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

Files are applied alphabetically. Already-completed migrations are skipped automatically.

## EF Core Users

If you are already using EF Core Migrations, convert your existing migrations to pgroll JSON files in a single command:

```bash
pgroll efcore convert \
  --assembly ./bin/Release/net8.0/MyApp.Migrations.dll \
  --output ./pgroll-migrations
```

See [EF Core Integration](efcore.md) for details.
