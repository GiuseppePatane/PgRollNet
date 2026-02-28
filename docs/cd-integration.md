---
title: CD Integration
description: Integrate pgroll into GitHub Actions, Azure DevOps and Kubernetes pipelines with pgroll pending and migrate.
outline: deep
---

# CD Integration

pgroll is designed to fit into standard CD pipelines. The key tool is `pgroll pending`, which returns exit code `1` when there are migrations to apply, letting the pipeline decide whether to run the migration step at all.

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

### Strategy A — Atomico (semplice, consigliato per iniziare)

Applica `start + complete` in sequenza prima del deploy. Non sfrutta l'expand/contract ma è più sicuro del DDL nudo (usa `CREATE INDEX CONCURRENTLY`, `NOT VALID/VALIDATE`, ecc.).

```
check pending → migrate (start+complete) → deploy app
```

Se non ci sono migration pending, il deploy si limita al rollout dell'app.

### Strategy B — Expand/Contract (zero-downtime vero)

Richiede una vera infrastruttura blue-green (due slot applicativi, load balancer con health check):

```
start migration → deploy new app version → health check OK → complete
                                         ↓ health check KO
                                         rollback
```

---

## Esempi

### GitHub Actions — Strategia A

```yaml
# .github/workflows/deploy.yml
name: Deploy

on:
  push:
    branches: [main]

jobs:
  deploy:
    runs-on: ubuntu-latest
    env:
      DB_CONN: ${{ secrets.DB_CONNECTION }}

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.x'

      - name: Install pgroll
        run: dotnet tool install -g pgroll

      - name: Init pgroll (idempotente)
        run: pgroll init --connection "$DB_CONN"

      - name: Check pending migrations
        id: pending
        run: |
          if pgroll pending ./migrations --connection "$DB_CONN"; then
            echo "has_pending=false" >> $GITHUB_OUTPUT
          else
            echo "has_pending=true" >> $GITHUB_OUTPUT
          fi

      - name: Apply migrations
        if: steps.pending.outputs.has_pending == 'true'
        run: pgroll migrate ./migrations --connection "$DB_CONN"

      - name: Deploy application
        run: |
          # kubectl set image deployment/myapp myapp=myrepo/myapp:${{ github.sha }}
          # oppure: helm upgrade myapp ./chart --set image.tag=${{ github.sha }}
          kubectl rollout status deployment/myapp --timeout=5m
```

### GitHub Actions — Strategia B (Expand/Contract)

```yaml
jobs:
  deploy:
    runs-on: ubuntu-latest
    env:
      DB_CONN: ${{ secrets.DB_CONNECTION }}

    steps:
      - uses: actions/checkout@v4

      - name: Install pgroll
        run: dotnet tool install -g pgroll

      - name: Init pgroll
        run: pgroll init --connection "$DB_CONN"

      - name: Check pending migrations
        id: pending
        run: |
          if pgroll pending ./migrations --connection "$DB_CONN"; then
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
            if ! pgroll pending ./migrations --connection "$DB_CONN" | grep -q "$name"; then
              continue  # already applied
            fi
            pgroll start "$f" --connection "$DB_CONN"
          done

      # ── Deploy new app version ───────────────────────────────────────────────

      - name: Deploy application
        run: kubectl rollout status deployment/myapp --timeout=5m

      # ── Complete or Rollback ─────────────────────────────────────────────────

      - name: Complete migrations
        if: steps.pending.outputs.has_pending == 'true' && success()
        run: pgroll complete --connection "$DB_CONN"

      - name: Rollback migrations on failure
        if: steps.pending.outputs.has_pending == 'true' && failure()
        run: |
          STATUS=$(pgroll status --connection "$DB_CONN")
          if echo "$STATUS" | grep -q "Active migration"; then
            pgroll rollback --connection "$DB_CONN"
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

          - script: dotnet tool install -g pgroll
            displayName: 'Install pgroll'

          - script: pgroll init --connection "$(DB_CONN)"
            displayName: 'Init pgroll'

          - script: |
              pgroll pending ./migrations --connection "$(DB_CONN)"
              echo "##vso[task.setvariable variable=hasPending;isOutput=true]$?"
            name: checkPending
            displayName: 'Check pending migrations'
            # Exit code 1 is expected when there are pending migrations — don't fail the step
            continueOnError: true

          - script: pgroll migrate ./migrations --connection "$(DB_CONN)"
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

Per pipeline Kubernetes-native, l'init container applica le migration prima che il pod principale parta.

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
              dotnet tool install -g pgroll
              export PATH="$PATH:$HOME/.dotnet/tools"
              pgroll init --connection "$DB_CONN"
              if ! pgroll pending /migrations --connection "$DB_CONN"; then
                pgroll migrate /migrations --connection "$DB_CONN"
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
            name: pgroll-migrations  # oppure un PVC, oppure baked nell'immagine
```

---

## Pattern Consigliati

### Bloccare il deploy se c'è già una migration attiva

```bash
STATUS=$(pgroll status --connection "$DB_CONN")
if echo "$STATUS" | grep -q "Active migration"; then
  echo "ERROR: a migration is already active. Complete or rollback it first."
  exit 2
fi
```

### Validare le migration in CI prima del merge

```yaml
# Nel job di CI (su ogni PR)
- name: Validate migrations
  run: |
    for f in ./migrations/*.json; do
      pgroll validate "$f" --connection "$DB_CONN_CI"
    done
```

### Naming convention per ordinamento corretto

Usa prefissi timestamp o numerici per garantire che `pgroll pending` e `pgroll migrate` processino i file nell'ordine giusto:

```
migrations/
  20250101_001_create_users.json
  20250115_001_add_email_verified.json
  20250201_001_add_users_index.json
```

---

## Cosa non automatizzare

| Azione | Perché |
|--------|--------|
| `pgroll complete` immediatamente dopo `start` | Annulla il beneficio del expand/contract. Aspetta che il deploy e i health check siano OK. |
| `pgroll rollback` automatico dopo `complete` | Impossibile: dopo `complete` non c'è rollback pgroll, serve una migration inversa. |
| Applicare migration su ambienti condivisi senza lock | Se più pipeline girano in parallelo entrambe potrebbero tentare `start` contemporaneamente. Usa `pgroll status` come guard. |
