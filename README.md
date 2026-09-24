# LedgerCore

A double-entry financial ledger with a separate, versioned transaction-policy service.

- **`services/ledger-api`**: ASP.NET Core (.NET 10). The financial source of truth: accounts, journals, posting, reversals, balances.
- **`services/policy-service`**: Spring Boot 4.1 (Java 21). Approval policies and versioned decisions (`APPROVED` / `REJECTED` / `REVIEW_REQUIRED`).
- **`contracts/`**: OpenAPI 3.1 and JSON Schema contract between them.
- **PostgreSQL 18**: one database per service, with no cross-access.

The core guarantees:

- every posted journal balances (**enforced**: domain model and PostgreSQL trigger);
- posted history is immutable, and corrections use reversals (**enforced**: domain, database triggers, least-privilege runtime role);
- a policy decision that can't be trusted never approves anything (fail closed; designed in ADR-005, implemented in Milestone 3);
- retries never double-post, and failures never leave partial state (**enforced**: posting is one locked transaction, and `post` / `reverse` require an `Idempotency-Key` whose claim the database requires; Milestone 4).

> **Status: Milestone 5 (operational hardening).**
> - Reconciliation reports categorized discrepancies with a run id and status, and tells legacy history from corruption.
> - Readiness includes the schema, and dependency failures degrade predictably: a database outage is `503`, and a policy outage keeps the ledger ready.
> - `/ops/outbox` shows how many events wait, and for how long.
> - Requests, commands, policy calls and reconciliation runs are logged as structured JSON, with no secrets (proven by tests).
> - Request sizes are bounded, and both runtime database roles pass destructive-privilege probes.
> - A policy-service authentication bypass (path parameters) was found and fixed.
>
> See [operational hardening](docs/architecture/operational-hardening.md), [ADR-016](docs/adr/ADR-016-operational-health-and-degradation.md) and the [Milestone 5 backlog](docs/backlog/milestone-5.md).
>
> **Milestone 4 (posting hardening).**
> - `post` and `reverse` are idempotent: a required `Idempotency-Key` header, and replays built from persisted state.
> - Concurrent and conflicting requests are decided by PostgreSQL.
> - Posting is bound in the database to its claim and to the journal's own `APPROVED` decision.
> - The audit records the authorizing decision and the key.
> - A reconciliation report recomputes the ledger from posted entries.
>
> See [posting idempotency](docs/architecture/posting-idempotency.md), [ADR-015](docs/adr/ADR-015-idempotent-posting-and-reversal.md) and the [Milestone 4 backlog](docs/backlog/milestone-4.md).
>
> **Milestone 3 (integration).** The ledger obtains every approval from the policy service. It uses a typed client with bounded retries, keeps journals `PENDING_APPROVAL` on any failure (fail closed), records each decision as append-only evidence that the database requires, and writes a `JournalPosted` outbox event with each posting. Service calls are authenticated with shared credentials, correlation ids span both services, and both run as least-privilege database roles. Docker Compose runs the whole stack. See [service integration](docs/architecture/service-integration.md) and the [Milestone 3 backlog](docs/backlog/milestone-3.md).
>
> **Milestone 1 (ledger domain).** `ledger-api` implements ledgers, a chart of accounts, journals with double-entry validation, the ADR-006 lifecycle, transactional posting, and reversals, persisted in PostgreSQL with database-enforced invariants.
>
> **Milestone 2 (policy engine).** `policy-service` implements versioned approval policies with four structured rule types, deterministic most-restrictive-wins evaluation, immutable and idempotent decision records, and contract v1 (now 1.1.0) on `POST /v1/policy-decisions`, persisted in its own PostgreSQL database. See [policy engine](docs/architecture/policy-engine.md), [ledger domain](docs/architecture/ledger-domain.md) and the [Milestone 1](docs/backlog/milestone-1.md) and [Milestone 2](docs/backlog/milestone-2.md) backlogs.

## Repository layout

```text
services/ledger-api/        ASP.NET Core solution: Domain + Api (src/), unit + PostgreSQL tests (tests/)
services/policy-service/    Spring Boot service (Maven wrapper)
tests/integration/          Multi-service tests (Testcontainers + Toxiproxy) and the Compose smoke script
contracts/                  OpenAPI + JSON Schemas + examples + validate.sh
infra/docker/postgres/      Database init and isolation check
docs/adr/                   Architecture decision records
docs/architecture/          Service boundaries, ledger domain, contributor ownership
docs/backlog/               Initial and per-milestone backlogs (not yet GitHub issues)
.github/                    CI, issue/PR templates, CODEOWNERS, label definitions
```

Contract tests live with each side: provider tests in `services/policy-service`, consumer tests in `services/ledger-api`. There is no separate `tests/contract` placeholder.

## Prerequisites

| Tool | Version used | Notes |
| --- | --- | --- |
| .NET SDK | 10.0.401 | pinned in `services/ledger-api/global.json` (rolls forward to later 10.0 feature bands); `dotnet-ef` via `dotnet tool restore` |
| JDK | 21 | Maven is supplied by `./mvnw` |
| Docker + Compose | 29.x / v5 | PostgreSQL; also required by `dotnet test` (Testcontainers) |
| Node.js | 20+ | only for `contracts/validate.sh` |

## Getting started

**Whole stack in Docker** (Milestone 3):

```bash
cp .env.example .env                        # local-only defaults, including service credentials
docker compose up -d --build --wait         # postgres, ledger-migrate, policy-service, ledger-api
./tests/integration/compose-smoke.sh        # approve and post one journal end to end
```

**Services on the host** (for development):

```bash
# 1. Database
cp .env.example .env                     # local-only defaults; .env is git-ignored
docker compose up -d --wait              # PostgreSQL 18 with databases `ledger` and `policy`
./infra/docker/postgres/verify-isolation.sh

# 2. Ledger API  → http://localhost:8080
set -a; source .env; set +a
cd services/ledger-api
dotnet tool restore
dotnet test                              # unit tests + PostgreSQL 18.6 via Testcontainers (Docker required)
# Migrations run as the schema owner (ledger_app), never as the runtime role:
dotnet ef database update --project src/LedgerCore.Ledger.Api \
  --connection "Host=localhost;Port=$POSTGRES_PORT;Database=ledger;Username=ledger_app;Password=$LEDGER_DB_PASSWORD"
# The API connects as the least-privilege runtime role:
export ConnectionStrings__Ledger="Host=localhost;Port=$POSTGRES_PORT;Database=ledger;Username=ledger_runtime;Password=$LEDGER_RUNTIME_DB_PASSWORD"
export Ledger__PolicyServiceToken="$POLICY_DECISION_API_TOKEN"   # the policy service's decision credential
dotnet run --project src/LedgerCore.Ledger.Api --launch-profile http

# 3. Policy service  → http://localhost:8081   (new terminal)
set -a; source .env; set +a                # POLICY_*_PASSWORD and POLICY_*_TOKEN; no committed defaults
cd services/policy-service
./mvnw verify                              # unit + PostgreSQL 18.6 Testcontainers tests (Docker required)
./mvnw spring-boot:run                     # Flyway migrates the `policy` database at startup

# 4. Contract, and the multi-service suite (builds both images; Docker required)
./contracts/validate.sh
(cd tests/integration && dotnet test)
```

The API never changes the schema at startup. If you created the Compose volume before Milestone 1, recreate it (`docker compose down -v`, which deletes local data) so the init script creates the `ledger_runtime` role.

Try it (the example uses `jq`):

```bash
H=(-H 'Content-Type: application/json' -H 'X-Actor-Id: you')
L=$(curl -s "${H[@]}" -d '{"code":"DEMO","name":"Demo"}' localhost:8080/api/v1/ledgers | jq -r .id)
curl -s "${H[@]}" -d '{"code":"1000","name":"Cash","type":"ASSET","currency":"KES"}' localhost:8080/api/v1/ledgers/$L/accounts
```

The full endpoint list is in [ledger-domain.md](docs/architecture/ledger-domain.md#http-api-milestone-1). Policy API: [policy-engine.md](docs/architecture/policy-engine.md#api). To try it: create a policy, add a version, activate it, then post `contracts/schemas/examples/request.valid.json` to `POST /v1/policy-decisions`.

### Endpoints

| | ledger-api | policy-service |
| --- | --- | --- |
| Liveness | `/health/live` | `/actuator/health/liveness` |
| Readiness | `/health/ready` (checks the ledger database) | `/actuator/health/readiness` |
| OpenAPI | `/openapi/v1.json` (Development) | `/openapi/v3/api-docs`, `/openapi/swagger-ui.html` |
| Info | — | `/actuator/info` (contract version) |
| Readiness includes | ledger database | policy database and schema (`readinessState,db,policySchema`) |
| Dependencies | `/health/dependencies`: policy service (`Degraded` when unreachable) | — |

### Configuration

Both services validate their configuration at startup and refuse to start if it's invalid.

| ledger-api (`Ledger:*`, env `Ledger__*`) | policy-service (`ledgercore.policy.*`) |
| --- | --- |
| `ConnectionStrings:Ledger`: required; the `ledger_runtime` role | |
| `ContractVersion`: semver, required | `contract-version`: semver, required |
| `PolicyServiceBaseUrl`: absolute URL, required | `environment`: required (`LEDGERCORE_ENVIRONMENT`, default `local`) |
| `PolicyServiceToken`: required, ≥ 32 characters (`Ledger__PolicyServiceToken`) | `POLICY_DB_URL` (default `jdbc:postgresql://localhost:5432/policy`); runtime `policy_runtime` / `POLICY_RUNTIME_DB_PASSWORD`; Flyway `policy_app` / `POLICY_DB_PASSWORD` |
| `PolicyAttemptTimeoutMs` (800), `PolicyMaxRetries` (2) | `POLICY_DECISION_API_TOKEN` (required), `POLICY_ADMIN_API_TOKEN` (empty disables the management API) |
| `PolicyDecisionTimeoutMs`: 100–30000, default 2000 | |
| `ExposeOpenApi`: `true` in Development | |

Logs are structured JSON on stdout: the JSON console formatter for .NET, and ECS for Spring (`LOG_FORMAT` overrides it).

## Formatting

```bash
(cd services/ledger-api && dotnet format)            # CI runs --verify-no-changes
(cd services/policy-service && ./mvnw spotless:apply)  # CI runs spotless:check
```

## Documentation

- [Service boundaries](docs/architecture/service-boundaries.md)
- [Ledger domain](docs/architecture/ledger-domain.md): model, lifecycle, posting, concurrency, immutability, reversals
- [Policy engine](docs/architecture/policy-engine.md): versions, rules, evaluation, decisions, idempotency, failure behaviour
- [Posting idempotency](docs/architecture/posting-idempotency.md): idempotency keys, concurrency, approval binding, reconciliation, failure injection
- [Operational hardening](docs/architecture/operational-hardening.md): health, degradation, reconciliation operations, outbox visibility, logging, security boundaries, troubleshooting
- [Service integration](docs/architecture/service-integration.md): approval sequence, retries, timeouts, failure mapping, evidence, authentication, correlation, outbox, health, Compose
- [Contributor ownership and milestones](docs/architecture/contributor-ownership.md)
- [ADRs](docs/adr/README.md): monorepo, ledger as source of truth, policy service, double entry and immutability, versioned contract, journal lifecycle, database-enforced invariants, money representation, immutable policy versions, deterministic evaluation, decision persistence and replay, ledger–policy reliability, service authentication, transactional outbox
- [Contract](contracts/README.md)
- [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md)
