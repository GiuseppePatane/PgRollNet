---
title: EF Core Integration
description: Convert EF Core migrations to pgroll JSON. CLI and library usage, type mapping, adoption workflow.
outline: deep
---

# EF Core Integration

pgroll provides two integration points for teams using EF Core Migrations:

1. **`pgroll efcore convert`** — CLI command that reads a compiled migrations assembly and writes pgroll JSON files
2. **`PgRoll.EntityFrameworkCore`** — .NET library for programmatic conversion

---

## CLI: `pgroll efcore convert`

The simplest way to migrate an existing EF Core project to pgroll.

### Requirements

- A compiled migrations assembly (`.dll`) built with `dotnet build` or `dotnet publish`
- All migration classes must be in that assembly (the default EF Core layout)

### Usage

```bash
pgroll efcore convert \
  --assembly <path-to-dll> \
  [--output <output-dir>] \
  [--filter <name-substring>]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--assembly` | *(required)* | Path to the compiled EF Core migrations assembly |
| `--output` | `pgroll-migrations` | Output directory (created if it does not exist) |
| `--filter` | | Only convert migrations whose name contains this string (case-insensitive) |

### Finding the assembly

After `dotnet build`, the migrations DLL is typically in:

```
bin/Debug/net8.0/MyApp.Migrations.dll
```

If your migrations project is a library referenced by a runnable project (API, Worker, etc.), you can also point at the binary output of the runnable project — its `bin/` directory will contain a copy with all NuGet dependencies alongside it.

```bash
# From the migrations-only project bin (deps resolved via .deps.json + NuGet cache)
pgroll efcore convert \
  --assembly src/MyApp.Migrations/bin/Release/net8.0/MyApp.Migrations.dll \
  --output pgroll-migrations

# From the API project bin (all DLLs are present)
pgroll efcore convert \
  --assembly src/MyApp.Api/bin/Debug/net8.0/MyApp.Migrations.dll \
  --output pgroll-migrations
```

The tool automatically resolves NuGet dependencies in both cases using the `.deps.json` file and the global NuGet packages cache (`~/.nuget/packages/` or `$NUGET_PACKAGES`).

### What gets converted

| EF Core operation | pgroll operation |
|-------------------|-----------------|
| `CreateTableOperation` | `create_table` (columns + PK; emits extra `create_constraint` for unique/FK/check constraints) |
| `DropTableOperation` | `drop_table` |
| `RenameTableOperation` | `rename_table` |
| `AddColumnOperation` | `add_column` (`up = null`) |
| `DropColumnOperation` | `drop_column` |
| `RenameColumnOperation` | `rename_column` |
| `AlterColumnOperation` | `alter_column` (DataType/NotNull/Default if changed; `up = null`) |
| `CreateIndexOperation` | `create_index` |
| `DropIndexOperation` | `drop_index` |
| `AddCheckConstraintOperation` | `create_constraint` type=`check` |
| `DropCheckConstraintOperation` | `drop_constraint` |
| `AddUniqueConstraintOperation` | `create_constraint` type=`unique` |
| `DropUniqueConstraintOperation` | `drop_constraint` |
| `AddForeignKeyOperation` | `create_constraint` type=`foreign_key` |
| `DropForeignKeyOperation` | `drop_constraint` |
| `SqlOperation` | `raw_sql` |

Operations with no pgroll equivalent (data seeding, sequences, schema ops) are **skipped** and reported in the summary output.

### Missing `up`/`down` expressions

The converter sets `up = null` for `add_column` and `alter_column`. This is intentional: the correct backfill expression depends on your application's business logic and pgroll cannot infer it automatically.

After conversion, review the generated files and add `up` expressions where needed for zero-downtime backfill:

```json
{
  "type": "add_column",
  "table": "users",
  "column": { "name": "full_name", "type": "text", "nullable": true },
  "up": "first_name || ' ' || last_name"
}
```

### Raw SQL, Functions and Triggers

In EF Core migrations, calls to `migrationBuilder.Sql(...)` produce a `SqlOperation`. These are automatically converted to pgroll `raw_sql` operations.

This is the recommended way to create or drop PostgreSQL functions, triggers, stored procedures, and any other DDL not covered by the standard EF Core operations:

```csharp
// EF Core migration Up() method
protected override void Up(MigrationBuilder migrationBuilder)
{
    // 1. Create a table the normal way
    migrationBuilder.CreateTable("orders", t => new { ... });

    // 2. Create a PostgreSQL function via raw SQL
    migrationBuilder.Sql("""
        CREATE OR REPLACE FUNCTION update_updated_at()
        RETURNS TRIGGER LANGUAGE plpgsql AS $$
        BEGIN
          NEW.updated_at = now();
          RETURN NEW;
        END;
        $$
        """);

    // 3. Attach the trigger
    migrationBuilder.Sql("""
        CREATE TRIGGER trg_orders_updated_at
        BEFORE UPDATE ON orders
        FOR EACH ROW EXECUTE FUNCTION update_updated_at()
        """);
}
```

After conversion, the generated pgroll JSON will contain three operations in order:

```json
{
  "name": "..._create_orders",
  "operations": [
    { "type": "create_table", "table": "orders", "columns": [ ... ] },
    {
      "type": "raw_sql",
      "sql": "CREATE OR REPLACE FUNCTION update_updated_at() ..."
    },
    {
      "type": "raw_sql",
      "sql": "CREATE TRIGGER trg_orders_updated_at ..."
    }
  ]
}
```

> **Rollback SQL:** The EF Core `SqlOperation` has no built-in rollback concept. After conversion, you can optionally add a `"rollback_sql"` field manually to each `raw_sql` block in the generated JSON (e.g. `"DROP FUNCTION IF EXISTS update_updated_at()"`). See [raw_sql](./operations#raw_sql) in the Operations Reference.

---

## Library: `PgRoll.EntityFrameworkCore`

For programmatic conversion from within .NET code.

### Installation

```bash
dotnet add package PgRoll.EntityFrameworkCore
```

### `EfCoreMigrationConverter`

```csharp
using PgRoll.EntityFrameworkCore;

// From a list of MigrationOperation objects
ConversionResult result = EfCoreMigrationConverter.Convert(
    name: "20250801_add_email_verified",
    operations: myMigration.UpOperations);

// From an Action<MigrationBuilder> (EF Core DSL)
ConversionResult result = EfCoreMigrationConverter.Convert(
    name: "20250801_add_email_verified",
    upAction: b =>
    {
        b.AddColumn<bool>(
            name: "EmailVerified",
            table: "Users",
            nullable: false,
            defaultValue: false);
    });

// Inspect results
Migration migration = result.Migration;          // pgroll Migration object
IReadOnlyList<string> skipped = result.Skipped;  // names of unsupported operations

// Serialize to JSON
string json = migration.Serialize();
File.WriteAllText("migration.json", json);
```

### `MigrationBuilderExtensions`

Adds an extension method directly on `MigrationBuilder`:

```csharp
using PgRoll.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

// Inside a MigrationBuilder-based method
MigrationBuilder builder = ...;
Migration pgrollMigration = builder.ToPgRollMigration("my_migration_name");
```

### Type Mapping

The library maps .NET CLR types to PostgreSQL column types:

| CLR Type | PostgreSQL type |
|----------|----------------|
| `string` | `text` |
| `int` / `Int32` | `integer` |
| `long` / `Int64` | `bigint` |
| `short` / `Int16` | `smallint` |
| `bool` | `boolean` |
| `Guid` | `uuid` |
| `decimal` | `numeric` |
| `double` | `double precision` |
| `float` | `real` |
| `byte[]` | `bytea` |
| `DateTime` | `timestamp with time zone` |
| `DateTimeOffset` | `timestamp with time zone` |
| `DateOnly` | `date` |
| `TimeOnly` | `time` |
| Nullable\<T> | same as T |
| *(anything else)* | `text` |

If an explicit `ColumnType` string is already set on the EF Core operation (e.g. Npgsql sets `"text[]"`, `"jsonb"`, `"uuid"`) it takes priority over the CLR type mapping.

---

## Adoption Workflow

### Step 1 — Convert existing migrations

```bash
pgroll efcore convert \
  --assembly bin/Release/net8.0/MyApp.Migrations.dll \
  --output pgroll-migrations
```

### Step 2 — Review and enrich

Open the generated JSON files. For any `add_column` or `alter_column` without an `up` expression, decide whether you need zero-downtime backfill and add the expression if so.

### Step 3 — Apply to database

Use `pgroll migrate` to apply all converted migrations at once (initial catch-up), or `pgroll start` / `pgroll complete` for the zero-downtime expand/contract workflow going forward.

```bash
# Catch-up: apply all at once
pgroll migrate ./pgroll-migrations \
  --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"
```

### Step 4 — Remove EF Core migration tracking

Since pgroll now manages the schema, you can stop calling `Database.MigrateAsync()` from your application. Optionally keep `__EFMigrationsHistory` for reference but do not let EF Core apply new migrations.

### Step 5 — Write new migrations in pgroll JSON

Future schema changes are authored as pgroll JSON files instead of EF Core migrations.

---

## Compatibility

The CLI converter uses .NET's `AssemblyLoadContext` in isolated mode and matches EF Core operations by class name (not by type identity), so it works with EF Core **7.x, 8.x, 9.x and later** without any changes.

The `PgRoll.EntityFrameworkCore` library depends on `Microsoft.EntityFrameworkCore.Relational` version 9.x and targets the types directly, so it requires EF Core 9+.
