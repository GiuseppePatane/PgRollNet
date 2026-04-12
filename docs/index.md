---
layout: home

hero:
  name: pgroll.NET
  text: Zero-downtime PostgreSQL migrations
  tagline: Port of pgroll for .NET — expand/contract pattern, no locking, safe rollback.
  actions:
    - theme: brand
      text: Get Started
      link: /getting-started
    - theme: alt
      text: Production Readiness
      link: /production-readiness
    - theme: alt
      text: CLI Reference
      link: /cli-reference

features:
  - title: Zero Downtime
    details: Old and new schemas coexist during deployment. Both application versions work simultaneously — no maintenance windows.

  - title: Safe Rollback
    details: Every migration can be rolled back before Complete. Soft-delete for tables, expand/contract for columns, NOT VALID for constraints.

  - title: Non-Blocking
    details: Indexes are created CONCURRENTLY. Constraints are added NOT VALID and validated separately. Backfill is batched with SKIP LOCKED.

  - title: EF Core Integration
    details: Convert your existing EF Core migrations to pgroll JSON in a single command. Works with EF Core 7, 8, 9 and later.

  - title: CD-Ready
    details: pgroll-net pending exits with code 1 when migrations are pending — plug it directly into GitHub Actions, Azure DevOps, or Kubernetes init containers.

  - title: Operational Guardrails
    details: "`doctor`, `plan`, `inspect-active`, checksum validation, and batched backfill tuning make production rollouts easier to reason about and safer to recover."

  - title: .NET Native
    details: Built on .NET 10 with Npgsql. Available as a .NET global tool package (`PgRoll.Cli`, command `pgroll-net`) and as NuGet libraries (`PgRoll.PostgreSQL`, `PgRoll.EntityFrameworkCore`).

  - title: Verified Matrix
    details: CI validates the full build, test, packaging, and packaged-tool workflow against PostgreSQL 14, 15, 16, and 17. The minimum verified version is PostgreSQL 14.
---

## Quick Start

```bash
# Install
dotnet tool install -g PgRoll.Cli

# Initialize pgroll in your database
pgroll-net init --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"

# Scaffold a new migration file
pgroll-net new 001_create_users --output ./migrations

# Start a migration
pgroll-net start ./migrations/001_create_users.json --connection "..."

# Complete when your new app version is deployed and healthy
pgroll-net complete --connection "..."
```

## How it works

pgroll uses the **expand/contract** pattern. Every schema change has three phases:

| Phase | What happens |
|-------|-------------|
| **Start** | New structure added alongside the old one. Trigger installed for dual-write. Existing rows backfilled. |
| **Complete** | Old structure removed. Trigger dropped. Version schema dropped. |
| **Rollback** | New structure removed. Database restored to pre-Start state. |

Between Start and Complete both old and new application versions run against the same database — each reading from their own [version schema](/architecture#version-schemas).

For production workflows, use `doctor` as a preflight check, `plan` for reviewable execution previews, and `inspect-active` when you need to understand or recover an in-progress migration.
