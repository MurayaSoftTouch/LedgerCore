# Ledger ↔ Policy Integration (Milestone 3)

This document covers how the ASP.NET ledger obtains approval decisions from the Spring policy service, and what happens when anything goes wrong. The decisions are in [ADR-012](../adr/ADR-012-ledger-policy-reliability-contract.md) (reliability), [ADR-013](../adr/ADR-013-service-to-service-authentication.md) (authentication) and [ADR-014](../adr/ADR-014-transactional-outbox-foundation.md) (outbox). They build on ADR-005 (contract), ADR-006 (lifecycle), ADR-007 (database guards) and ADR-011 (policy idempotency).

## Sequence

```text
client            ledger-api                                    policy-service        PostgreSQL
  │ POST …/submit     │                                                │                   │
  │──────────────────▶│ tx1: lock, DRAFT → PENDING_APPROVAL, commit     │──────────────────▶│ ledger
  │                   │ read journal + entries + account types (no tx)  │                   │
  │                   │ POST /v1/policy-decisions  (Bearer, X-Correlation-Id, retries ≤ 2)  │
  │                   │───────────────────────────────────────────────▶│ evaluate, store   │ policy
  │                   │◀───────────────────────────── 200 decision ─────│ (idempotent on    │
  │                   │                                                 │  transactionId)   │
  │                   │ tx2: lock journal, insert evidence,             │                   │
  │                   │      APPROVED / REJECTED / stay pending, commit │──────────────────▶│ ledger
  │◀── 200 journal ───│ (+ policyDecision, manualReviewRequired, approvalFailure)          │
  │ POST …/post       │ tx3: lock, check APPROVED (trigger: evidence), POSTED + outbox row │ ledger
```

- `transactionId` = the journal id; `organizationId` = the ledger id (ADR-012).
- No database transaction or lock is held during the HTTP call.
- `POST …/request-approval` repeats steps 2–4 for a `PENDING_APPROVAL` journal. If evidence already exists, it returns the evidence without calling the policy service.
- `POST …/reverse` creates the reversal (`transactionType: REVERSAL`) and requests approval for it in the same way.

## Contract versioning

The contract is `contracts/openapi/policy-decision.v1.yaml`, version **1.1.0**. Payload schemas are unchanged since 1.0.0:
- 1.0.1 clarified semantics;
- 1.1.0 documents the bearer credential and `401`.

The ledger sends `contractVersion: "1.1.0"` and accepts any `1.x` response. Both sides test against the repository files:

| Side | Tests |
| --- | --- |
| Provider (Spring) | `ContractComplianceTests`, `ServiceAuthenticationTests` |
| Consumer (.NET) | `PolicyContractConsumerTests`: the serialized request validates against the request schema; every response example; unknown enums; missing fields; wrong formats; extra fields |

## Client, timeouts and retries

`PolicyDecisionClient` is a typed `HttpClient` registered through `HttpClientFactory` (`PolicyClientRegistration`):

| Layer | Default | Setting |
| --- | --- | --- |
| Total budget (including backoff) | 2000 ms | `Ledger:PolicyDecisionTimeoutMs` (100–30000) |
| Retry | 2 extra attempts; exponential backoff from 100 ms, with jitter; `Retry-After` ignored | `Ledger:PolicyMaxRetries` (0–5) |
| Per attempt | 800 ms | `Ledger:PolicyAttemptTimeoutMs` (50–30000) |
| `HttpClient.Timeout` | total + 1 s (backstop only) | — |

**Retried:** connection failures, attempt timeouts, `502`, `503`, `504`. Retrying a POST is safe because the policy service returns the stored decision for a repeated `transactionId`.

**Not retried:** `400`, `401`, `403`, `409`, `500`, any other status, any `200` (a business decision, or a body that violates the contract).

The multi-service harness uses 5000 ms total and 2000 ms per attempt, so a cold JVM's first evaluation isn't mistaken for an outage. The production defaults above are what `appsettings` ships.

## Failure mapping

| What happened | Client result | Journal | `submit` | `request-approval` |
| --- | --- | --- | --- | --- |
| `APPROVED` | Decided | → `APPROVED` + evidence | 200 | 200 |
| `REJECTED` | Decided | → `REJECTED` + evidence | 200 | 200 |
| `REVIEW_REQUIRED` | Decided | stays pending + evidence, `manualReviewRequired` | 200 | 200 |
| No answer within the budget | `TIMEOUT` | stays pending, no evidence | 200 + `approvalFailure` | 503 `POLICY_TIMEOUT` |
| Connection refused or reset, `5xx` | `UNAVAILABLE` | stays pending | 200 + `approvalFailure` | 503 `POLICY_UNAVAILABLE` |
| `409 IDEMPOTENCY_CONFLICT` | `CONFLICT` | stays pending | 200 + `approvalFailure` | 409 `POLICY_CONFLICT` |
| `400`, `409` version, unknown `decision`, missing field, wrong `transactionId`, `APPROVED` with reasons | `CONTRACT_VIOLATION` | stays pending | 200 + `approvalFailure` | 502 `POLICY_CONTRACT_VIOLATION` |
| Non-JSON body, unexpected status | `INVALID_RESPONSE` | stays pending | 200 + `approvalFailure` | 502 `POLICY_INVALID_RESPONSE` |
| `401` / `403` | `AUTHENTICATION_FAILED` | stays pending | 200 + `approvalFailure` | 502 `POLICY_AUTHENTICATION_FAILED` |

`submit` always returns 200 once the submission committed. A policy failure doesn't undo it; `approvalFailure` says why no decision was obtained. Failures are logged (see Observability) and are never stored as decisions.

## Ambiguous outcomes

Suppose the policy service commits a decision, then the connection drops before the ledger reads the response:
1. The client sees a connection error. It retries within the budget, and each retry gets the same stored decision back, unless the network is still broken.
2. If the budget runs out, the journal stays `PENDING_APPROVAL` with no evidence.
3. The next `request-approval` gets the original decision (same `decisionId`). The ledger records it once (the evidence primary key) and transitions once (the row lock plus the lifecycle trigger).

The multi-service test `LostResponseAfterPolicyCommitsIsRecoveredWithoutDuplicates` does exactly this. Toxiproxy's `limit_data` toxic cuts every response. The test asserts one policy decision, then after recovery one ledger evidence row and one `APPROVED` transition.

## Policy decision evidence

`journal_policy_decisions` has one row per journal: `decision_id` (unique), `policy_version`, `decision`, `reason_codes`, `evaluated_at`, `contract_version`, `correlation_id`, `received_at`. It is written only while the journal is `PENDING_APPROVAL`, and it is append-only (update, delete and truncate are refused, even for the owner). The ledger's lifecycle trigger requires `APPROVED` evidence for `APPROVED` and `REJECTED` evidence for `REJECTED`. Tests show that raw SQL can't approve a journal without evidence, or with only `REVIEW_REQUIRED` evidence. No policy rules are copied into the ledger.

## Service authentication

Summary of ADR-013:
- `POST /v1/policy-decisions` requires `Authorization: Bearer <POLICY_DECISION_API_TOKEN>`. The ledger sends `Ledger__PolicyServiceToken`.
- `/api/v1/**` requires the separate `POLICY_ADMIN_API_TOKEN`. Without one configured, the management API is disabled (403).
- Comparison is constant-time; credentials are never logged. The multi-service suite checks both containers' logs.

This is a shared secret, not workload identity. Production should add TLS and mTLS or platform identity.

## Correlation ids

- The ledger accepts `X-Correlation-Id` if it matches `^[A-Za-z0-9._:-]{1,128}$`, and otherwise generates a UUID. It echoes the id on the response and adds it to every log line of the request (JSON console `Scopes`).
- `CorrelationIdHandler` forwards it to the policy service, which already validates, echoes and logs it (ECS `correlationId`) and stores it on the decision.
- The ledger stores it on the evidence.
- End to end, one id appears in both services' logs and in both databases (`CorrelationIdSpansBothServices`).
- It is a tracing aid, never an authentication token.

## Observability

Neither service logs request bodies, credentials, connection strings or passwords.

| Event | Service | Fields |
| --- | --- | --- |
| Policy decision recorded | ledger (Information) | `JournalId`, `PolicyTransactionId`, `PolicyDecisionId`, `PolicyVersion`, `Decision`, plus `CorrelationId` scope |
| Policy evaluation failed | ledger (Warning) | `PolicyTransactionId`, `FailureKind`, `Detail` (no body) |
| Conflicting decision ignored | ledger (Error) | `JournalId`, recorded and received decision ids |
| Policy decision | policy (ECS) | `transactionId`, `decisionId`, `policyId`, `policyVersion`, `decision`, `reasonCodes`, `replayed`, `correlationId` |
| Request rejected by service authentication | policy (ECS, Warn) | `path`, `code` |

## Runtime database roles

| Database | Owner (migrations only) | Runtime (the service) | Runtime may not |
| --- | --- | --- | --- |
| `ledger` | `ledger_app` (EF migrations bundle / `dotnet ef`) | `ledger_runtime` | run DDL, TRUNCATE, DELETE, disable triggers, update entries, evidence, audit or outbox payloads |
| `policy` | `policy_app` (Flyway, separate connection) | `policy_runtime` (new in M3, Flyway V3) | run DDL, TRUNCATE, DELETE, disable or replace triggers, write `flyway_schema_history` |

`policy_runtime` has `UPDATE` on `policies` only so it can take the per-policy row lock; `policies_immutable` still refuses every update. `verify-isolation.sh` checks all cross-database denials. `RuntimeRoleTests` (policy) and the ledger `DatabaseGuardTests` check the privileges with raw SQL.

## Outbox

`JournalPosted` events are written in the posting transaction (ADR-014). Delivery will be at-least-once, and consumers must deduplicate on the event id. **No publisher exists yet.**

## Health

| Endpoint | Meaning |
| --- | --- |
| ledger `/health/live` | the process is up |
| ledger `/health/ready` | the ledger database is reachable. **Not** tied to the policy service: reads, drafting and posting of already-approved journals keep working during a policy outage |
| ledger `/health/dependencies` | `Healthy`, or `Degraded` when the policy service's readiness can't be reached |
| policy `/actuator/health/liveness` | the process is up |
| policy `/actuator/health/readiness` | `readinessState` + `db` + `policySchema`. Flyway has run (the service doesn't start otherwise), and every table the evaluation path needs is usable by `policy_runtime` |

Compose starts `ledger-api` only after `ledger-migrate` completed successfully **and** `policy-service` is healthy.

## Docker Compose

```bash
cp .env.example .env
docker compose up -d --build --wait           # postgres, ledger-migrate (one-shot), policy-service, ledger-api
./tests/integration/compose-smoke.sh          # approval flow over Compose DNS, outbox, correlation
docker compose up -d --wait postgres          # database only, services on the host
```

- Services reach each other by Compose DNS: `postgres`, `policy-service`.
- Published ports bind to `127.0.0.1` only.
- Secrets come from `.env`, which is git-ignored; `.env.example` holds clearly marked local-only values.
- Both images run as non-root users and have container health checks.

## Tests

| Suite | Where | What it proves |
| --- | --- | --- |
| Client resilience | `services/ledger-api/tests/.../Policy/PolicyClientResilienceTests.cs` | retry set, retry counts, per-attempt and total timeouts, status mapping |
| Consumer contract | `…/Policy/PolicyContractConsumerTests.cs` | request schema validity, response strictness, fail-closed handling |
| Approval (PostgreSQL) | `…/PolicyApprovalTests.cs`, `ApprovalApiTests.cs` | outcome mapping, evidence, concurrency, the ambiguous outcome, the DB refusing approval without evidence |
| Outbox | `…/OutboxTests.cs` | atomicity, one event per posting, immutability |
| Policy runtime role | `services/policy-service/.../RuntimeRoleTests.java` | the privilege boundary |
| Multi-service | `tests/integration/` | the real containers: approved, rejected, review, unreachable policy, lost response, duplicate and concurrent requests, contract violation, correlation in both logs, no credentials in logs, service authentication, the database boundary, dependency health. It runs with a 5000 ms budget and 2000 ms attempts so a cold JVM isn't mistaken for an outage; the production 2000/800 ms values are proven by the client resilience tests |
| Compose smoke | `tests/integration/compose-smoke.sh` | the Compose wiring |

## Known limitations

- There is no manual-review workflow and no automatic re-evaluation of pending journals (ADR-012).
- There is no outbox publisher or retention (ADR-014).
- Shared bearer credentials are sent without TLS in local and Compose setups, and there is no credential rotation overlap (ADR-013).
- `organizationId` is the ledger id until organizations exist.
- There is still no end-user authentication: `X-Actor-Id` is client-asserted (Milestone 5).
- Both databases share one PostgreSQL cluster. Access is isolated, resources are not.
