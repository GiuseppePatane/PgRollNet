---
title: Architecture
description: Internals — expand/contract pattern, version schemas, trigger-based backfill, state storage, concurrent operations.
outline: deep
---

# Architecture

## Overview

pgroll.NET is organized into four projects:

```
PgRoll.Core          — database-agnostic abstractions (operations, models, schema, errors)
PgRoll.PostgreSQL    — PostgreSQL implementation (executor, state store, schema reader)
PgRoll.Cli           — .NET tool CLI (`pgroll-net`, built on System.CommandLine)
PgRoll.EntityFrameworkCore — EF Core → pgroll converter library
```

The Core layer defines contracts. The PostgreSQL layer implements them. The CLI wires them together for human use. The EF Core library converts from the EF Core migration DSL.

---

## Expand/Contract Pattern

Zero-downtime schema changes follow the [expand/contract](https://martinfowler.com/bliki/ParallelChange.html) pattern:

```
Deploy v1 (old code)
   │
   ▼
pgroll-net start migration.json   ← Expand phase
   │  - Add new column/structure
   │  - Keep old column/structure
   │  - Set up trigger for dual-write
   │  - Backfill existing rows
   ▼
Deploy v2 (new code, reads new column)
   │
   ▼
pgroll-net complete               ← Contract phase
   │  - Drop old column/structure
   │  - Drop trigger and version schema
   ▼
(migration done)
```

Between Start and Complete, **both old and new structures coexist**. Old application versions continue using the old schema; new versions use the new one.

---

## Version Schemas

When an operation requires expand/contract (e.g. `add_column` with `up`, `alter_column`), pgroll creates a temporary PostgreSQL schema named:

```
{schema}_{migration_name}
```

This schema contains views that expose the correct column names/types to each application version. The `search_path` for old application versions points to the original schema; new versions point to the version schema.

Example:
- Schema: `public`
- Migration: `add_full_name`
- Version schema: `public_add_full_name`

The version schema is **dropped automatically** at Complete or Rollback.

---

## Trigger-Based Dual Write

During Start for expand/contract operations, pgroll installs a `BEFORE INSERT OR UPDATE` trigger on the affected table. The trigger keeps the new column in sync with writes to the old column (and vice versa), using the `up` expression provided in the migration.

The trigger body uses the pattern:

```sql
PERFORM (SELECT NEW.*)   -- expands NEW into named columns
```

This allows the `up`/`down` expressions to reference columns by name inside PL/pgSQL.

---

## Backfill

After setting up the trigger, pgroll backfills existing rows using batched updates:

```sql
UPDATE {table}
SET {new_col} = {up_expression}
WHERE ctid IN (
  SELECT ctid FROM {table}
  WHERE {new_col} IS NULL
  LIMIT {batch_size}
  FOR UPDATE SKIP LOCKED
)
```

Key properties:
- `FOR UPDATE SKIP LOCKED` — skips rows locked by concurrent transactions, avoiding contention
- Batched — runs in configurable batch sizes to avoid long-running transactions
- Idempotent — only processes rows where the new column is still NULL

---

## State Storage

pgroll tracks migrations in a `pgroll.migrations` table created by `pgroll init`:

```sql
CREATE TABLE pgroll.migrations (
    schema      text NOT NULL,
    name        text NOT NULL,
    migration   jsonb,          -- original JSON, stored for pull/history
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    parent      text,           -- name of the previous migration
    done        boolean NOT NULL DEFAULT false,
    PRIMARY KEY (schema, name)
);
```

States:
- `done = false` — migration is in progress (Start phase completed, Complete not yet called)
- `done = true` — migration is completed

Only one `done = false` record per schema is allowed at a time.

---

## Concurrent Operations

Operations that require `CREATE INDEX CONCURRENTLY` or `DROP INDEX CONCURRENTLY`, and operations that perform backfill, **cannot run inside a transaction** (PostgreSQL restriction).

pgroll handles this transparently via the `RequiresConcurrentConnection` property on each operation. When `true`, the executor opens a separate autocommit connection for that operation.

Operations with `RequiresConcurrentConnection = true`:
- `create_index`
- `add_column` (when `up` is set)
- `alter_column` (always)

---

## Operation Phases in Detail

### Operations that defer to Complete

Some operations do nothing at Start (to keep rollback cost-free) and apply the real change at Complete:

| Operation | What is deferred |
|-----------|-----------------|
| `rename_table` | `ALTER TABLE RENAME` |
| `drop_column` | `ALTER TABLE DROP COLUMN` |
| `rename_column` | `ALTER TABLE RENAME COLUMN` |
| `drop_index` | `DROP INDEX CONCURRENTLY` |
| `drop_constraint` | `ALTER TABLE DROP CONSTRAINT` |
| `rename_constraint` | `ALTER TABLE RENAME CONSTRAINT` |

### Soft-delete for table drops

`drop_table` renames the table to `_pgroll_del_{name}` at Start rather than dropping it immediately. This means:
- Start is fast and safe
- Rollback is a simple rename back
- Complete issues the real `DROP TABLE`

---

## Validation

Every operation implements `Validate(SchemaSnapshot schema)`. The validator is called before Start to detect problems early (e.g. table does not exist, column already exists, constraint name conflict). Validation reads `pg_catalog` via `PgSchemaReader` to build a `SchemaSnapshot`.

---

## Project Dependencies

```
PgRoll.Cli ──────────────────────┐
                                 ▼
PgRoll.PostgreSQL ──────► PgRoll.Core
                                 ▲
PgRoll.EntityFrameworkCore ──────┘
```

- `PgRoll.Core` has no external dependencies beyond `Microsoft.Extensions.Logging.Abstractions`
- `PgRoll.PostgreSQL` depends on `Npgsql`
- `PgRoll.Cli` depends on `System.CommandLine`
- `PgRoll.EntityFrameworkCore` depends on `Microsoft.EntityFrameworkCore.Relational`

---

## Assembly Load Isolation (EF Core CLI)

The `pgroll-net efcore convert` command loads user assemblies in an isolated `AssemblyLoadContext` (collectible). This prevents version conflicts between the host process's own EF Core reference (9.x) and the user's assembly (which may use 7.x or 8.x).

Dependency resolution order for the isolated context:

1. **Local directory** — looks for `{AssemblyName}.dll` in the same folder as the target DLL
2. **NuGet global cache** — parses `{assembly}.deps.json` to find package versions, then looks up the DLL in `~/.nuget/packages/` (or `$NUGET_PACKAGES`)
3. **Runtime fallback** — delegates to the default context for BCL and framework assemblies

The converter itself (`ReflectionConverter`) uses only reflection and type-name matching — it never casts to EF Core types — so it works with any EF Core version without compile-time references.
