# Policy Engine (Milestone 2)

This document describes the Spring Boot `policy-service` as implemented: it decides whether a ledger journal may be approved. Decisions are recorded in [ADR-003](../adr/ADR-003-dedicated-policy-service.md) (the service), [ADR-005](../adr/ADR-005-versioned-ledger-policy-contract.md) (the contract), [ADR-009](../adr/ADR-009-immutable-policy-versions.md) (versions), [ADR-010](../adr/ADR-010-deterministic-policy-evaluation.md) (evaluation) and [ADR-011](../adr/ADR-011-decision-persistence-and-replay.md) (persistence and replay).

**What it does not own:** balances, accounts of record, journal entries, posting state, reversals and reconciliation all stay in the ledger (ADR-002). It sees only what contract v1 sends.

Code map (`services/policy-service/src/main/java/io/ledgercore/policy/`):

| Package | Contents |
| --- | --- |
| `domain` | `Policy`, `PolicyVersion`, `rules.*`, `PolicyEvaluator`, `RequestFingerprint`. Pure Java, no Spring, no I/O. |
| `application` | `PolicyAdminService` (locking and lifecycle), `DecisionService` (selection, binding, idempotency, fail-safe) |
| `persistence` | `JdbcClient` repositories with explicit SQL. There is no ORM. |
| `web` | Contract v1 endpoint, management API, strict JSON, correlation id, problem details |
| `resources/db/migration` | Flyway `V1__policy_schema.sql`, `V2__policy_guards.sql` |

## Domain model

```text
Policy (key, name, organizationId | null = default)
  └── PolicyVersion n = 1, 2, 3 …   DRAFT → ACTIVE → RETIRED   (rules frozen at creation)
        └── Rule[position 1..100]   AMOUNT_ABOVE | TRANSACTION_TYPE | ACCOUNT_CONTEXT | CURRENCY_NOT_ALLOWED
PolicyDecision (one per transactionId) ──► exactly one PolicyVersion, or none
  └── Match[position, outcome, reasonCode] ──► rules of that same version
```

- **Policy.** Its `key` matches `^[a-z0-9][a-z0-9-]{1,62}$` and is unique. There is at most one policy per organization and at most one default policy (partial unique indexes). It is immutable.
- **Version lifecycle** (ADR-009):
  - A version is created as `DRAFT` with the next contiguous number.
  - `activate` retires the current `ACTIVE` version in the same transaction.
  - `retire` ends an active version or withdraws a draft.
  - `RETIRED` is terminal.
  - Rules never change after the version is created, whatever its status.
  - The contract's `policyVersion` label is `<key>@<n>`.

## Rules and precedence

The four rule types, their matching semantics and the evaluation algorithm are specified in ADR-010. In short:
- every rule is evaluated;
- the most restrictive outcome wins (`REJECTED > REVIEW_REQUIRED > APPROVED`);
- nothing matching means `APPROVED`;
- rule order affects only the order of reason codes, never the decision.

There is no "approve" rule, and there is no expression language.

Example version (the one the tests use):

```json
{"rules": [
  {"type": "AMOUNT_ABOVE", "currency": "KES", "threshold": "10000.00", "outcome": "REVIEW_REQUIRED", "reasonCode": "AMOUNT_EXCEEDS_REVIEW_THRESHOLD"},
  {"type": "AMOUNT_ABOVE", "currency": "KES", "threshold": "50000.00", "outcome": "REJECTED", "reasonCode": "AMOUNT_EXCEEDS_HARD_LIMIT"},
  {"type": "TRANSACTION_TYPE", "transactionTypes": ["ADJUSTMENT"], "outcome": "REVIEW_REQUIRED", "reasonCode": "TRANSACTION_TYPE_REQUIRES_REVIEW"},
  {"type": "CURRENCY_NOT_ALLOWED", "allowedCurrencies": ["KES", "USD"], "outcome": "REJECTED", "reasonCode": "CURRENCY_NOT_ALLOWED"},
  {"type": "ACCOUNT_CONTEXT", "accountTypes": ["EQUITY"], "side": "DEBIT", "outcome": "REJECTED", "reasonCode": "ACCOUNT_CONTEXT_RESTRICTED"}
]}
```

An `ADJUSTMENT` of `60000` KES under this version gives `REJECTED` with `[AMOUNT_EXCEEDS_HARD_LIMIT, AMOUNT_EXCEEDS_REVIEW_THRESHOLD, TRANSACTION_TYPE_REQUIRES_REVIEW]`.

Malformed rules are rejected at creation with 400:
- a field that doesn't belong to the type;
- a missing field;
- an unknown type;
- an `APPROVED` outcome;
- a bad reason code;
- a threshold sent as a JSON number, or with more than 4 decimals.

The database repeats the per-type shape as a `CHECK` constraint. Required columns are tested with explicit `IS NOT NULL`, because a `CHECK` whose expression is `NULL` passes; a test caught exactly that gap during development.

## Selection and effective dates

| Situation | Decision | `reasonCodes` | `policyVersion` |
| --- | --- | --- | --- |
| The organization's policy has an `ACTIVE` version | evaluated | from rules | `<key>@<n>` |
| No policy for the organization; the default policy has an `ACTIVE` version | evaluated | from rules | `<default-key>@<n>` |
| The organization's policy exists but has no `ACTIVE` version | `REVIEW_REQUIRED` | `NO_ACTIVE_POLICY_VERSION` | `none` |
| No organization policy and no default | `REVIEW_REQUIRED` | `NO_APPLICABLE_POLICY` | `none` |

In the third case the service does **not** fall back to the default, because a paused organization policy must never silently become a looser one.

**Effective dates:**
- The version used is the one `ACTIVE` at the transaction's **first** evaluation, and the decision is stored.
- `requestedAt` is ignored for selection: it is client-supplied, and trusting it would allow backdating into an older policy.
- A version's `activatedAt` and `retiredAt` record when it was in force.
- Replaying a transaction returns its stored decision, so the answer never drifts as policies change (ADR-011).

## Decision persistence, idempotency and explainability

- Decisions and their matched rules are append-only (triggers).
- A decision stores its policy and version, the label, the decision and reason codes, the evaluated type, currency and amount, a SHA-256 request fingerprint, the contract version, the correlation id and `evaluatedAt` (microsecond precision, identical on replay).
- It does not store account ids, the principal or balances.
- Idempotency is keyed on `transactionId` and decided by PostgreSQL (`INSERT … ON CONFLICT (transaction_id) DO NOTHING`):
  - an identical retry returns the stored decision (`X-Decision-Replayed: true`);
  - a retry that differs in a decision-relevant field is `409 IDEMPOTENCY_CONFLICT`.

  Retries may change `requestedAt`, `requestedBy` and `contractVersion`.
- `GET /api/v1/policy-decisions/{decisionId}` explains a decision: the matched rules (position, outcome, reason code), the evaluated inputs, the version and the correlation id.
- Service-generated reason codes: `NO_APPLICABLE_POLICY`, `NO_ACTIVE_POLICY_VERSION`, `TRANSACTION_TYPE_UNSUPPORTED`. All others come from rules.

## Failure behaviour

| Condition | Response | Recorded |
| --- | --- | --- |
| Policy store unavailable, invalid stored rule, unexpected exception | `503 POLICY_EVALUATION_UNAVAILABLE`, `Retry-After: 1` | nothing |
| Malformed request (unknown field, wrong type, pattern violation, JSON number amount) | `400 VALIDATION_FAILED` / `REQUEST_INVALID` | nothing |
| `contractVersion` not 1.x | `409 CONTRACT_VERSION_UNSUPPORTED` | nothing |
| Conflicting duplicate | `409 IDEMPOTENCY_CONFLICT` | original kept |
| Later-1.x `transactionType` | `200 REVIEW_REQUIRED` (`TRANSACTION_TYPE_UNSUPPORTED`) | yes |

No failure path returns `APPROVED`, and the database forbids an `APPROVED` decision without a version (`ck_policy_decisions_approval_needs_version`). Problem bodies follow the contract's `ProblemDetails`:
- `type` is `urn:ledgercore:problem:<CODE>`;
- they also carry `title`, `status`, `detail`, `code` and `correlationId`;
- they never include stack traces or SQL.

## Concurrency

| Race | Mechanism | Test |
| --- | --- | --- |
| Competing activations | Policy row `FOR UPDATE`; backstop `ux_policy_versions_one_active` | `ConcurrencyTests.competingActivationsLeaveExactlyOneActiveVersion` (8 threads) |
| Raw SQL activating two versions without the lock | The partial unique index: the second transaction blocks, then fails with `23505` | `sqlThatBypassesTheLockStillCannotCreateTwoActiveVersions` (the block is observed in `pg_locks`) |
| Concurrent version creation | Policy lock, a unique `(policy_id, version_number)`, and a monotonic trigger | `concurrentVersionCreationNumbersContiguously` (versions 1–8) |
| Activation during an in-flight evaluation | The evaluation holds `FOR SHARE` on the active version; the activation waits | `activationWaitsForAnInFlightEvaluationUsingTheActiveVersion` |
| Identical concurrent evaluations | `ON CONFLICT DO NOTHING`, then read the winner | `concurrentIdenticalEvaluationsRecordOneDecision`: one row, one `decisionId` |
| Conflicting concurrent evaluations | Same, plus a fingerprint comparison | `concurrentConflictingEvaluations…`: one row; the other group gets 409 |

## API

| Method and path | Purpose |
| --- | --- |
| `POST /v1/policy-decisions` | **Contract v1** evaluation (path per the OpenAPI document) |
| `POST /api/v1/policies` · `GET /api/v1/policies` · `GET /api/v1/policies/{policyId}` | Create, list or read policies (with `activeVersion`) |
| `POST /api/v1/policies/{policyId}/versions` · `GET …/versions` · `GET …/versions/{n}` | Create a version with its rules; list or read versions |
| `POST …/versions/{n}/activate` · `POST …/versions/{n}/retire` | Explicit lifecycle commands |
| `GET /api/v1/policy-decisions/{decisionId}` | Decision explanation |

There is no PATCH, PUT or DELETE. Management writes require `X-Actor-Id`.

## Contract v1 compatibility

Wire behaviour:
- Request binding is strict: unknown properties fail, and numbers or booleans are never coerced to strings, so `"totalAmount": 125000.5` is rejected.
- `transactionType` accepts any `^[A-Z][A-Z0-9_]{0,63}$`, so later-1.x values are tolerated.
- Every other field follows the schema.

`ContractComplianceTests` loads `contracts/` from the repository at test time and validates real HTTP bodies with JSON Schema 2020-12, with `format` asserted. It covers:
- every `*.valid.json` request example (accepted, answered with a schema-valid response);
- every `*.invalid.json` example (400, with a schema-valid problem);
- every required field;
- type and pattern violations;
- unknown fields;
- contract-version handling;
- correlation-id echo.

The service implements **contract 1.0.1**, which only clarifies 1.0.0 (see the changelog in `contracts/openapi/policy-decision.v1.yaml`).

## Observability

Structured ECS JSON logs (existing convention) carry key-value fields:

| Event | Fields |
| --- | --- |
| Policy created | `policyId`, `policyKey`, `organizationId`, `actor` |
| Version created / activated / retired | `policyId`, `policyVersion`, `supersededVersion`, `actor` |
| Decision | `transactionId`, `decisionId`, `policyId`, `policyVersion`, `decision`, `reasonCodes`, `replayed` |
| Conflicting duplicate | `transactionId`, `decisionId` |
| Evaluation failure (ERROR) | `transactionId`, `errorType`, and the stack trace (logs only) |

HTTP requests add `correlationId` from `X-Correlation-Id`, or a generated one. Request bodies and principals are not logged. There is no metrics or tracing stack yet (Milestone 5).

## Database isolation

- The service connects only to the `policy` database. Neither policy role has `CONNECT` on `ledger` (checked by `infra/docker/postgres/verify-isolation.sh`). Tests use the repository's real init script via Testcontainers.
- **Since Milestone 3, the service runs as `policy_runtime`**, which has DML only (Flyway V3). Flyway migrates at startup over a separate connection as the owner, `policy_app` (`clean` disabled, validate-on-migrate).
  - `policy_runtime` can't run DDL, TRUNCATE or DELETE, and can't disable or replace the V2 guard triggers (`RuntimeRoleTests`).
  - It has `UPDATE` on `policies` only so it can take the per-policy row lock; `policies_immutable` still refuses every update.
- The owner, or a superuser, can still disable the triggers; see ADR-007's limitation, which applies here too.

## Security boundary

Since Milestone 3 (ADR-013):
- `POST /v1/policy-decisions` requires the ledger's bearer credential (`POLICY_DECISION_API_TOKEN`); without it the request gets `401`.
- The management API requires a **separate** admin credential (`POLICY_ADMIN_API_TOKEN`), and is **disabled** (403) when none is configured.

This is still a shared-secret boundary, not a mature one:
- `X-Actor-Id` is recorded but **client-asserted and unverified**.
- There are no per-user administrative roles and no four-eyes activation (Milestone 5).
- Transport security (TLS, mTLS or workload identity, network policy) remains a deployment responsibility.
- Springdoc's API docs and Swagger UI are enabled by default; disable them outside development.

## Handoff to Milestone 3 (done in Milestone 3; see service-integration.md)

These were the ledger-side and integration changes left for their owners. `services/ledger-api` was not modified in Milestone 2. All of them were delivered in Milestone 3 ([service-integration.md](service-integration.md)). The ledger records evidence in a separate append-only table rather than on the journal row, and sends contract 1.1.0.

- **Ledger policy client** (@Ngetich-86, with @LMichy1):
  - send contract v1 exactly;
  - treat only a schema-valid `200` + `APPROVED` + matching `transactionId` as approval;
  - map `409 IDEMPOTENCY_CONFLICT` and `409 CONTRACT_VERSION_UNSUPPORTED` to contract incompatibility (no blind retry);
  - map `503` and timeouts to "still `PENDING_APPROVAL`, retry" (ADR-005).
- **Ledger journal schema** (@LMichy1): store `decisionId` and `policyVersion` on the journal (a new ledger migration), replacing `TestApprovalRecorder` on the approval path.
- **Retry semantics:** retries must reuse the journal's `transactionId` with unchanged amounts and account context, or they'll receive 409. A journal decided `REVIEW_REQUIRED` because no policy existed stays that way for that `transactionId`.
- **Contract version:** the ledger may keep sending `1.0.0`; responses carry `1.0.1`, and both are valid 1.x.
- **Security:** service-to-service authentication on `POST /v1/policy-decisions`, and a DML-only `policy_runtime` role (`infra/`, @Ngetich-86).

## Known limitations

- A shared-credential boundary only, with no user-level authorization (above).
- No scheduled activation, effective-date ranges, FX-aware thresholds, or velocity/cumulative rules.
- Decision history has no retention or archival policy.
- The ledger doesn't call this service yet. The policy client, timeout handling and contract tests on the ledger side are Milestone 3.
