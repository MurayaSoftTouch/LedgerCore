# Milestone 4 — Posting Idempotency and Financial Hardening

- Owner: @LMichy1
- Branch: `feat/m4-posting-hardening` (from `feat/m3-service-integration` @ `887fdcf`)
- These are local work items, not GitHub issues. GitHub assigns issue numbers when someone with write access creates the issues.
- **Reviews required before merge:**
  - @Ngetich-86, for the outbox binding, the API header change as it affects `tests/integration` and `compose-smoke.sh`, and reconciliation, which Milestone 5 builds on.
  - @MurayaSoftTouch, to confirm the policy contract is unchanged; nothing on the policy side is expected to change.

**Out of scope:**
- an outbox publisher or broker;
- a manual-review workflow;
- expiry of idempotency records;
- scheduled or cross-system reconciliation (Milestone 5);
- any change to the policy language or contract;
- a user interface.

**Shared definition of done:**
- Acceptance criteria are covered by automated tests against real PostgreSQL.
- These gates are green: ledger `dotnet build`, `dotnet test` and `dotnet format --verify-no-changes`; policy `./mvnw clean spotless:check verify`; `contracts/validate.sh`; `verify-isolation.sh`; and the multi-service suite.
- The docs are updated.
- The work is committed as LMichy1.

---

## M4-01 Posting idempotency

- **Problem.** A client that loses the response to `post` can't tell whether the journal was posted. Today a retry gets `409 JOURNAL_ALREADY_POSTED` instead of the original result.
- **Scope.**
  - `post` requires an `Idempotency-Key` header.
  - An append-only claim table, scoped by ledger and operation, records the key, a request fingerprint, the target journal and the result.
  - The claim is written in the posting transaction and decided by a PostgreSQL unique index (`INSERT … ON CONFLICT DO NOTHING`).
  - A retry with the same key and request replays the result from persisted state.
- **Acceptance criteria.**
  - An identical retry returns the original posted journal and writes nothing.
  - Unknown outcomes (a committed post whose response was lost) are recovered by retrying with the same key.
  - A missing, malformed or oversized key → `400`.
- **Dependencies.** Milestone 1 posting; Milestone 3 outbox.
- **Tests.** API and PostgreSQL tests for the first request, replay, unknown outcome and header validation.
- **Status.** Planned.

## M4-02 Idempotency conflict handling

- **Problem.** Reusing a key for a different request must never replay the wrong result.
- **Scope.**
  - A versioned SHA-256 fingerprint over the fields that define the command: operation, ledger, target journal, actor, and for reversal the description.
  - Transport metadata such as the correlation id is excluded.
  - Same key with a different fingerprint → `409 IDEMPOTENCY_CONFLICT`.
- **Acceptance criteria.**
  - A conflict changes nothing: no posting, no event, no audit row.
  - The fingerprint fields are documented.
- **Dependencies.** M4-01.
- **Tests.** Conflict on a different journal, a different actor, and a different reversal description.
- **Status.** Planned.

## M4-03 Posting concurrency hardening

- **Problem.** Idempotency must supplement the posting state machine, not replace it.
- **Scope.**
  - Concurrent requests for the same journal serialize on its row lock.
  - Concurrent requests with the same key on different journals are decided by the unique index.
  - The database refuses a posting that has no posting claim, and allows at most one posting claim per journal.
- **Acceptance criteria.**
  - 8 concurrent identical requests → one posting, eight identical successful responses.
  - 8 concurrent requests with different keys → one posting, seven `409 JOURNAL_ALREADY_POSTED`.
  - The same key on two journals concurrently → one wins, the other gets `409 IDEMPOTENCY_CONFLICT`, and that journal stays postable.
- **Dependencies.** M4-01.
- **Tests.** Latch-started concurrent tests on real PostgreSQL; a paused-commit race test.
- **Status.** Planned.

## M4-04 Approval-evidence binding

- **Problem.** Posting relies on approval evidence only indirectly (`POSTED` requires `APPROVED`, which required evidence).
- **Scope.**
  - The posting transition itself requires `APPROVED` evidence for the same journal.
  - The `JournalPosted` event must carry that evidence's decision id (checked at commit).
  - The approval flow refuses a decision whose `transactionId` is not the journal.
  - Posting uses the persisted evidence and never calls the policy service.
- **Acceptance criteria.**
  - A journal without `APPROVED` evidence can't be posted, even if a bypass left it `APPROVED`.
  - `REJECTED` and `REVIEW_REQUIRED` journals can't be posted.
  - Evidence for another journal is refused.
  - Evidence can't change after posting.
  - Posting succeeds while the policy service is down.
- **Dependencies.** Milestone 3 evidence.
- **Tests.** Database guard tests, approval tests, and a multi-service test with the policy service cut off.
- **Status.** Planned.

## M4-05 Reversal idempotency

- **Problem.** A retried `reverse` should return the reversal it created, not `409 JOURNAL_ALREADY_REVERSED`.
- **Scope.**
  - `reverse` requires an `Idempotency-Key`.
  - The claim records the reversal journal as its result.
  - The database requires every reversal journal to have exactly one reversal claim.
- **Acceptance criteria.**
  - An identical retry returns the same reversal journal.
  - A conflicting reuse of the key → `409`.
  - Concurrent reversals → exactly one live reversal.
  - The original journal is never written.
- **Dependencies.** M4-01, M4-02.
- **Tests.** Replay, conflict, concurrency and an original-unchanged check against PostgreSQL.
- **Status.** Planned.

## M4-06 Reversal/approval interaction

- **Problem.** The approval semantics for reversals have to be confirmed, not assumed.
- **Scope.**
  - Confirm the ADR-006 behaviour: a reversal is created as `PENDING_APPROVAL`, is evaluated by the real policy service as `transactionType: REVERSAL`, and needs `APPROVED` evidence before it can post.
  - A replayed `reverse` re-requests approval only while no decision is recorded.
- **Acceptance criteria.**
  - A reversal that is rejected or under review can't post.
  - A replay while the policy service is down leaves the reversal pending, and a later replay gets it approved.
  - There is no test-only bypass.
- **Dependencies.** M4-05.
- **Tests.** Reversal approval tests with the stub policy client.
- **Status.** Planned.

## M4-07 Ledger reconciliation

- **Problem.** Balances are derived, and nothing in the product checks that the derivation is consistent.
- **Scope.** A read-only reconciliation report per ledger, computed in one snapshot from posted entries only. For each currency it checks:
  - every posted journal balances;
  - total debits equal total credits;
  - net movement per account;
  - every posted reversal exactly mirrors its original.

  It is exposed as `GET /api/v1/ledgers/{id}/reconciliation`.
- **Acceptance criteria.**
  - Draft, pending, approved-but-unposted and rejected journals don't affect the report.
  - An original and its posted reversal net to zero.
  - Both journals remain visible.
- **Dependencies.** Milestone 1 balances.
- **Tests.** Reconciliation tests against PostgreSQL.
- **Status.** Planned.

## M4-08 Audit completeness

- **Problem.** The transition audit records what happened, to which journal, when, by whom, and from and to which state. It doesn't record which policy decision authorized a transition, or which idempotent command caused it.
- **Scope.**
  - Add `policy_decision_id` and `idempotency_key` to `journal_status_transitions`, filled by the audit trigger from the evidence and the claim.
  - The audit stays append-only.
- **Acceptance criteria.**
  - An `APPROVED`, `REJECTED` or `POSTED` transition carries the decision id.
  - A `POSTED` transition, and a reversal's `PENDING_APPROVAL` transition, carry the key.
  - A replay adds no audit rows.
- **Dependencies.** M4-01, M4-04, M4-05.
- **Tests.** Audit assertions in the posting and reversal tests.
- **Status.** Planned.

## M4-09 Failure injection

- **Problem.** Atomicity has to be demonstrated at each transaction boundary, including the new claim.
- **Scope.** Inject failures:
  - on the claim insert;
  - on the posting update;
  - on the outbox insert;
  - before commit;
  - on the reversal claim.
- **Acceptance criteria.**
  - After each failure there is no posting, no event, no audit row and no claim.
  - A retry with the same key succeeds.
  - No key is ever left unusable.
- **Dependencies.** M4-01 to M4-05.
- **Tests.** Command and commit interceptors against PostgreSQL.
- **Status.** Planned.

## M4-10 Documentation and verification

- **Problem.** The guarantees and their limits must be written down accurately.
- **Scope.**
  - `docs/architecture/posting-idempotency.md`.
  - An ADR for the idempotency decision.
  - Updates to the ledger domain doc, the README, the API usage in `tests/integration` and `compose-smoke.sh`.
  - All the quality gates.
- **Acceptance criteria.**
  - The docs match the code.
  - No claim that remote CI passed.
- **Dependencies.** All of the above.
- **Tests.** The quality gates.
- **Status.** Planned.
