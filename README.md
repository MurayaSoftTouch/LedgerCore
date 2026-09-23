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
- retries never double-post, and failures never leave partial state (posting is one locked transaction; command idempotency keys are Milestone 4).

> **Status: Milestone 1 (ledger domain).** `ledger-api` implements ledgers, a chart of accounts, journals with double-entry validation, the ADR-006 lifecycle, transactional posting, and reversals, persisted in PostgreSQL with database-enforced invariants. There is **no production approval path yet**: journals reach `APPROVED` only through a test-only recorder until the policy service is integrated (Milestones 2–3). 
>
> **Milestone 2 (policy engine).** `policy-service` implements versioned approval policies with four structured rule types, deterministic most-restrictive-wins evaluation, immutable and idempotent decision records, and contract v1 (1.0.1) on `POST /v1/policy-decisions`, persisted in its own PostgreSQL database. **The ledger does not call it yet**: the policy client and fail-closed handling are Milestone 3. Neither service has authentication. See [policy engine](docs/architecture/policy-engine.md), [ledger domain](docs/architecture/ledger-domain.md) and the [Milestone 1](docs/backlog/milestone-1.md) and [Milestone 2](docs/backlog/milestone-2.md) backlogs.

## Repository layout

```text
services/ledger-api/        ASP.NET Core solution: Domain + Api (src/), unit + PostgreSQL tests (tests/)
services/policy-service/    Spring Boot service (Maven wrapper)
contracts/                  OpenAPI + JSON Schemas + examples + validate.sh
infra/docker/postgres/      Database init and isolation check
docs/adr/                   Architecture decision records
docs/architecture/          Service boundaries, ledger domain, contributor ownership
docs/backlog/               Initial and per-milestone backlogs (not yet GitHub issues)
.github/                    CI, issue/PR templates, CODEOWNERS, label definitions
```

`tests/contract` and `tests/integration` arrive in Milestone 3; they aren't created as empty placeholders.

## Prerequisites

| Tool | Version used | Notes |
| --- | --- | --- |
| .NET SDK | 10.0.401 | pinned in `services/ledger-api/global.json` (rolls forward to later 10.0 feature bands); `dotnet-ef` via `dotnet tool restore` |
| JDK | 21 | Maven is supplied by `./mvnw` |
| Docker + Compose | 29.x / v5 | PostgreSQL; also required by `dotnet test` (Testcontainers) |
| Node.js | 20+ | only for `contracts/validate.sh` |

## Getting started

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
dotnet run --project src/LedgerCore.Ledger.Api --launch-profile http

# 3. Policy service  → http://localhost:8081   (new terminal)
set -a; source .env; set +a                # POLICY_DB_PASSWORD; there is no committed default
cd services/policy-service
./mvnw verify                              # unit + PostgreSQL 18.6 Testcontainers tests (Docker required)
./mvnw spring-boot:run                     # Flyway migrates the `policy` database at startup

# 4. Contract
./contracts/validate.sh
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
| Readiness includes | ledger database | policy database (`readinessState,db`) |

### Configuration

Both services validate their configuration at startup and refuse to start if it's invalid.

| ledger-api (`Ledger:*`, env `Ledger__*`) | policy-service (`ledgercore.policy.*`) |
| --- | --- |
| `ConnectionStrings:Ledger`: required; the `ledger_runtime` role | |
| `ContractVersion`: semver, required | `contract-version`: semver, required |
| `PolicyServiceBaseUrl`: absolute URL, required | `environment`: required (`LEDGERCORE_ENVIRONMENT`, default `local`) |
| | `POLICY_DB_URL` (default `jdbc:postgresql://localhost:5432/policy`), `POLICY_DB_USERNAME` (default `policy_app`), `POLICY_DB_PASSWORD` (required, no default) |
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
- [Contributor ownership and milestones](docs/architecture/contributor-ownership.md)
- [ADRs](docs/adr/README.md): monorepo, ledger as source of truth, policy service, double entry and immutability, versioned contract, journal lifecycle, database-enforced invariants, money representation, immutable policy versions, deterministic evaluation, decision persistence and replay
- [Contract](contracts/README.md)
- [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md)
