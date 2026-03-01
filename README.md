# pgroll.NET

[![CI](https://github.com/gpatane/pgrool/actions/workflows/ci.yml/badge.svg)](https://github.com/gpatane/pgrool/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/PgRoll.PostgreSQL.svg)](https://www.nuget.org/packages/PgRoll.PostgreSQL)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

Zero-downtime PostgreSQL schema migrations for .NET — a port of [pgroll](https://github.com/xataio/pgroll) to the .NET ecosystem.

## Features

- **Zero downtime** — old and new application versions run simultaneously during migrations
- **Instant rollback** — roll back any migration without data loss
- **20+ operations** — create/drop tables, columns, indexes, constraints, enums, views, schemas, and more
- **EF Core integration** — convert EF Core migrations to pgroll JSON automatically
- **CLI tool** — `pgroll init|start|complete|rollback|status|validate|migrate|pending|pull`
- **Offline validation** — validate migration files without a database connection
- **Advisory locks** — concurrent-safe execution across multiple instances

## Installation

### CLI tool

```bash
dotnet tool install -g PgRoll.Cli
```

### NuGet packages

```bash
dotnet add package PgRoll.PostgreSQL
dotnet add package PgRoll.EntityFrameworkCore   # optional EF Core integration
```

## Quick Start

### CLI

```bash
# Initialize pgroll state in your database
pgroll init --connection "Host=localhost;Database=mydb;Username=postgres"

# Start a migration
pgroll start migration.json --connection "..."

# Complete (make permanent) or roll back
pgroll complete --connection "..."
pgroll rollback --connection "..."

# Apply all pending migrations from a directory
pgroll migrate ./migrations --connection "..."
```

### Migration file format

```json
{
  "name": "01_create_users",
  "operations": [
    {
      "type": "create_table",
      "table": "users",
      "columns": [
        { "name": "id",    "type": "serial",       "primaryKey": true },
        { "name": "email", "type": "varchar(255)",  "unique": true, "nullable": false },
        { "name": "name",  "type": "text" }
      ]
    }
  ]
}
```

### .NET API

```csharp
using PgRoll.Core;
using PgRoll.PostgreSQL;

var connectionString = "Host=localhost;Database=mydb;Username=postgres";

var stateStore  = new PgStateStore(connectionString);
var schemaReader = new PgSchemaReader(connectionString);
var executor    = new PgMigrationExecutor(stateStore, schemaReader, connectionString);

await executor.InitAsync();
await executor.StartAsync(migration);
await executor.CompleteAsync();
```

### EF Core integration

```bash
# Convert an EF Core migration assembly to pgroll JSON files
pgroll efcore convert --assembly MyApp.Migrations.dll --output ./pgroll-migrations
```

Or use the API directly:

```csharp
using PgRoll.EntityFrameworkCore;

var converter = new EfCoreMigrationConverter();
var result = converter.Convert("AddUserTable", efCoreMigrationOperations);

// result.Migration  — pgroll Migration ready to execute
// result.Skipped    — list of operation types that couldn't be converted
```

## Documentation

Full documentation: [https://github.com/gpatane/pgrool](https://github.com/gpatane/pgrool)

## License

MIT — see [LICENSE](LICENSE).
