---
title: Migration File Format
description: JSON schema, naming conventions, column definitions and serialization API.
---

# Migration File Format

A pgroll migration is a JSON file with two top-level fields.

## Schema

```json
{
  "name": "<migration_name>",
  "operations": [ <operation>, ... ]
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | Unique migration identifier. Used as the key in `pgroll.migrations`. |
| `operations` | array | yes | One or more operation objects, executed in order. |

Every operation object **must** start with a `"type"` field that identifies the operation kind. All other field names use `snake_case`.

## Naming Convention

Migration file names are typically prefixed with a timestamp or sequential number so they sort correctly when using `pgroll migrate`:

```
001_create_users.json
002_add_email_verified.json
003_add_users_role_index.json
20250801_165455_add_replacement_event.json
```

The `name` field inside the JSON must match (or at least be unique across all migrations applied to the same schema).

## Column Definition

Several operations accept a `column` or `columns[]` object:

```json
{
  "name": "email",
  "type": "text",
  "nullable": false,
  "default": "''",
  "primary_key": false
}
```

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Column name |
| `type` | string | PostgreSQL type (e.g. `text`, `integer`, `uuid`, `timestamp with time zone`) |
| `nullable` | bool | Whether the column allows NULL |
| `default` | string | SQL default expression (e.g. `now()`, `0`, `gen_random_uuid()`) |
| `primary_key` | bool | Mark column as part of the primary key |

## Full Example

```json
{
  "name": "20250801_create_orders",
  "operations": [
    {
      "type": "create_table",
      "table": "orders",
      "columns": [
        { "name": "id", "type": "bigserial", "nullable": false, "primary_key": true },
        { "name": "user_id", "type": "bigint", "nullable": false },
        { "name": "total", "type": "numeric", "nullable": false, "default": "0" },
        { "name": "status", "type": "text", "nullable": false, "default": "'pending'" },
        { "name": "created_at", "type": "timestamp with time zone", "nullable": false, "default": "now()" }
      ]
    },
    {
      "type": "create_index",
      "name": "IX_orders_user_id",
      "table": "orders",
      "columns": ["user_id"],
      "unique": false
    },
    {
      "type": "create_constraint",
      "table": "orders",
      "name": "FK_orders_users",
      "constraint_type": "foreign_key",
      "columns": ["user_id"],
      "references_table": "users",
      "references_columns": ["id"]
    }
  ]
}
```

## Serialization API

From .NET code, use the `Migration` model in `PgRoll.Core`:

```csharp
// Deserialize from JSON
var migration = Migration.Deserialize(jsonString);

// Serialize back to JSON
var json = migration.Serialize();
```
