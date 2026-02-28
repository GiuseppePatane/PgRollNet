---
title: CLI Reference
description: All pgroll commands and options — init, start, complete, rollback, status, validate, pending, migrate, pull, efcore convert.
outline: deep
---

# CLI Reference

All commands accept the following global options:

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--connection` | | *(required)* | PostgreSQL connection string |
| `--schema` | | `public` | Target schema name |

---

## `pgroll init`

Initialize pgroll in the target database. Creates the `pgroll.migrations` tracking table if it does not already exist. Safe to run multiple times.

```
pgroll init --connection <conn>
```

**Example:**

```bash
pgroll init --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

---

## `pgroll start <file>`

Start a migration from a JSON file. Executes the **Start** phase for every operation in the file and records the migration in `pgroll.migrations` with `done = false`.

```
pgroll start <file> --connection <conn> [--schema <name>]
```

| Argument | Description |
|----------|-------------|
| `<file>` | Path to a pgroll JSON migration file |

**Example:**

```bash
pgroll start migrations/002_add_email_verified.json \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

Only one migration can be active at a time. Running `start` while another migration is active returns an error.

---

## `pgroll complete`

Complete the currently active migration. Executes the **Complete** phase for every operation and marks the migration as `done = true`.

```
pgroll complete --connection <conn> [--schema <name>]
```

**Example:**

```bash
pgroll complete --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

---

## `pgroll rollback`

Roll back the currently active migration to the pre-Start state. Executes the **Rollback** phase for every operation and removes the migration record.

```
pgroll rollback --connection <conn> [--schema <name>]
```

**Example:**

```bash
pgroll rollback --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

---

## `pgroll status`

Show the currently active (in-progress) migration, if any.

```
pgroll status --connection <conn> [--schema <name>]
```

**Output examples:**

```
Active migration: 003_add_age_column (started 2025-08-01 14:32:11 UTC)
```

```
No active migration.
```

---

## `pgroll validate <file>`

Validate a migration file against the current database schema without executing it. Useful in CI to catch errors before deployment.

```
pgroll validate <file> --connection <conn> [--schema <name>]
```

| Argument | Description |
|----------|-------------|
| `<file>` | Path to a pgroll JSON migration file |

Exits with code `0` if valid, `1` if there are validation errors.

**Example:**

```bash
pgroll validate migrations/004_drop_legacy_column.json \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

---

## `pgroll pending <directory>`

List migration files in a directory that have not yet been applied to the database.

```
pgroll pending <directory> --connection <conn> [--schema <name>]
```

| Argument | Description |
|----------|-------------|
| `<directory>` | Directory containing pgroll JSON files |

**Exit codes:**

| Code | Meaning |
|------|---------|
| `0` | Everything is up to date — no migrations to apply |
| `1` | One or more migrations are pending — the pipeline should apply them |
| `2` | Error (directory not found, connection failure, etc.) |

**Example:**

```bash
pgroll pending ./migrations \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

**Output (pending):**
```
Pending migrations (2):
  003_add_age_column.json
  004_add_users_index.json
```

**Output (up to date):**
```
Up to date. No pending migrations.
```

Designed for use in CI/CD pipelines:

```bash
if pgroll pending ./migrations --connection "$DB_CONN"; then
  echo "No migrations to apply, skipping"
else
  pgroll migrate ./migrations --connection "$DB_CONN"
fi
```

---

## `pgroll migrate <directory>`

Apply all pending migrations from a directory. Discovers `*.json` files sorted alphabetically, skips migrations that have already been completed (by checking `pgroll.migrations`), and for each pending migration runs Start → Complete in sequence.

```
pgroll migrate <directory> --connection <conn> [--schema <name>]
```

| Argument | Description |
|----------|-------------|
| `<directory>` | Directory containing pgroll JSON files |

**Example:**

```bash
pgroll migrate ./migrations \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

**Note:** This command is designed for CI/CD pipelines where all migrations should be applied atomically. It does not support the expand/contract multi-deployment pattern — use `start` + `complete` separately for zero-downtime workflows.

---

## `pgroll pull <directory>`

Write the completed migration history to JSON files in a directory. One file is created per completed migration, named `{migration_name}.json`. Useful for reconstructing the migration history from the database.

```
pgroll pull <directory> --connection <conn> [--schema <name>]
```

| Argument | Description |
|----------|-------------|
| `<directory>` | Output directory (created if it does not exist) |

**Example:**

```bash
pgroll pull ./migrations-backup \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

---

## `pgroll efcore convert`

Discover all EF Core migrations in a compiled assembly and convert them to pgroll JSON files. Supports EF Core 7.x, 8.x, 9.x and later.

```
pgroll efcore convert --assembly <dll> [--output <dir>] [--filter <string>]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--assembly` | *(required)* | Path to the compiled EF Core migrations assembly (`.dll`) |
| `--output` | `pgroll-migrations` | Output directory for pgroll JSON files (created if it does not exist) |
| `--filter` | | Only convert migrations whose name contains this string (case-insensitive) |

**Example:**

```bash
pgroll efcore convert \
  --assembly ./bin/Release/net8.0/MyApp.Migrations.dll \
  --output ./pgroll-migrations

pgroll efcore convert \
  --assembly ./bin/Release/net8.0/MyApp.Migrations.dll \
  --output ./pgroll-migrations \
  --filter "AddUser"
```

**Output:**

```
Assembly : /path/to/MyApp.Migrations.dll
Output   : /path/to/pgroll-migrations

Found 12 migration(s)

  ✓  20240101_InitialCreate  [5 ops]
          • create_table
          • create_table
          • create_index
          • create_constraint
          • create_constraint
  ✓  20240201_AddEmailVerified  [1 op  → add_column]
  ✓  20240301_AddStoredProcedure  [(no schema ops)]  skip: SqlOperation×2

Written  : 12 file(s) → /path/to/pgroll-migrations
Skipped  : 2 unsupported operation(s) (SqlOperation)
           Sql ops (stored procedures, raw DDL) have no pgroll equivalent.
```

See [EF Core Integration](efcore.md) for a complete guide.
