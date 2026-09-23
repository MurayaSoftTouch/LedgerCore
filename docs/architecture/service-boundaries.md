# Service Boundaries

```text
               ┌──────────────────────────────┐   POST /v1/policy-decisions   ┌──────────────────────────────┐
  clients ───▶ │  ledger-api  (ASP.NET, :8080) │ ────────────────────────────▶ │ policy-service (Spring, :8081)│
               │  source of financial truth    │ ◀──────────────────────────── │ approval rules & decisions    │
               └──────────────┬───────────────┘   decision / problem+json     └──────────────┬───────────────┘
                              │ ledger_app                                                   │ policy_app
                              ▼                                                              ▼
                     ┌─────────────────┐        one PostgreSQL 18 cluster         ┌─────────────────┐
                     │  database ledger │  ── no cross-database CONNECT grants ── │  database policy │
                     └─────────────────┘                                          └─────────────────┘
```

| Concern | ledger-api | policy-service |
| --- | --- | --- |
| Accounts, journals, entries | **owns** | never sees entries |
| Balances | **owns** | never receives |
| Double-entry validation | **owns** | — |
| Posting and reversal | **owns** | — |
| Approval rules and versions | — | **owns** |
| Approval decision | requests; records `decisionId` and `policyVersion` | **produces** |
| Database | `ledger` (owner `ledger_app`; runtime `ledger_runtime`, see ADR-007) | `policy` (role `policy_app`) |
| Contract role | client | server |

## Rules

1. The services communicate only through `contracts/`. Neither reads the other's database: the roles lack `CONNECT` on each other's databases.
2. The ledger never delegates balance calculation. The policy service never stores journal lines.
3. If the policy service can't give a trustworthy decision, the ledger fails closed (ADR-005).
4. The central invariants (balanced postings, immutable posted history, reversals for corrections) are in ADR-004. The lifecycle is in ADR-006.

## Operational endpoints

| Service | Liveness | Readiness | OpenAPI |
| --- | --- | --- | --- |
| ledger-api | `GET /health/live` | `GET /health/ready` (database check) | `GET /openapi/v1.json` (Development, or `Ledger:ExposeOpenApi=true`) |
| policy-service | `GET /actuator/health/liveness` | `GET /actuator/health/readiness` | `GET /openapi/v3/api-docs` |

The policy service also exposes `GET /actuator/info`, which reports the contract version. The ledger's business API is described in [ledger-domain.md](ledger-domain.md#http-api-milestone-1); the policy service's API, including contract v1 `POST /v1/policy-decisions`, is in [policy-engine.md](policy-engine.md#api).
