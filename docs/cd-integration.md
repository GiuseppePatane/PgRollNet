---
title: CD Integration
description: Integrate pgroll.NET into GitHub Actions, Azure DevOps, and Kubernetes pipelines with `pgroll-net pending` and `pgroll-net migrate`.
outline: deep
---

# CD Integration

pgroll.NET is designed to fit into standard CD pipelines. The key tool is `pgroll-net pending`, which returns exit code `1` when there are migrations to apply, letting the pipeline decide whether to run the migration step at all.

## Exit Codes Reference

| Command | Exit 0 | Exit 1 | Exit 2 |
|---------|--------|--------|--------|
| `pending` | up to date | migrations pending | error |
| `migrate` | all applied | — | error |
| `start` | started | — | error |
| `complete` | completed | — | error |
| `rollback` | rolled back | — | error |
| `validate` | valid | invalid | error |

---

## Strategies

### Strategy A — Atomic (simple, recommended to start with)

Apply `start + complete` in sequence before deployment. It does not use the full expand/contract pattern, but it is safer than raw DDL because it still relies on safer PostgreSQL primitives such as `CREATE INDEX CONCURRENTLY` and `NOT VALID/VALIDATE`.

```
check pending → migrate (start+complete) → deploy app
```

If there are no pending migrations, the deployment only rolls out the application.

### Strategy B — Expand/Contract (true zero-downtime)

This requires real blue-green infrastructure with two application slots and a load balancer with health checks:

```
start migration → deploy new app version → health check OK → complete
                                         ↓ health check KO
                                         rollback
```

---

## Examples

### GitHub Actions — Strategy A

```yaml
# .github/workflows/deploy.yml
name: Deploy

on:
  push:
    branches: [main]

jobs:
  deploy:
    runs-on: ubuntu-latest
    concurrency:
      group: production-deploy
      cancel-in-progress: false
    env:
      DB_CONN: ${{ secrets.DB_CONNECTION }}

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.x'

      - name: Install pgroll-net
        run: dotnet tool install -g PgRoll.Cli

      - name: Guard against active migration
        run: |
          STATUS=$(pgroll-net status --connection "$DB_CONN")
          if echo "$STATUS" | grep -q "Active migration"; then
            echo "ERROR: a migration is already active. Complete or rollback it first."
            exit 2
          fi

      - name: Initialize pgroll state (idempotent)
        run: pgroll-net init --connection "$DB_CONN"

      - name: Check pending migrations
        id: pending
        run: |
          if pgroll-net pending ./migrations --connection "$DB_CONN"; then
            echo "has_pending=false" >> $GITHUB_OUTPUT
          else
            echo "has_pending=true" >> $GITHUB_OUTPUT
          fi

      - name: Apply migrations
        if: steps.pending.outputs.has_pending == 'true'
        run: pgroll-net migrate ./migrations --connection "$DB_CONN"

      - name: Deploy application
        run: |
          # kubectl set image deployment/myapp myapp=myrepo/myapp:${{ github.sha }}
          # or: helm upgrade myapp ./chart --set image.tag=${{ github.sha }}
          kubectl rollout status deployment/myapp --timeout=5m
```

### GitHub Actions — Strategy B (Expand/Contract)

```yaml
jobs:
  deploy:
    runs-on: ubuntu-latest
    concurrency:
      group: production-deploy
      cancel-in-progress: false
    env:
      DB_CONN: ${{ secrets.DB_CONNECTION }}

    steps:
      - uses: actions/checkout@v4

      - name: Install pgroll-net
        run: dotnet tool install -g PgRoll.Cli

      - name: Guard against active migration
        run: |
          STATUS=$(pgroll-net status --connection "$DB_CONN")
          if echo "$STATUS" | grep -q "Active migration"; then
            echo "ERROR: a migration is already active. Complete or rollback it first."
            exit 2
          fi

      - name: Initialize pgroll state
        run: pgroll-net init --connection "$DB_CONN"

      - name: Check pending migrations
        id: pending
        run: |
          if pgroll-net pending ./migrations --connection "$DB_CONN"; then
            echo "has_pending=false" >> $GITHUB_OUTPUT
          else
            echo "has_pending=true" >> $GITHUB_OUTPUT
          fi

      # ── Start phase ──────────────────────────────────────────────────────────

      - name: Start migrations
        if: steps.pending.outputs.has_pending == 'true'
        run: |
          for f in $(ls ./migrations/*.json | sort); do
            name=$(basename "$f" .json)
            if ! pgroll-net pending ./migrations --connection "$DB_CONN" | grep -q "$name"; then
              continue  # already applied
            fi
            pgroll-net start "$f" --connection "$DB_CONN"
          done

      # ── Deploy new app version ───────────────────────────────────────────────

      - name: Deploy application
        run: kubectl rollout status deployment/myapp --timeout=5m

      # ── Complete or Rollback ─────────────────────────────────────────────────

      - name: Complete migrations
        if: steps.pending.outputs.has_pending == 'true' && success()
        run: pgroll-net complete --connection "$DB_CONN"

      - name: Rollback migrations on failure
        if: steps.pending.outputs.has_pending == 'true' && failure()
        run: |
          STATUS=$(pgroll-net status --connection "$DB_CONN")
          if echo "$STATUS" | grep -q "Active migration"; then
            pgroll-net rollback --connection "$DB_CONN"
          fi
```

### Azure DevOps

```yaml
# azure-pipelines.yml
trigger:
  branches:
    include: [main]

variables:
  DB_CONN: $(DB_CONNECTION)

stages:
  - stage: Database
    displayName: 'Database Migrations'
    jobs:
      - job: Migrate
        steps:
          - task: UseDotNet@2
            inputs:
              version: '10.x'

          - script: dotnet tool install -g PgRoll.Cli
            displayName: 'Install pgroll-net'

          - script: |
              STATUS=$(pgroll-net status --connection "$(DB_CONN)")
              if echo "$STATUS" | grep -q "Active migration"; then
                echo "ERROR: a migration is already active. Complete or rollback it first."
                exit 2
              fi
            displayName: 'Guard against active migration'

          - script: pgroll-net init --connection "$(DB_CONN)"
            displayName: 'Initialize pgroll state'

          - script: |
              pgroll-net pending ./migrations --connection "$(DB_CONN)"
              echo "##vso[task.setvariable variable=hasPending;isOutput=true]$?"
            name: checkPending
            displayName: 'Check pending migrations'
            # Exit code 1 is expected when there are pending migrations — don't fail the step
            continueOnError: true

          - script: pgroll-net migrate ./migrations --connection "$(DB_CONN)"
            displayName: 'Apply migrations'
            condition: eq(variables['checkPending.hasPending'], '1')

  - stage: Deploy
    displayName: 'Deploy Application'
    dependsOn: Database
    jobs:
      - deployment: AppDeploy
        environment: production
        strategy:
          runOnce:
            deploy:
              steps:
                - script: |
                    kubectl set image deployment/myapp \
                      myapp=$(imageRepo):$(Build.BuildId)
                    kubectl rollout status deployment/myapp --timeout=5m
                  displayName: 'Rollout new version'
```

### Kubernetes Init Container

For Kubernetes-native pipelines, the init container applies migrations before the main pod starts.

```yaml
# deployment.yaml
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      initContainers:
        - name: pgroll-migrate
          image: mcr.microsoft.com/dotnet/sdk:10.0
          command:
            - sh
            - -c
            - |
              set -e
              dotnet tool install -g PgRoll.Cli
              export PATH="$PATH:$HOME/.dotnet/tools"
              STATUS=$(pgroll-net status --connection "$DB_CONN")
              if echo "$STATUS" | grep -q "Active migration"; then
                echo "ERROR: a migration is already active. Complete or rollback it first."
                exit 2
              fi
              pgroll-net init --connection "$DB_CONN"
              if ! pgroll-net pending /migrations --connection "$DB_CONN"; then
                pgroll-net migrate /migrations --connection "$DB_CONN"
              fi
          env:
            - name: DB_CONN
              valueFrom:
                secretKeyRef:
                  name: db-secret
                  key: connection-string
          volumeMounts:
            - name: migrations
              mountPath: /migrations

      containers:
        - name: myapp
          image: myrepo/myapp:latest
          # ...

      volumes:
        - name: migrations
          configMap:
            name: pgroll-migrations  # or a PVC, or baked into the image
```

---

## Recommended Patterns

### Block deployment if a migration is already active

```bash
STATUS=$(pgroll-net status --connection "$DB_CONN")
if echo "$STATUS" | grep -q "Active migration"; then
  echo "ERROR: a migration is already active. Complete or rollback it first."
  exit 2
fi
```

### Validate migrations in CI before merge

```yaml
# In the CI job (on every PR)
- name: Validate migrations
  run: |
    for f in ./migrations/*.json; do
      pgroll-net validate "$f" --connection "$DB_CONN_CI"
    done
```

### Naming convention for correct ordering

Use timestamp or numeric prefixes to ensure `pgroll-net pending` and `pgroll-net migrate` process files in the correct order:

```
migrations/
  20250101_001_create_users.json
  20250115_001_add_email_verified.json
  20250201_001_add_users_index.json
```

---

## What Not To Automate

| Action | Why |
|--------|--------|
| `pgroll-net complete` immediately after `start` | This cancels the benefit of expand/contract. Wait until deployment and health checks are confirmed OK. |
| Automatic `pgroll-net rollback` after `complete` | This is not possible. After `complete`, pgroll rollback is no longer available; you need a new reverse migration. |
| Applying migrations to shared environments without a lock guard | If multiple pipelines run in parallel, both may try `start` at the same time. Use `pgroll-net status` as a guard. |
