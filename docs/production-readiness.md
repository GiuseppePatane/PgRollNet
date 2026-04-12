---
title: Production Readiness
description: Verified environments, CI guarantees, operational checks, and release expectations for pgroll.NET.
---

# Production Readiness

## Verified Matrix

pgroll.NET is continuously validated in CI with:

- .NET 10 SDK
- PostgreSQL 14 (`postgres:14-alpine`)
- PostgreSQL 15 (`postgres:15-alpine`)
- PostgreSQL 16 (`postgres:16-alpine`)
- PostgreSQL 17 (`postgres:17-alpine`)

Minimum verified PostgreSQL version: 14.

The CI pipeline runs:

- full solution build
- full automated test suite
- CLI end-to-end tests
- NuGet packing for all packages
- packaged tool smoke tests via local `dotnet tool install`

## What Is Covered

- expand/contract lifecycle (`start`, `complete`, `rollback`)
- advisory lock safety under concurrent starts
- state-store invariants, including one active migration per schema
- EF Core conversion behavior
- CLI behavior for validation, pending detection, migrate semantics, and verbose output
- operational failures such as permission denial and failed deferred DDL during `complete`

## Release Expectations

Before publishing a release tag:

1. `dotnet test PgRoll.slnx` must pass locally.
2. CI must be green on every verified PostgreSQL version.
3. `dotnet pack` must succeed for all projects.
4. The packaged CLI must install and run from local NuGet output.
5. Documentation examples must match the shipped API and CLI surface.

## Operational Guidance

- Use `pgroll-net pending` in CI/CD to gate deployment when migrations are waiting.
- Use `--verbose` in staging and production rollout jobs to retain an execution trace.
- Prefer running migrations with a dedicated PostgreSQL role scoped to the target schema.
- Treat `start` and `complete` as separate deployment stages; do not collapse them unless your rollout strategy is designed for it.
- Keep migration files immutable once applied. Renaming a file is safe, but changing `name` is not.
