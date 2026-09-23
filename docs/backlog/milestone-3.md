# Milestone 3 — Cross-Service Reliability and Integration

- Owner: @Ngetich-86
- Branch: `feat/m3-service-integration` (from `feat/m2-policy-engine` @ `58472cb`)
- Not GitHub issues: these are local work items. GitHub assigns issue numbers when someone with write access creates the issues.
- **Reviews required before merge:**
  - @LMichy1, for every ledger change: the journal `transactionType`, the approval-evidence trigger, the outbox write in posting, and the removal of `TestApprovalRecorder`.
  - @MurayaSoftTouch, for the policy runtime role, the authentication filters and the contract 1.1.0 change.

Out of scope: an outbox publisher or broker (only atomic persistence), a manual-review workflow, a frontend, payments, and any change to accounting invariants.

Shared definition of done:
- acceptance criteria covered by automated tests;
- ledger `dotnet build` / `test` / `format --verify-no-changes`, policy `./mvnw clean spotless:check verify`, `contracts/validate.sh`, role isolation and the multi-service suite all green;
- docs updated;
- committed as Ngetich-86.

---

## M3-01 Contract compatibility

- **Problem.** Both sides must agree on contract v1 as written, including authentication, which the contract doesn't describe yet.
- **Scope.** Document service authentication in the OpenAPI as contract **1.1.0**: a bearer security scheme and a `401` response. Payload schemas stay unchanged. Add consumer tests on the ledger side.
- **Acceptance criteria.**
  - `contracts/validate.sh` passes.
  - The provider tests (Spring) pass against the same files.
  - The consumer tests validate the ledger's serialized request against the request schema, and parse every response example.
- **Dependencies.** Contract 1.0.1.
- **Tests.** Provider `ContractComplianceTests`; consumer contract tests in `LedgerCore.Ledger.Api.Tests`.
- **Status.** Done (committed locally; not pushed or reviewed).

## M3-02 .NET policy client

- **Problem.** The ledger needs a typed client that can never turn a transport problem into a business decision.
- **Scope.**
  - An `IPolicyDecisionClient` typed `HttpClient` via `HttpClientFactory`.
  - Strict response validation.
  - A result type that separates business outcomes (`APPROVED` / `REJECTED` / `REVIEW_REQUIRED`) from failures (`TIMEOUT` / `UNAVAILABLE` / `INVALID_RESPONSE` / `CONTRACT_VIOLATION` / `CONFLICT` / `AUTHENTICATION_FAILED`).
  - Bounded, jittered retries only on transient failures.
- **Acceptance criteria.**
  - There is no unbounded timeout.
  - `400`, `409`, business decisions and contract violations are never retried.
  - An unknown `decision`, a mismatched `transactionId`, a missing field, or an `APPROVED` carrying reason codes → `CONTRACT_VIOLATION`.
- **Dependencies.** M3-01.
- **Tests.** Client tests against a stub HTTP handler, covering every status, body shape, timeout and retry count.
- **Status.** Done (committed locally; not pushed or reviewed).

## M3-03 Ledger approval integration

- **Problem.** Journals reach `APPROVED` only through a test shortcut.
- **Scope.**
  - `submit` moves to `PENDING_APPROVAL`, then requests a decision. `request-approval` retries for a pending journal.
  - The policy call runs **outside** any database transaction; the outcome is applied in a new transaction that locks the journal.
  - `TestApprovalRecorder` is deleted; tests use a stub policy client through the real approval path.
  - Journals gain an immutable `transactionType`, which the contract requires; reversals are always `REVERSAL`.
- **Acceptance criteria.**
  - `APPROVED` → `APPROVED`.
  - `REJECTED` → `REJECTED`.
  - `REVIEW_REQUIRED` and every failure → stays `PENDING_APPROVAL`.
  - Nothing can set `APPROVED` without matching evidence (DB trigger).
- **Dependencies.** M3-02, M3-04.
- **Tests.** API tests with a stub client; DB-bypass tests.
- **Status.** Done (committed locally; not pushed or reviewed).

## M3-04 Policy decision reference persistence

- **Problem.** The ledger must be able to prove which decision approved or rejected a journal.
- **Scope.**
  - An append-only `journal_policy_decisions` table: one row per journal, holding decision id, policy version, decision, reason codes, evaluated-at, contract version, correlation id and received-at.
  - An additive migration. Old migrations are untouched.
  - The replacement lifecycle trigger requires matching evidence for `APPROVED` and `REJECTED`.
- **Acceptance criteria.**
  - Evidence can't be updated or deleted, even by the owner.
  - A second, different decision for the same journal is refused.
- **Dependencies.** M1 schema.
- **Tests.** Raw-SQL guard tests; replay test.
- **Status.** Done (committed locally; not pushed or reviewed).

## M3-05 Failure and timeout handling

- **Problem.** Timeouts, outages and ambiguous outcomes must fail closed and be recoverable.
- **Scope.**
  - An explicit per-attempt timeout, total budget and retry count.
  - Ambiguous outcomes (the policy committed, the response was lost) are recovered by retrying with the same `transactionId`.
- **Acceptance criteria.**
  - A lost response followed by a retry → one policy decision, one evidence row, one transition.
  - An unreachable policy service → `PENDING_APPROVAL`, and posting is refused.
- **Dependencies.** M3-02, M3-03.
- **Tests.** Client unit tests; a multi-service test with Toxiproxy cutting the response.
- **Status.** Done and verified locally on 2026-09-23:
  - client tests, including retries resending the same transaction;
  - PostgreSQL tests with a stubbed policy service, including the lost-response case;
  - the Toxiproxy multi-service scenarios for an unreachable policy service and a lost response.

## M3-06 Runtime database-role hardening

- **Problem.** The policy service runs as its schema owner, which can disable its guard triggers.
- **Scope.**
  - A `policy_runtime` role with DML only.
  - Flyway keeps running as `policy_app`, through a separate connection.
  - Exact grants in Flyway V3.
  - Isolation-script checks.
- **Acceptance criteria.**
  - The runtime role can't run DDL, TRUNCATE, DELETE, `DISABLE TRIGGER` or `DROP`.
  - The service runs as `policy_runtime`.
  - The guards are unchanged.
- **Dependencies.** M2 schema.
- **Tests.** Raw-SQL privilege tests in `RuntimeRoleTests`; `verify-isolation.sh`.
- **Status.** Done (committed locally; not pushed or reviewed).

## M3-07 Docker multi-service integration

- **Problem.** The services only run on the host.
- **Scope.**
  - Dockerfiles for both services and a one-shot ledger migration job.
  - Compose with health checks and `depends_on` conditions, where the ledger waits for policy readiness.
  - Secrets from `.env`; ports bound to 127.0.0.1.
- **Acceptance criteria.**
  - `docker compose up --build --wait` brings up PostgreSQL, the ledger migration, the policy service and the ledger API, all healthy.
  - A journal approved through Compose service DNS names.
- **Dependencies.** M3-03, M3-06.
- **Tests.** `tests/integration/compose-smoke.sh`.
- **Status.** Done (committed locally; not pushed or reviewed).

## M3-08 Correlation and structured logging

- **Problem.** One operation spans two services and two logs.
- **Scope.**
  - An `X-Correlation-Id` middleware in the ledger (validate or generate, echo, log scope).
  - The policy client forwards the id; the policy service already honours it.
  - Integration log events carry the identifiers.
- **Acceptance criteria.**
  - The same id appears in both services' logs and in the ledger response.
  - Credentials, payloads and connection strings are never logged.
- **Dependencies.** M3-02.
- **Tests.** Middleware tests; the stub client receives the id; a multi-service test reads the id from the policy decision.
- **Status.** Done and verified locally on 2026-09-23. `CorrelationIdSpansBothServices` finds the id in the ledger response, both databases and both services' logs. `CredentialsNeverAppearInEitherServiceLog` passes. Compose smoke confirmed the id reaches the policy service.

## M3-09 Full integration tests

- **Problem.** Each service is tested alone; their interaction is not.
- **Scope.** `tests/integration`: real ledger and policy containers built from their Dockerfiles, real PostgreSQL (one cluster, two databases), and Toxiproxy between ledger and policy.
- **Acceptance criteria.** These scenarios pass:
  - approved;
  - rejected;
  - review;
  - unavailable;
  - ambiguous outcome;
  - duplicate submit;
  - contract mismatch;
  - cross-database boundary.
- **Dependencies.** M3-03 to M3-08.
- **Tests.** This item is the tests.
- **Status.** Done and verified locally on 2026-09-23: 13 of 13 tests pass against images built from the repository.
  - Covered here: approved, rejected, review, unavailable, ambiguous outcome, duplicate and concurrent submit, contract mismatch, and the cross-database boundary.
  - Covered by the ledger PostgreSQL tests instead: the approval-vs-post race (`PostingCannotOvertakeAnInFlightApproval`).

## M3-10 CI and documentation

- **Problem.** The integrated backend needs automated verification and accurate docs.
- **Scope.**
  - CI jobs: `ledger`, `policy`, `contracts`, `database-isolation`, `integration`.
  - `docs/architecture/service-integration.md`.
  - ADRs for real decisions only.
- **Acceptance criteria.**
  - actionlint is clean.
  - The docs match the code.
  - No claim that remote CI passed.
- **Dependencies.** All of the above.
- **Tests.** Quality gates.
- **Status.** Implemented; verified locally only.
  - actionlint 1.7.12 is clean, and every job's commands pass locally.
  - The workflow has **never run on GitHub**, so remote CI is unverified.

---

## Verification record (local, 2026-09-23)

Run on the developer machine (WSL2, Docker 29.1.3). **Remote CI has not run.**

| Gate | Result |
| --- | --- |
| Multi-service suite (`tests/integration`) | 13 / 13 passed |
| Compose smoke (`compose-smoke.sh`) | passed, against images built from this branch |
| Ledger domain tests | 68 / 68 passed |
| Ledger API tests (PostgreSQL, policy client, consumer contract) | 188 / 188 passed; build 0 warnings; `dotnet format` clean |
| Policy service (`./mvnw clean spotless:check verify`) | 235 / 235 passed; Spotless clean |
| Contract (`contracts/validate.sh`, 1.1.0) | OpenAPI lint clean; 2 schemas compile; 7 / 7 examples as expected |
| Database isolation (`verify-isolation.sh`) | 10 / 10 passed |
| actionlint | clean |

Defects found by this run (fixed in test code only; no production change):
- `PolicyOutageDegradesDependenciesButNotReadiness` depended on nothing listening at `localhost:8081`. It failed whenever the Compose stack was up. The test host now uses an unroutable address.
- The client tests didn't check that retries resend the same transaction, or that `REJECTED` and contract-violating responses are sent only once. Those tests are now added.
