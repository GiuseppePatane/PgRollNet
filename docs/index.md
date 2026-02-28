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
      text: CLI Reference
      link: /cli-reference
    - theme: alt
      text: View on GitHub
      link: https://github.com/your-org/pgrool

features:
  - icon: 🔄
    title: Zero Downtime
    details: Old and new schemas coexist during deployment. Both application versions work simultaneously — no maintenance windows.

  - icon: 🛡️
    title: Safe Rollback
    details: Every migration can be rolled back before Complete. Soft-delete for tables, expand/contract for columns, NOT VALID for constraints.

  - icon: ⚡
    title: Non-Blocking
    details: Indexes are created CONCURRENTLY. Constraints are added NOT VALID and validated separately. Backfill is batched with SKIP LOCKED.

  - icon: 🔌
    title: EF Core Integration
    details: Convert your existing EF Core migrations to pgroll JSON in a single command. Works with EF Core 7, 8, 9 and later.

  - icon: 🚀
    title: CD-Ready
    details: pgroll pending exits with code 1 when migrations are pending — plug it directly into GitHub Actions, Azure DevOps, or Kubernetes init containers.

  - icon: 🏗️
    title: .NET Native
    details: Built on .NET 10 with Npgsql. Available as a dotnet global tool (pgroll) and as NuGet libraries (PgRoll.PostgreSQL, PgRoll.EntityFrameworkCore).
---

## Quick Start

```bash
# Install
dotnet tool install -g pgroll

# Initialize pgroll in your database
pgroll init --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret"

# Start a migration
pgroll start 001_create_users.json --connection "..."

# Complete when your new app version is deployed and healthy
pgroll complete --connection "..."
```

## How it works

pgroll uses the **expand/contract** pattern. Every schema change has three phases:

| Phase | What happens |
|-------|-------------|
| **Start** | New structure added alongside the old one. Trigger installed for dual-write. Existing rows backfilled. |
| **Complete** | Old structure removed. Trigger dropped. Version schema dropped. |
| **Rollback** | New structure removed. Database restored to pre-Start state. |

Between Start and Complete both old and new application versions run against the same database — each reading from their own [version schema](/architecture#version-schemas).
