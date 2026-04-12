---
title: CLI Reference
description: All pgroll-net commands and global options.
outline: deep
---

# CLI Reference

Install the tool:

```bash
dotnet tool install -g PgRoll.Cli
```

## Global Options

All commands accept these options (passed before or after the command name):

| Option | Default | Description |
|--------|---------|-------------|
| `--connection` | *(required for DB commands)* | PostgreSQL connection string |
| `--schema` | `public` | Target user schema |
| `--pgroll-schema` | `pgroll` | Internal pgroll state schema |
| `--lock-timeout` | `500` | DDL lock timeout in milliseconds |
| `--statement-timeout` | `0` | SQL statement timeout in milliseconds (`0` = PostgreSQL default) |
| `--backfill-batch-size` | `1000` | Batch size for expand/contract backfills |
| `--backfill-delay-ms` | `0` | Delay between backfill batches in milliseconds |
| `--role` | — | PostgreSQL role to `SET` before executing DDL |
| `--verbose` | `false` | Enable verbose logging |

---

## `pgroll-net new [name]`

Interactively scaffold a new migration file. Guides you through selecting operation types and filling in the required fields. The file is created with an **auto-incremented numeric prefix** that guarantees correct apply order.

```
pgroll-net new [name] [--output <dir>]
```

| Argument/Option | Description |
|-----------------|-------------|
| `name` | Migration name (optional — you will be prompted if omitted) |
| `--output` | Directory where the file is created (default: current directory; created if it does not exist) |

**Ordering:** the command scans the output directory for files starting with a numeric prefix (`0001_`, `0042_`, …), finds the highest number, and uses `max + 1`. Starts at `0001` if no prefixed files exist. Compatible with `pgroll-net efcore convert` output.

**Example session:**

```
$ pgroll-net new --output ./migrations

Migration name: create_orders

Add operations to this migration (press Enter to skip optional fields).

Add an operation? [Y/n]

  Operation types:
   1. create_table          Create a new table with columns
   2. drop_table            Drop an existing table
   3. rename_table          Rename a table
   4. add_column            Add a column to a table
   5. alter_column          Change type, nullability, default or rename a column
  ...
  23. raw_sql               Execute arbitrary SQL

  Select (1-23): 1
  → create_table

  table name: orders

  Define columns:
    column name: id
    type: bigserial
    nullable? [true/false, default true]: false
    primary_key? [true/false, default false]: true
    ...

  Add another column? [y/N] n

Add another operation? [Y/n] n

Created: ./migrations/0001_create_orders.json
```

**Output file (`0001_create_orders.json`):**

```json
{
  "name": "0001_create_orders",
  "operations": [
    {
      "type": "create_table",
      "table": "orders",
      "columns": [
        { "name": "id", "type": "bigserial", "nullable": false, "primary_key": true }
      ]
    }
  ]
}
```

**Supported operation types in the wizard:** all 23 types — `create_table`, `drop_table`, `rename_table`, `add_column`, `drop_column`, `rename_column`, `alter_column`, `create_index`, `drop_index`, `create_constraint`, `drop_constraint`, `rename_constraint`, `set_not_null`, `drop_not_null`, `set_default`, `drop_default`, `create_schema`, `drop_schema`, `create_enum`, `drop_enum`, `create_view`, `drop_view`, `raw_sql`.

---

## `pgroll-net init`

Initialize pgroll in the target database. Creates the `pgroll.migrations` tracking table if it does not already exist. Safe to run multiple times.

```
pgroll-net init --connection <conn>
```

**Example:**

```bash
pgroll-net init --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

---

## `pgroll-net start <file>`

Start a migration from a JSON or YAML file. Executes the **Start** phase for every operation in the file and records the migration in `pgroll.migrations` with `done = false`.

```
pgroll-net start <file> --connection <conn> [--schema <name>] [--dry-run]
```

| Argument | Description |
|----------|-------------|
| `<file>` | Path to a pgroll JSON or YAML migration file |

**Example:**

```bash
pgroll-net start migrations/002_add_email_verified.json \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

Only one migration can be active at a time. Running `start` while another migration is active returns an error.

Use `--dry-run` to validate and describe the migration without executing it.

---

## `pgroll-net complete`

Complete the currently active migration. Executes the **Complete** phase for every operation and marks the migration as `done = true`.

```
pgroll-net complete --connection <conn> [--schema <name>]
```

**Example:**

```bash
pgroll-net complete --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

---

## `pgroll-net rollback`

Roll back the currently active migration to the pre-Start state. Executes the **Rollback** phase for every operation and removes the migration record.

```
pgroll-net rollback --connection <conn> [--schema <name>]
```

**Example:**

```bash
pgroll-net rollback --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

---

## `pgroll-net status`

Show the currently active (in-progress) migration, if any.

```
pgroll-net status --connection <conn> [--schema <name>]
```

**Output examples:**

```
Active migration: 003_add_age_column (started 2025-08-01 14:32:11 UTC)
```

```
No active migration.
```

---

## `pgroll-net validate <file>`

Validate a migration file against the current database schema without executing it. Useful in CI to catch errors before deployment.

```
pgroll-net validate <file> [--connection <conn>] [--schema <name>] [--offline]
```

| Argument/Option | Description |
|-----------------|-------------|
| `<file>` | Path to a pgroll JSON or YAML migration file |
| `--offline` | Validate structure only — no database connection required |

Exits with code `0` if valid, `1` if there are validation errors.

**Examples:**

```bash
# Online validation (checks table/column existence against live schema)
pgroll-net validate migrations/004_drop_legacy_column.json \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"

# Offline structural validation (no DB needed)
pgroll-net validate migrations/004_drop_legacy_column.json --offline
```

---

## `pgroll-net pending <directory>`

List migration files in a directory that have not yet been applied to the database.

```
pgroll-net pending <directory> --connection <conn> [--schema <name>]
```

| Argument | Description |
|----------|-------------|
| `<directory>` | Directory containing pgroll JSON/YAML files |

**Exit codes:**

| Code | Meaning |
|------|---------|
| `0` | Everything is up to date — no migrations to apply |
| `1` | One or more migrations are pending — the pipeline should apply them |
| `2` | Error (directory not found, connection failure, etc.) |

**Example:**

```bash
pgroll-net pending ./migrations \
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
if pgroll-net pending ./migrations --connection "$DB_CONN"; then
  echo "No migrations to apply, skipping"
else
  pgroll-net migrate ./migrations --connection "$DB_CONN"
fi
```

Applied migrations are verified by checksum. Renaming an already applied file is safe. Editing the contents of an applied migration causes `pending` to fail.

---

## `pgroll-net migrate <directory>`

Apply all pending migrations from a directory. Discovers `*.json`, `*.yaml`, and `*.yml` files sorted alphabetically, skips migrations already completed, and for each pending migration runs Start → Complete in sequence.

```
pgroll-net migrate <directory> --connection <conn> [--schema <name>] [--continue-on-error]
```

| Argument/Option | Description |
|-----------------|-------------|
| `<directory>` | Directory containing pgroll migration files |
| `--continue-on-error` | Log a warning and continue instead of stopping on migration failure |

**Example:**

```bash
pgroll-net migrate ./migrations \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"

# Skip failing migrations (useful during EF Core conversion adoption)
pgroll-net migrate ./migrations \
  --connection "..." \
  --continue-on-error
```

**Note:** This command is designed for CI/CD pipelines where all migrations should be applied sequentially. It does not support the expand/contract multi-deployment pattern — use `start` + `complete` separately for zero-downtime workflows.

Already applied migrations are verified by checksum before execution. If a migration file was edited after being applied, `migrate` fails fast.

---

## `pgroll-net pull <directory>`

Write the completed migration history to JSON files in a directory. One file is created per completed migration. Useful for reconstructing the migration history from the database.

```
pgroll-net pull <directory> --connection <conn> [--schema <name>]
```

| Argument | Description |
|----------|-------------|
| `<directory>` | Output directory (created if it does not exist) |

**Example:**

```bash
pgroll-net pull ./migrations-backup \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

---

## `pgroll-net baseline <name>`

Record the current database state as a baseline — inserts a completed migration record with no operations. Useful when adopting pgroll on an existing database that was not migrated with pgroll.

```
pgroll-net baseline <name> --connection <conn> [--schema <name>]
```

| Argument | Description |
|----------|-------------|
| `<name>` | Name to use for the baseline migration record |

**Example:**

```bash
pgroll-net baseline initial_state \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

After running `baseline`, pgroll will treat the database as already having `initial_state` applied and will only run subsequent migrations.

---

## `pgroll-net latest`

Print the name of the most recently completed migration.

```
pgroll-net latest --connection <conn> [--schema <name>]
```

**Example:**

```bash
pgroll-net latest --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
# → 004_add_users_index
```

Exits with code `1` if no migration has been applied yet.

---

## `pgroll-net doctor`

Run operational preflight checks against a database and optionally verify that the applied migration history still matches local files.

```
pgroll-net doctor --connection <conn> [--migrations <dir>]
```

| Option | Description |
|--------|-------------|
| `--migrations` | Optional directory of local migration files used to verify applied migration checksums |

Checks include:

- PostgreSQL minimum verified version
- current user and target schema visibility
- `CREATE` privilege on the target schema
- presence of the pgroll state table
- currently active migration
- checksum drift between applied migrations and local files when `--migrations` is supplied

**Example:**

```bash
pgroll-net doctor \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret" \
  --migrations ./migrations
```

Exits with code `1` when issues are detected.

---

## `pgroll-net plan <file>`

Render a preview of a migration without applying it. Useful for CI review, change management, or machine-readable inspection.

```
pgroll-net plan <file> [--format text|json] [--connection <conn>] [--schema <name>]
```

| Argument/Option | Description |
|-----------------|-------------|
| `<file>` | Path to the migration file |
| `--format` | Output format: `text` or `json` |

When a connection is provided, `plan` also performs online validation against the live schema and reports validation errors in the output.

**Example:**

```bash
pgroll-net plan ./migrations/004_add_users_index.json --format json
```

---

## `pgroll-net inspect-active`

Print recovery-relevant details for the currently active migration.

```
pgroll-net inspect-active --connection <conn> [--json]
```

| Option | Description |
|--------|-------------|
| `--json` | Emit JSON instead of text |

The output includes:

- active migration name
- schema
- migration checksum
- parent migration
- version schema name
- operation list

**Example:**

```bash
pgroll-net inspect-active --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

---

## `pgroll-net efcore convert`

Discover all EF Core migrations in a compiled assembly and convert them to pgroll JSON files. Supports EF Core 7.x, 8.x, 9.x and later.

```
pgroll-net efcore convert --assembly <dll> [--output <dir>] [--filter <string>]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--assembly` | *(required)* | Path to the compiled EF Core migrations assembly (`.dll`) |
| `--output` | `pgroll-migrations` | Output directory (created if it does not exist) |
| `--filter` | | Only convert migrations whose name contains this string (case-insensitive) |

Output files are prefixed with a 4-digit position index so alphabetical sort always matches EF Core's apply order:

```
0001_InitialCreate.json
0002_AddEmailVerified.json
0003_AddUsersIndex.json
```

**Example:**

```bash
pgroll-net efcore convert \
  --assembly ./bin/Release/net8.0/MyApp.Migrations.dll \
  --output ./pgroll-migrations

pgroll-net efcore convert \
  --assembly ./bin/Release/net8.0/MyApp.Migrations.dll \
  --output ./pgroll-migrations \
  --filter "AddUser"
```

See [EF Core Integration](efcore.md) for a complete guide.
