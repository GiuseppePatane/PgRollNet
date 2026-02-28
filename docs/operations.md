---
title: Operations Reference
description: All 13 pgroll operations — table, column, index and constraint operations with JSON examples.
outline: deep
---

# Operations Reference

pgroll supports 13 schema operations grouped into four categories. Each operation has three phases: **Start**, **Complete**, and **Rollback**.

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

**Example:**

```json
{
  "type": "create_table",
  "table": "products",
  "columns": [
    { "name": "id", "type": "bigserial", "nullable": false, "primary_key": true },
    { "name": "name", "type": "text", "nullable": false },
    { "name": "price", "type": "numeric", "nullable": false, "default": "0" },
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

## Operation Summary

| Type | Start locking | RequiresConcurrentConnection | Rollback safe |
|------|:---:|:---:|:---:|
| `create_table` | — | no | yes |
| `drop_table` | — | no | yes |
| `rename_table` | no-op | no | yes |
| `add_column` (simple) | brief | no | yes |
| `add_column` (with `up`) | brief | yes | yes |
| `drop_column` | no-op | no | yes |
| `rename_column` | no-op | no | yes |
| `alter_column` | brief | yes | yes |
| `create_index` | none | yes | yes |
| `drop_index` | no-op | no | yes |
| `create_constraint` (check/FK) | brief | no | yes |
| `create_constraint` (unique) | brief | no | yes |
| `drop_constraint` | no-op | no | yes |
| `rename_constraint` | no-op | no | yes |
