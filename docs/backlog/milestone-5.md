# Milestone 5 — Reconciliation, Observability and Security Hardening

- Owner: @Ngetich-86
- Supporting reviewer: @MurayaSoftTouch
- Branch: `feat/m5-operational-hardening`, from `feat/m4-posting-hardening` @ `ec9e611`
- Not GitHub issues: these are local work items. GitHub assigns issue numbers when someone with write access creates the issues.
- **Reviews required before merge:**
  - @MurayaSoftTouch: every policy-service change (the authentication filter, error handling, request limits).
  - @LMichy1: every ledger change (reconciliation categories, health checks, error mapping, request limits, logging in the posting path).

Out of scope:
- an outbox publisher or broker;
- user accounts, RBAC or an identity platform;
- a metrics backend;
- Kubernetes;
- a UI;
- any change to accounting invariants or the policy contract.

Shared definition of done:
- acceptance criteria are covered by automated tests;
- all regression gates are green (ledger, policy, contracts, isolation, multi-service, Compose smoke);
- no secret appears in logs, as proven by tests;
- docs are updated;
- committed as Ngetich-86.

---

## M5-01 Reconciliation hardening

- **Problem.** The Milestone 4 report is a boolean plus two id lists. It has no run id, no status and no discrepancy categories, and it doesn't check posting evidence or outbox events.
- **Scope.**
  - A run id and a status: `HEALTHY` or `DISCREPANCY`. A scan that fails is an error response and never `HEALTHY`.
  - Categorized discrepancies:
    - an unbalanced journal;
    - a malformed or missing reversal leg;
    - a currency imbalance;
    - missing `APPROVED` evidence;
    - a missing, orphaned or mismatched `JournalPosted` event.
  - A bounded number of discrepancy details.
  - Legacy postings without an idempotency claim are counted and reported as expected history, not as corruption.
- **Acceptance criteria.**
  - Each category is detected from a corrupted fixture made with the guards bypassed in a disposable test database.
  - Nothing is ever modified.
  - The query count does not grow with the size of the data.
- **Tests.** Reconciliation tests (PostgreSQL); a query-count and performance test.
- **Status.** Done and verified locally (2026-09-24): `ReconciliationDiscrepancyTests` (12), `ReconciliationTests`, `ReconciliationApiTests`. On the real Compose data, 5 Milestone 3 ledgers reconcile `HEALTHY` with 1 legacy posting each.

## M5-02 Operational health model

- **Problem.** Ledger readiness checks only that the database connects. It doesn't check that the schema matches the migrations this build expects.
- **Scope.**
  - Liveness: the process is running.
  - Readiness: the database is reachable, and every migration in this build is applied.
  - Dependency health: the policy service. It reports `Degraded`, never unready.
  - Policy readiness stays: `db` + `policySchema` + readiness state. Flyway validates the schema at startup.
- **Acceptance criteria.**
  - A missing or unmigrated schema → ledger unready.
  - A policy outage → ledger ready, dependencies `Degraded`.
- **Tests.** Health tests (PostgreSQL); multi-service.
- **Status.** Done and verified locally (2026-09-24): `OperationsTests` and the multi-service suite.

## M5-03 Structured audit and observability

- **Problem.** There is no per-request log line, and posting, reversal, reconciliation and policy calls log no durations.
- **Scope.**
  - A request log with: method, route template, status, duration and correlation id. No bodies, no query strings, no headers.
  - Command outcome logs with: ledger id, journal id, operation, outcome, replayed flag, duration, and a short hash of the idempotency key.
  - Policy call latency.
  - Reconciliation run logs.
- **Acceptance criteria.**
  - The fields are present in the JSON output.
  - No raw idempotency keys, bodies or credentials appear in logs.
- **Tests.** Log-capture tests.
- **Status.** Done and verified locally (2026-09-24): `SecretRedactionTests` and `OperationsTests`.

## M5-04 Security hardening

- **Problem.** The HTTP services accept defaults that aren't justified: Kestrel's 30 MB bodies, the `Server` header, and unbounded JSON arrays in policy requests. The policy service can also answer unexpected exceptions with non-problem bodies.
- **Scope.**
  - Request-size and header limits on both services, `413` for oversized bodies, and no `Server` header.
  - JSON depth limits.
  - A problem-details response for every unhandled error, without internals.
  - A ledger runtime-role destructive-privilege suite.
  - A dependency vulnerability review with the tools already in each stack.
- **Acceptance criteria.**
  - Oversized input fails predictably.
  - Errors never echo bodies, SQL or stack traces.
  - The runtime roles can't run DDL, disable triggers, truncate history or mutate immutable rows.
- **Tests.** Limit tests, error-body tests and runtime-role probes on both services.
- **Status.** Done and verified locally (2026-09-24): `OperationsTests`, `HttpHardeningTests`, `LedgerRuntimeRoleTests` (32 probes), `RuntimeRoleTests` (+8). Dependency review in SECURITY.md.

## M5-05 Administrative API protection

- **Problem.** The policy authentication filter matches the raw request URI and allows anything it doesn't recognize. **Found in this milestone:** `POST /v1/policy-decisions;x=y` reaches the decision controller without a credential.
- **Scope.**
  - Deny by default, on the normalized servlet path.
  - The public allow-list is only health, info and the API docs.
  - `/v1/**` needs the decision credential; `/api/**` needs the admin credential.
  - Malformed bearer headers are rejected.
  - Credentials are never echoed.
- **Acceptance criteria.**
  - Tested over real HTTP (Tomcat, not MockMvc) with path parameters, trailing slashes, double slashes, case variants and encodings.
  - Neither credential works on the other's API.
  - With no admin credential, management stays disabled.
- **Tests.** A RANDOM_PORT authentication test.
- **Status.** Done and verified locally (2026-09-24). The bypass was confirmed on the running stack before the fix (the management read returned 200 with no credential). After the fix both variants return 401 on the running stack, and `HttpAuthenticationBoundaryTests` passes 29/29 (7 reproduced bypasses against the old filter).

## M5-06 Dependency degradation behavior

- **Problem.** When the ledger database is unavailable or its connection pool is exhausted, the error escapes as a `500` with no domain code.
- **Scope.**
  - Database connection failures and pool exhaustion → `503 LEDGER_DATABASE_UNAVAILABLE` with `Retry-After`, and no internals.
  - Keep the existing behaviour: a policy outage, timeout or malformed response keeps the journal pending, and an approved journal still posts.
- **Acceptance criteria.**
  - The ledger database down → `503` on commands and queries, liveness `200`, readiness `503`.
  - Pool exhaustion → `503`.
  - A schema mismatch → unready.
- **Tests.** API tests; multi-service tests.
- **Status.** Done and verified locally (2026-09-24). This also covers rejected database credentials (class 28) and a missing database.

## M5-07 Failure simulation

- **Problem.** Some failure modes have never been simulated against the real stack: policy latency, and both databases being unavailable.
- **Scope.** Multi-service simulations:
  - policy latency above the attempt timeout (bounded, `TIMEOUT`, stays pending);
  - PostgreSQL paused (ledger unready, policy readiness `DOWN`, both live, both recover);
  - the existing outage, contract-violation and credential scenarios kept.
- **Acceptance criteria.** Every simulation passes, and state is correct after recovery.
- **Tests.** `tests/integration`.
- **Status.** Done and verified locally (2026-09-24): `FailureSimulationTests` (3), plus the existing outage, contract-violation and credential scenarios.

## M5-08 Outbox operational visibility

- **Problem.** Nothing shows how many events are waiting, or for how long.
- **Scope.**
  - `GET /ops/outbox`: the pending count, the oldest pending age, and a per-event-type breakdown.
  - An overall state of `EMPTY`, `PENDING` or `AGING`, where `AGING` means older than a configured threshold.
  - No payloads and no ids.
  - No "failed delivery" language, because there is no publisher.
- **Acceptance criteria.**
  - The counts and ages match the data.
  - The threshold is configurable and validated.
- **Tests.** API tests (PostgreSQL).
- **Status.** Done and verified locally (2026-09-24): `OperationsTests`. The live stack shows `AGING` with 7 pending events, as expected with no publisher.

## M5-09 CI and release diagnostics

- **Problem.** When CI fails, nothing is kept beyond the console output.
- **Scope.**
  - On failure, upload the test results, the Compose status, container health and the service logs.
  - Never upload `.env`.
- **Acceptance criteria.**
  - actionlint is clean.
  - No secrets appear in the uploaded paths.
- **Tests.** actionlint.
- **Status.** Done: actionlint is clean. The steps have **never run on GitHub**.

## M5-10 Documentation and verification

- **Scope.**
  - `docs/architecture/operational-hardening.md`.
  - An ADR for the health and degradation model.
  - README and `SECURITY.md`.
  - Actor trust and idempotency-claim growth.
  - Troubleshooting.
  - All gates.
- **Acceptance criteria.**
  - The docs match the code.
  - No claim that remote CI passed.
- **Status.** Done: `operational-hardening.md`, ADR-016, README, SECURITY.md, the ownership table.

---

## Verification record (local, 2026-09-24)

This ran on the developer machine (WSL2, Docker 29.1.3). The clock was checked before and after each database run, with no jumps and Windows−WSL within about 0.2 s. **Remote CI has not run.**

| Gate | Result |
| --- | --- |
| Ledger domain tests | 68 / 68 passed |
| Ledger API tests | 311 / 311 passed (252 before Milestone 5, 59 new); build 0 warnings; `dotnet format` clean |
| Policy service (`./mvnw clean spotless:check verify`) | 277 / 277 passed (235 before, 42 new); Spotless clean |
| Contract (`contracts/validate.sh`, 1.1.0) | OpenAPI lint clean; 2 schemas; 7 / 7 examples as expected (unchanged) |
| Database isolation | 10 / 10 passed |
| Multi-service suite | 17 / 17 passed (14 before, 3 new failure simulations) |
| Compose smoke | passed, against images built from this branch; the path bypass is closed on the running stack |
| actionlint | clean |
| Dependencies | .NET: no known-vulnerable packages (nuget.org). Maven: update check only; no CVE scanner in the stack |

Defects found and fixed:
1. **Policy-service authentication bypass (security, critical).** Path-parameter variants reached the decision endpoint and the management API with no credential (`84bae74`).
2. **Unbounded lists inside policy rules** (`a8ec8bf`).
3. **A 30 s Hikari connection timeout.** Every policy request and probe hung during a database outage (`0632bfe`).
4. **Database outages and rejected database credentials surfaced as `500`** without a code (`e1898d8`, `22dc1d7`).
5. **`runtimeRoleStillHitsTheGuardsWhereItHasPrivileges` depended on test order** (`a8ec8bf`).

Storage:
- D: was 14.36 GB at the start. 45 unused LedgerCore build images were removed (5.47 GB in Linux), then the VHDX was compacted, bringing D: to 20.00 GB.
- The multi-service rebuild then used 3.50 GB, leaving D: at 16.41 GB (ACCEPTABLE).
