# Milestone 2 — Transaction Policy Engine

- Owner: @MurayaSoftTouch
- Branch: `feat/m2-policy-engine` (from `feat/m1-ledger-domain` @ `1ce4f20`)
- Reviewers needed before merge: @Ngetich-86 for `contracts/`; @LMichy1 for the contract clarification, which affects the ledger client in Milestone 3
- Not GitHub issues: these are local work items. GitHub assigns issue numbers when someone with write access creates the issues.

Out of scope: the ledger policy client and any ledger ↔ policy HTTP call (Milestone 3), authentication (Milestone 5), and any change to `services/ledger-api`.

Shared definition of done for every item: acceptance criteria covered by automated tests; `./mvnw clean spotless:check verify` green (including PostgreSQL Testcontainers tests); `contracts/validate.sh` green; docs updated; committed as MurayaSoftTouch.

---

## M2-01 Policy aggregate

- **Problem.** Approval rules need an owner with a stable business identity.
- **Scope.** `Policy`: id, unique `key`, name, description, optional `organizationId` scope (null means the default policy), created-at and created-by. Immutable once created.
- **Acceptance criteria.**
  - Duplicate key → 409.
  - At most one policy per organization, and at most one default policy (DB unique indexes).
  - No update or delete operation.
- **Dependencies.** ADR-003.
- **Tests.** Domain validation; API create, get, list, duplicate key, duplicate organization scope.
- **Status.** Done (committed locally; not pushed or reviewed).

## M2-02 Policy versioning

- **Problem.** Decisions must be explainable against the exact rules that produced them.
- **Scope.**
  - `PolicyVersion`: number, status `DRAFT` / `ACTIVE` / `RETIRED`, created-at and created-by, activated-at and activated-by, retired-at and retired-by, rules.
  - Content is immutable from creation.
  - Activation atomically retires the previous active version.
- **Acceptance criteria.**
  - Version numbers go 1, 2, 3, … per policy (DB unique constraint plus a trigger).
  - At most one `ACTIVE` version per policy (partial unique index).
  - `RETIRED` is terminal.
  - Old versions stay queryable.
- **Dependencies.** M2-01.
- **Tests.** Create v1 and v2, monotonic numbering, activate, supersede, retire, forbidden transitions, historical retrieval.
- **Status.** Done (committed locally; not pushed or reviewed).

## M2-03 Structured rule model

- **Problem.** Rules must be data, not code.
- **Scope.** Four typed rules, each with an explicit position, an outcome (`REVIEW_REQUIRED` or `REJECTED`) and a reason code:
  - `AMOUNT_ABOVE`: currency plus threshold;
  - `TRANSACTION_TYPE`: a set of contract v1 types;
  - `ACCOUNT_CONTEXT`: account types, an optional side, and/or account ids;
  - `CURRENCY_NOT_ALLOWED`: an allow-list.

  There are no expressions and no scripting.
- **Acceptance criteria.**
  - Malformed rules are rejected with 400 at version creation: a field that doesn't belong to the type, a missing field, an unknown type, or a bad reason code.
  - The DB has check constraints per rule type.
- **Dependencies.** M2-02.
- **Tests.** Domain validation matrix; API 400 cases; DB constraint rejects a malformed row inserted with raw SQL.
- **Status.** Done (committed locally; not pushed or reviewed).

## M2-04 Evaluation engine

- **Problem.** The same input and version must always produce the same decision.
- **Scope.** A pure evaluator:
  - evaluate every rule in position order;
  - the most restrictive outcome wins (`REJECTED` > `REVIEW_REQUIRED` > `APPROVED`);
  - reason codes are ordered by severity, then position;
  - an unknown `transactionType` → `REVIEW_REQUIRED` (ADR-005);
  - no policy, or no active version → `REVIEW_REQUIRED`.
- **Acceptance criteria.**
  - The result is deterministic regardless of the order rules were stored in.
  - No internal failure ever yields `APPROVED`: failures return 503.
- **Dependencies.** M2-03.
- **Tests.** Each rule type, boundaries, multiple matches, precedence, determinism under shuffled input.
- **Status.** Done (committed locally; not pushed or reviewed).

## M2-05 Decision persistence

- **Problem.** Every decision must remain explainable and unchanged.
- **Scope.** `policy_decisions`:
  - transaction, organization, policy, version and label;
  - decision, reason codes, the evaluated inputs (type, currency, amount), a request fingerprint and a correlation id;
  - `policy_decision_matches`, one row per matched rule.

  Account ids and the principal are not stored.
- **Acceptance criteria.**
  - Decision rows can't be updated, deleted or truncated (triggers).
  - A decision stays linked to its version after a newer version is activated.
- **Dependencies.** M2-04.
- **Tests.** Historical determinism test; raw-SQL immutability tests.
- **Status.** Done (committed locally; not pushed or reviewed).

## M2-06 Policy API

- **Problem.** Management and evaluation are separate concerns.
- **Scope.**
  - Management: `POST/GET /api/v1/policies`, `GET /api/v1/policies/{id}`, `POST/GET …/versions`, `GET …/versions/{n}`, `POST …/versions/{n}/activate|retire`, `GET /api/v1/policy-decisions/{decisionId}`.
  - Evaluation: `POST /v1/policy-decisions` (the contract path).
  - Explicit commands only; no PATCH.
- **Acceptance criteria.**
  - Errors are RFC 9457 problem details with a `code`.
  - Management writes require `X-Actor-Id`.
- **Dependencies.** M2-02 to M2-05.
- **Tests.** MockMvc API tests against PostgreSQL.
- **Status.** Done (committed locally; not pushed or reviewed).

## M2-07 Contract compatibility

- **Problem.** The Spring implementation must match contract v1 exactly.
- **Scope.**
  - Strict request binding: unknown fields rejected; strings never coerced from numbers or booleans.
  - `contractVersion` check (a non-1.x version → 409).
  - The idempotency conflict → 409.
  - `X-Correlation-Id` echoed.
  - Document the clarifications as contract 1.0.1 (description only).
- **Acceptance criteria.**
  - Every `*.valid.json` example is accepted.
  - Every `*.invalid.json` example is rejected with 400.
  - Every 200 body validates against the response schema, and every error body against `ProblemDetails`.
  - `contracts/validate.sh` passes.
- **Dependencies.** M2-06.
- **Tests.** Contract test class using the repository schemas (networknt JSON Schema 2020-12) over real HTTP serialization.
- **Status.** Done (committed locally; not pushed or reviewed).

## M2-08 PostgreSQL integration testing

- **Problem.** Constraints, triggers and locking can't be proven with mocks.
- **Scope.** Testcontainers PostgreSQL 18.6 running the repository's real init script, connected as `policy_app`; Flyway migrations.
- **Acceptance criteria.** Every database-relevant case runs against real PostgreSQL.
- **Dependencies.** M2-05.
- **Tests.** This item is the tests.
- **Status.** Done (committed locally; not pushed or reviewed).

## M2-09 Concurrency and activation safety

- **Problem.** Competing activations and duplicate evaluations must be safe.
- **Scope.**
  - Activation locks the policy row, and the partial unique index is the backstop.
  - Evaluation idempotency via `INSERT … ON CONFLICT (transaction_id) DO NOTHING` plus a fingerprint comparison.
- **Acceptance criteria.**
  - Concurrent activations → exactly one `ACTIVE`.
  - Raw-SQL competing activations → a unique violation.
  - N concurrent identical evaluations → one decision row, one `decisionId`.
  - Conflicting duplicates → 409; the stored decision is unchanged.
- **Dependencies.** M2-02, M2-05.
- **Tests.** Real concurrent connections with latch-started threads, and a `pg_locks` wait probe instead of sleeps.
- **Status.** Done (committed locally; not pushed or reviewed).

## M2-10 Documentation and verification

- **Problem.** The design and its limits must be written down accurately.
- **Scope.** `docs/architecture/policy-engine.md`; ADRs for real decisions only; README and contract docs; quality gates.
- **Acceptance criteria.**
  - The docs match the code.
  - The security boundary is stated honestly.
- **Dependencies.** All of the above.
- **Tests.** Quality gates: Spring `clean spotless:check verify`, `contracts/validate.sh`, and the .NET build and tests (unchanged service).
- **Status.** Done (committed locally; not pushed or reviewed).
