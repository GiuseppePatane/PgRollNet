# PgRoll.NET — Claude Code Instructions

## Project Overview

PgRoll.NET is a .NET library and CLI tool for **zero-downtime PostgreSQL schema migrations**, inspired by [pgroll](https://github.com/xataio/pgroll). It provides expand/contract migrations with automatic backfill, triggers, and versioned schema views.

## Solution Structure

```
PgRoll.slnx
├── src/
│   ├── PgRoll.Core/                # Core abstractions, models, operations, errors
│   ├── PgRoll.PostgreSQL/          # PostgreSQL implementation (Npgsql)
│   ├── PgRoll.EntityFrameworkCore/  # EF Core migration converter
│   └── PgRoll.Cli/                 # CLI tool (System.CommandLine)
└── tests/
    ├── PgRoll.Core.Tests/           # Unit tests (validation, deserialization)
    ├── PgRoll.PostgreSQL.Tests/     # Integration tests (Testcontainers)
    └── PgRoll.EntityFrameworkCore.Tests/
```

**Dependency flow:** CLI → PostgreSQL → Core ← EntityFrameworkCore

## Build & Test

```bash
dotnet build              # Build all projects
dotnet test               # Run all tests (requires Docker for integration tests)
dotnet test --filter "FullyQualifiedName~Core.Tests"  # Unit tests only
dotnet pack               # Create NuGet packages
```

- **Target:** .NET 10, C# latest (LangVersion latest)
- **Central package management:** `Directory.Packages.props`
- **Warnings as errors:** enabled globally in `Directory.Build.props`

## Architecture

**Layered library** — Core abstractions with provider-specific implementations.

- `PgRoll.Core` — Zero dependencies (except logging abstractions + YamlDotNet). Defines `IMigrationOperation`, `IMigrationState`, models, errors, and validation.
- `PgRoll.PostgreSQL` — Npgsql-based implementation: `PgMigrationExecutor`, `PgStateStore`, `PgSchemaReader`, trigger/backfill management. Embedded SQL resources in `Sql/` folder.
- `PgRoll.EntityFrameworkCore` — Converts EF Core migrations to pgroll format.
- `PgRoll.Cli` — `pgroll-net` dotnet tool. Commands built with `System.CommandLine` using static `Build(GlobalOptions)` factory pattern.

## Coding Conventions

### Naming
- **Classes:** PascalCase. `Pg*` prefix for PostgreSQL implementations, `*Operation` suffix for migration ops, `*Error` suffix for exceptions.
- **Interfaces:** `I`-prefix (`IMigrationOperation`, `IMigrationState`).
- **Private fields:** `_camelCase` with underscore prefix.
- **Async methods:** `*Async` suffix. Every async method takes `CancellationToken ct = default`.
- **JSON properties:** `[JsonPropertyName("snake_case")]`.

### Patterns
- **Sealed by default** — all operation classes, error classes, records, and implementations are `sealed`.
- **Records for value objects** — `ValidationResult`, `StartResult`, `MigrationRecord`, `BackfillProgress`.
- **Classes for behavior** — `Migration`, `SchemaSnapshot`, `MigrationContext`.
- **Primary constructors** for error classes and simple types.
- **`required` + `init`** for mandatory properties on models.
- **Validation:** two-level — `ValidateStructure()` (offline) + `Validate(SchemaSnapshot)` (online). Returns `ValidationResult` record, not exceptions.
- **Errors:** sealed exception classes inheriting from `PgRollException`. Used for runtime failures, not validation.
- **Logging:** `ILogger<T>` with `NullLogger<T>.Instance` fallback. Structured logging with named placeholders.
- **No DI container** — constructor injection with optional parameters.
- **Section comments:** `// ── Section Name ──────────────` style dividers.

### CLI Commands
- Static `Build(GlobalOptions g)` method returns `Command`.
- `GlobalOptions` centralizes shared options (`--connection`, `--schema`, etc.).
- Exception handler in `Program.cs` uses pattern matching on exception types.
- User feedback via `Console.WriteLine` / `Console.Error.WriteLine`.

## Testing

- **Framework:** xUnit 2.x + FluentAssertions
- **Integration tests:** Testcontainers.PostgreSql with `IAsyncLifetime`, `[Collection("Postgres")]` shared fixtures
- **Test naming:** `MethodUnderTest_Scenario_ExpectedBehavior`
- **Test structure:** mirrors source project namespace
- **Unit tests** in Core.Tests (validation, deserialization); **integration tests** in PostgreSQL.Tests (full lifecycle against real DB)

## Key Files

- `Directory.Build.props` — Global build settings, package metadata
- `Directory.Packages.props` — Centralized NuGet versions
- `src/PgRoll.Core/Operations/` — All migration operation types
- `src/PgRoll.Core/Errors/` — Exception hierarchy
- `src/PgRoll.Core/Models/Migration.cs` — Migration model with JSON/YAML deserialization
- `src/PgRoll.PostgreSQL/PgMigrationExecutor.cs` — Main executor (start/complete/rollback)
- `src/PgRoll.PostgreSQL/Sql/` — Embedded SQL resources
- `src/PgRoll.Cli/Program.cs` — CLI entry point
- `src/PgRoll.Cli/Commands/` — Individual CLI commands
