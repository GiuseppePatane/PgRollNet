---
title: Operations Reference
description: All 24 pgroll operations — table, column, index, constraint, schema, enum, view and raw SQL operations with JSON examples.
outline: deep
---

# Operations Reference

pgroll supports 24 schema operations grouped into six categories. Each operation has three phases: **Start**, **Complete**, and **Rollback**.

- **Start** — applies the change safely; the old schema remains accessible
- **Complete** — finalizes the change; cuts over to the new schema
- **Rollback** — reverts to the pre-Start state (only available before Complete)

---

## Table Operations

### `create_table`

Creates a new table.

| Phase | Action |
|-------|--------|
| Start | `CREATE TABLE` with all columns |
| Complete | no-op |
| Rollback | `DROP TABLE` |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Table name |
| `columns` | ColumnDefinition[] | yes | Column definitions |

Each column supports the full [ColumnDefinition](./migration-format#column-definition) schema, including `unique` and `references` for inline constraints.

**Example:**

```json
{
  "type": "create_table",
  "table": "products",
  "columns": [
    { "name": "id", "type": "bigserial", "nullable": false, "primary_key": true },
    { "name": "name", "type": "text", "nullable": false },
    { "name": "price", "type": "numeric", "nullable": false, "default": "0" },
    { "name": "category_id", "type": "integer", "references": "categories(id)" },
    { "name": "sku", "type": "text", "unique": true },
    { "name": "created_at", "type": "timestamp with time zone", "nullable": false, "default": "now()" }
  ]
}
```

---

### `drop_table`

Drops a table. Uses a soft-delete pattern during Start to allow rollback.

| Phase | Action |
|-------|--------|
| Start | Renames table to `_pgroll_del_{table}` |
| Complete | `DROP TABLE _pgroll_del_{table}` |
| Rollback | Renames back to original name |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Table name to drop |

**Example:**

```json
{
  "type": "drop_table",
  "table": "legacy_events"
}
```

---

### `rename_table`

Renames a table. The rename is deferred to Complete so rollback is safe.

| Phase | Action |
|-------|--------|
| Start | no-op |
| Complete | `ALTER TABLE {from} RENAME TO {to}` |
| Rollback | no-op |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `from` | string | yes | Current table name |
| `to` | string | yes | New table name |

**Example:**

```json
{
  "type": "rename_table",
  "from": "events",
  "to": "shows"
}
```

---

## Column Operations

### `add_column`

Adds a column to an existing table. Supports zero-downtime backfill via an `up` expression.

**Simple (no backfill):**

| Phase | Action |
|-------|--------|
| Start | `ALTER TABLE ... ADD COLUMN {name} {type}` |
| Complete | no-op |
| Rollback | `ALTER TABLE ... DROP COLUMN {name}` |

**With `up` expression (expand/contract):**

| Phase | Action |
|-------|--------|
| Start | Add temporary column, install trigger to keep it in sync, backfill existing rows, create version schema |
| Complete | Rename temporary column to final name, drop version schema |
| Rollback | Drop trigger, version schema, and temporary column |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Target table |
| `column` | ColumnDefinition | yes | Column to add |
| `up` | string | no | SQL expression to backfill existing rows (enables expand/contract pattern) |
| `down` | string | no | SQL expression for old-code compatibility during the migration window |

**Example — simple:**

```json
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
```

**Example — with backfill:**

```json
{
  "type": "add_column",
  "table": "users",
  "column": {
    "name": "full_name",
    "type": "text",
    "nullable": true
  },
  "up": "first_name || ' ' || last_name"
}
```

---

### `drop_column`

Removes a column. The column is not deleted until Complete, allowing rollback.

| Phase | Action |
|-------|--------|
| Start | no-op |
| Complete | `ALTER TABLE ... DROP COLUMN {column}` |
| Rollback | no-op |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Target table |
| `column` | string | yes | Column to drop |
| `down` | string | no | SQL expression to populate the column if the migration is rolled back |

**Example:**

```json
{
  "type": "drop_column",
  "table": "users",
  "column": "legacy_token"
}
```

---

### `rename_column`

Renames a column. Deferred to Complete.

| Phase | Action |
|-------|--------|
| Start | no-op |
| Complete | `ALTER TABLE ... RENAME COLUMN {from} TO {to}` |
| Rollback | no-op |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Target table |
| `from` | string | yes | Current column name |
| `to` | string | yes | New column name |

**Example:**

```json
{
  "type": "rename_column",
  "table": "users",
  "from": "firstname",
  "to": "first_name"
}
```

---

### `alter_column`

Modifies an existing column: change type, nullability, default value, uniqueness, or add a check constraint. Always uses the expand/contract pattern — a duplicate column is created, kept in sync via trigger, and the original swapped out at Complete.

| Phase | Action |
|-------|--------|
| Start | Add duplicate column, install sync trigger, backfill, create version schema |
| Complete | Drop version schema, drop original column, rename duplicate to original name |
| Rollback | Drop trigger, version schema, and duplicate column |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Target table |
| `column` | string | yes | Column to alter |
| `up` | string | no | SQL expression for populating the new column from old values |
| `down` | string | no | SQL expression for backfilling old column from new values during rollback |
| `name` | string | no | New column name (renames the column) |
| `data_type` | string | no | New PostgreSQL type |
| `not_null` | bool | no | Add NOT NULL constraint |
| `unique` | bool | no | Add UNIQUE constraint |
| `default` | string | no | New default SQL expression |
| `check` | string | no | Check constraint SQL expression |

**Example — change type and backfill:**

```json
{
  "type": "alter_column",
  "table": "orders",
  "column": "total_cents",
  "data_type": "bigint",
  "up": "total_cents::bigint",
  "down": "total_cents::integer"
}
```

**Example — add NOT NULL:**

```json
{
  "type": "alter_column",
  "table": "users",
  "column": "email",
  "not_null": true,
  "up": "COALESCE(email, 'unknown@example.com')"
}
```

---

## Index Operations

### `create_index`

Creates an index using `CREATE INDEX CONCURRENTLY` to avoid locking.

| Phase | Action |
|-------|--------|
| Start | `CREATE INDEX CONCURRENTLY {name} ON {table} ({columns})` |
| Complete | no-op |
| Rollback | `DROP INDEX CONCURRENTLY {name}` |

> Requires a non-transactional (autocommit) connection.

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | Index name |
| `table` | string | yes | Target table |
| `columns` | string[] | yes | Columns to index |
| `unique` | bool | no | Create a unique index |

**Example:**

```json
{
  "type": "create_index",
  "name": "IX_orders_user_id",
  "table": "orders",
  "columns": ["user_id"],
  "unique": false
}
```

---

### `drop_index`

Drops an index. Deferred to Complete.

| Phase | Action |
|-------|--------|
| Start | no-op |
| Complete | `DROP INDEX CONCURRENTLY {name}` |
| Rollback | no-op |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | Index name to drop |

**Example:**

```json
{
  "type": "drop_index",
  "name": "IX_orders_legacy_ref"
}
```

---

## Constraint Operations

### `create_constraint`

Adds a constraint of type `check`, `unique`, or `foreign_key`.

- `check` and `foreign_key` constraints are added as `NOT VALID` during Start (fast, non-locking) and validated during Complete.
- `unique` constraints are added immediately during Start (no `NOT VALID` support in PostgreSQL).

| Phase | Action |
|-------|--------|
| Start | `ADD CONSTRAINT ... NOT VALID` (check/FK) or `ADD CONSTRAINT` (unique) |
| Complete | `ALTER TABLE ... VALIDATE CONSTRAINT` (check/FK only) |
| Rollback | `DROP CONSTRAINT` |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Target table |
| `name` | string | yes | Constraint name |
| `constraint_type` | string | yes | `"check"`, `"unique"`, or `"foreign_key"` |
| `check` | string | for check | SQL check expression (e.g. `price > 0`) |
| `columns` | string[] | for unique/FK | Columns involved in the constraint |
| `references_table` | string | for FK | Referenced table |
| `references_columns` | string[] | for FK | Referenced columns |

**Example — check:**

```json
{
  "type": "create_constraint",
  "table": "products",
  "name": "CK_products_price_positive",
  "constraint_type": "check",
  "check": "price > 0"
}
```

**Example — unique:**

```json
{
  "type": "create_constraint",
  "table": "users",
  "name": "UQ_users_email",
  "constraint_type": "unique",
  "columns": ["email"]
}
```

**Example — foreign key:**

```json
{
  "type": "create_constraint",
  "table": "orders",
  "name": "FK_orders_users",
  "constraint_type": "foreign_key",
  "columns": ["user_id"],
  "references_table": "users",
  "references_columns": ["id"]
}
```

---

### `drop_constraint`

Removes a constraint. Deferred to Complete.

| Phase | Action |
|-------|--------|
| Start | no-op |
| Complete | `ALTER TABLE ... DROP CONSTRAINT {name}` |
| Rollback | no-op |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Target table |
| `name` | string | yes | Constraint name to drop |

**Example:**

```json
{
  "type": "drop_constraint",
  "table": "orders",
  "name": "FK_orders_legacy_customers"
}
```

---

### `rename_constraint`

Renames a constraint. Deferred to Complete.

| Phase | Action |
|-------|--------|
| Start | no-op |
| Complete | `ALTER TABLE ... RENAME CONSTRAINT {from} TO {to}` |
| Rollback | no-op |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Target table |
| `from` | string | yes | Current constraint name |
| `to` | string | yes | New constraint name |

**Example:**

```json
{
  "type": "rename_constraint",
  "table": "orders",
  "from": "orders_user_id_fkey",
  "to": "FK_orders_users"
}
```

---

## Column Constraint Operations

These operations modify nullability or defaults on existing columns. They are simple, fast DDL changes with no backfill or version schema overhead.

### `set_not_null`

Adds a `NOT NULL` constraint to a column.

| Phase | Action |
|-------|--------|
| Start | `ALTER TABLE ... ALTER COLUMN ... SET NOT NULL` |
| Complete | no-op |
| Rollback | `ALTER TABLE ... ALTER COLUMN ... DROP NOT NULL` |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Target table |
| `column` | string | yes | Target column |

**Example:**

```json
{
  "type": "set_not_null",
  "table": "users",
  "column": "email"
}
```

---

### `drop_not_null`

Removes a `NOT NULL` constraint from a column.

| Phase | Action |
|-------|--------|
| Start | `ALTER TABLE ... ALTER COLUMN ... DROP NOT NULL` |
| Complete | no-op |
| Rollback | `ALTER TABLE ... ALTER COLUMN ... SET NOT NULL` |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Target table |
| `column` | string | yes | Target column |

**Example:**

```json
{
  "type": "drop_not_null",
  "table": "users",
  "column": "middle_name"
}
```

---

### `set_default`

Sets a default expression on a column.

| Phase | Action |
|-------|--------|
| Start | `ALTER TABLE ... ALTER COLUMN ... SET DEFAULT {value}` |
| Complete | no-op |
| Rollback | `ALTER TABLE ... ALTER COLUMN ... DROP DEFAULT` |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Target table |
| `column` | string | yes | Target column |
| `value` | string | yes | SQL default expression (e.g. `0`, `now()`, `'pending'`) |

**Example:**

```json
{
  "type": "set_default",
  "table": "orders",
  "column": "status",
  "value": "'pending'"
}
```

---

### `drop_default`

Removes the default expression from a column.

| Phase | Action |
|-------|--------|
| Start | `ALTER TABLE ... ALTER COLUMN ... DROP DEFAULT` |
| Complete | no-op |
| Rollback | no-op *(original default is not preserved — intentional)* |

> **Note:** Rollback is a deliberate no-op. The previous default value is not captured during Start, so it cannot be automatically restored. If you need rollback capability, record the original default and use a `set_default` operation instead.

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | yes | Target table |
| `column` | string | yes | Target column |

**Example:**

```json
{
  "type": "drop_default",
  "table": "products",
  "column": "status"
}
```

---

## Schema Operations

### `create_schema`

Creates a new PostgreSQL schema.

| Phase | Action |
|-------|--------|
| Start | `CREATE SCHEMA IF NOT EXISTS {schema}` |
| Complete | no-op |
| Rollback | `DROP SCHEMA IF EXISTS {schema} CASCADE` |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `schema` | string | yes | Schema name to create |

**Example:**

```json
{
  "type": "create_schema",
  "schema": "reporting"
}
```

---

### `drop_schema`

Drops a schema. Uses the soft-delete pattern during Start to allow rollback.

| Phase | Action |
|-------|--------|
| Start | Renames schema to `_pgroll_del_{schema}` |
| Complete | `DROP SCHEMA _pgroll_del_{schema} CASCADE` |
| Rollback | Renames back to original name |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `schema` | string | yes | Schema name to drop |

**Example:**

```json
{
  "type": "drop_schema",
  "schema": "legacy_reporting"
}
```

---

## Enum Operations

### `create_enum`

Creates a new PostgreSQL enum type.

| Phase | Action |
|-------|--------|
| Start | `CREATE TYPE {schema}.{name} AS ENUM (...)` |
| Complete | no-op |
| Rollback | `DROP TYPE IF EXISTS {schema}.{name}` |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | Type name |
| `values` | string[] | yes | Enum labels (must be unique) |

**Example:**

```json
{
  "type": "create_enum",
  "name": "order_status",
  "values": ["pending", "processing", "shipped", "delivered", "cancelled"]
}
```

---

### `drop_enum`

Drops an enum type. Uses the soft-delete pattern during Start to allow rollback.

| Phase | Action |
|-------|--------|
| Start | Renames type to `_pgroll_del_{name}` |
| Complete | `DROP TYPE IF EXISTS _pgroll_del_{name}` |
| Rollback | Renames back to original name |

> **Prerequisite:** All columns using this enum must be altered or dropped before the enum can be dropped.

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | Enum type name to drop |

**Example:**

```json
{
  "type": "drop_enum",
  "name": "legacy_status"
}
```

---

## View Operations

### `create_view`

Creates a view.

| Phase | Action |
|-------|--------|
| Start | `CREATE VIEW {schema}.{name} AS {definition}` |
| Complete | no-op |
| Rollback | `DROP VIEW IF EXISTS {schema}.{name}` |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | View name |
| `definition` | string | yes | SQL query body (without `CREATE VIEW ... AS`) |

**Example:**

```json
{
  "type": "create_view",
  "name": "active_users",
  "definition": "SELECT id, email, created_at FROM users WHERE deleted_at IS NULL"
}
```

---

### `drop_view`

Drops a view. Uses the soft-delete pattern during Start to allow rollback.

| Phase | Action |
|-------|--------|
| Start | Renames view to `_pgroll_del_{name}` |
| Complete | `DROP VIEW IF EXISTS _pgroll_del_{name}` |
| Rollback | Renames back to original name |

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | View name to drop |

**Example:**

```json
{
  "type": "drop_view",
  "name": "legacy_active_users"
}
```

---

## Raw SQL

### `raw_sql`

Executes arbitrary SQL. Useful as an escape hatch for operations not covered by the standard operation types.

| Phase | Action |
|-------|--------|
| Start | Executes `sql` |
| Complete | no-op |
| Rollback | Executes `rollback_sql` if provided; otherwise no-op |

> **Warning:** `raw_sql` bypasses pgroll's safety guarantees. Use only for DDL not supported by other operation types, and always provide a `rollback_sql` if the change must be reversible.

**Fields:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `sql` | string | yes | SQL statement(s) to execute during Start |
| `rollback_sql` | string | no | SQL statement(s) to execute on Rollback |

**Example:**

```json
{
  "type": "raw_sql",
  "sql": "CREATE EXTENSION IF NOT EXISTS pg_trgm",
  "rollback_sql": "DROP EXTENSION IF EXISTS pg_trgm"
}
```

---

## Operation Summary

| Type | Start locking | RequiresConcurrentConnection | Rollback safe |
|------|:---:|:---:|:---:|
| **Table** | | | |
| `create_table` | — | no | yes |
| `drop_table` | — | no | yes |
| `rename_table` | no-op | no | yes |
| **Column** | | | |
| `add_column` (simple) | brief | no | yes |
| `add_column` (with `up`) | brief | yes | yes |
| `drop_column` | no-op | no | yes |
| `rename_column` | no-op | no | yes |
| `alter_column` | brief | yes | yes |
| **Column constraints** | | | |
| `set_not_null` | brief | no | yes |
| `drop_not_null` | brief | no | yes |
| `set_default` | brief | no | yes |
| `drop_default` | brief | no | no ¹ |
| **Index** | | | |
| `create_index` | none | yes | yes |
| `drop_index` | no-op | no | yes |
| **Constraint** | | | |
| `create_constraint` (check/FK) | brief | no | yes |
| `create_constraint` (unique) | brief | no | yes |
| `drop_constraint` | no-op | no | yes |
| `rename_constraint` | no-op | no | yes |
| **Schema** | | | |
| `create_schema` | — | no | yes |
| `drop_schema` | — | no | yes |
| **Enum** | | | |
| `create_enum` | — | no | yes |
| `drop_enum` | — | no | yes |
| **View** | | | |
| `create_view` | — | no | yes |
| `drop_view` | — | no | yes |
| **Raw SQL** | | | |
| `raw_sql` | depends | no | partial ² |

¹ `drop_default` rollback is a no-op — the original default value is not preserved.
² `raw_sql` rollback executes `rollback_sql` if provided; otherwise it is a no-op.
