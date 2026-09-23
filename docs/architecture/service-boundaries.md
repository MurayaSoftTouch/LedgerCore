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
| Database | `ledger` (role `ledger_app`) | `policy` (role `policy_app`) |
| Contract role | client | server |

## Rules

1. The services communicate only through `contracts/`. Neither reads the other's database: the roles lack `CONNECT` on each other's databases.
2. The ledger never delegates balance calculation. The policy service never stores journal lines.
3. If the policy service can't give a trustworthy decision, the ledger fails closed (ADR-005).
4. The central invariants (balanced postings, immutable posted history, reversals for corrections) are in ADR-004. The lifecycle is in ADR-006.

## Endpoints in Milestone 0

| Service | Liveness | Readiness | OpenAPI |
| --- | --- | --- | --- |
| ledger-api | `GET /health/live` | `GET /health/ready` | `GET /openapi/v1.json` (Development, or `Ledger:ExposeOpenApi=true`) |
| policy-service | `GET /actuator/health/liveness` | `GET /actuator/health/readiness` | `GET /openapi/v3/api-docs` |

The policy service also exposes `GET /actuator/info`, which reports the contract version.
