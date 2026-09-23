# LedgerCore

A double-entry financial ledger with a separate, versioned transaction-policy service.

- **`services/ledger-api`**: ASP.NET Core (.NET 10). The financial source of truth: accounts, journals, posting, reversals, balances.
- **`services/policy-service`**: Spring Boot 4.1 (Java 21). Approval policies and versioned decisions (`APPROVED` / `REJECTED` / `REVIEW_REQUIRED`).
- **`contracts/`**: OpenAPI 3.1 and JSON Schema contract between them.
- **PostgreSQL 18**: one database per service, with no cross-access.

The core guarantees, documented now and enforced from Milestone 1:

- every posted journal balances;
- posted history is immutable, and corrections use reversals;
- a policy decision that can't be trusted never approves anything (fail closed);
- retries never double-post, and failures never leave partial state.

> **Status: Milestone 0 (foundation).** Both services start, report health, serve OpenAPI and validate their configuration. There is no financial logic yet. See [the backlog](docs/backlog/initial-backlog.md).

## Repository layout

```text
services/ledger-api/        ASP.NET Core solution (src/, tests/)
services/policy-service/    Spring Boot service (Maven wrapper)
contracts/                  OpenAPI + JSON Schemas + examples + validate.sh
infra/docker/postgres/      Database init and isolation check
docs/adr/                   Architecture decision records
docs/architecture/          Service boundaries, contributor ownership
docs/backlog/               Initial backlog (not yet GitHub issues)
.github/                    CI, issue/PR templates, CODEOWNERS, label definitions
```

`tests/contract` and `tests/integration` arrive in Milestone 3; they aren't created as empty placeholders.

## Prerequisites

| Tool | Version used in M0 | Notes |
| --- | --- | --- |
| .NET SDK | 10.0.401 | pinned in `services/ledger-api/global.json` (rolls forward to later 10.0 feature bands) |
| JDK | 21 | Maven is supplied by `./mvnw` |
| Docker + Compose | 29.x / v5 | PostgreSQL only |
| Node.js | 20+ | only for `contracts/validate.sh` |

## Getting started

```bash
# 1. Database
cp .env.example .env                     # local-only defaults; .env is git-ignored
docker compose up -d --wait              # PostgreSQL 18 with databases `ledger` and `policy`
./infra/docker/postgres/verify-isolation.sh

# 2. Ledger API  → http://localhost:8080
cd services/ledger-api
dotnet test
dotnet run --project src/LedgerCore.Ledger.Api --launch-profile http

# 3. Policy service  → http://localhost:8081   (new terminal)
cd services/policy-service
./mvnw verify
./mvnw spring-boot:run

# 4. Contract
./contracts/validate.sh
```

The services don't connect to PostgreSQL yet. Database access starts in Milestone 1 (ledger) and Milestone 2 (policy).

### Endpoints

| | ledger-api | policy-service |
| --- | --- | --- |
| Liveness | `/health/live` | `/actuator/health/liveness` |
| Readiness | `/health/ready` | `/actuator/health/readiness` |
| OpenAPI | `/openapi/v1.json` (Development) | `/openapi/v3/api-docs`, `/openapi/swagger-ui.html` |
| Info | — | `/actuator/info` (contract version) |

### Configuration

Both services validate their configuration at startup and refuse to start if it's invalid.

| ledger-api (`Ledger:*`, env `Ledger__*`) | policy-service (`ledgercore.policy.*`) |
| --- | --- |
| `ContractVersion`: semver, required | `contract-version`: semver, required |
| `PolicyServiceBaseUrl`: absolute URL, required | `environment`: required (`LEDGERCORE_ENVIRONMENT`, default `local`) |
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
- [Contributor ownership and milestones](docs/architecture/contributor-ownership.md)
- [ADRs](docs/adr/README.md): monorepo, ledger as source of truth, policy service, double entry and immutability, versioned contract, journal lifecycle
- [Contract](contracts/README.md)
- [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md)
