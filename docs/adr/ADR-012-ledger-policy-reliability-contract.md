# ADR-012 — Ledger–Policy Reliability Contract

- Status: Accepted
- Date: 2026-09-23 (Milestone 3)
- Reviews required: @LMichy1 (ledger lifecycle and schema), @MurayaSoftTouch (policy side)

## Context

ADR-005 said the ledger must fail closed and listed the outcomes it has to distinguish. ADR-006 said approval comes from policy. ADR-011 made the policy service idempotent on `transactionId`. Milestone 3 connects the two services, so it has to settle four things:
- exactly how a policy call is made, bounded and retried;
- how an outcome becomes ledger state;
- how an outcome lost in transit is recovered;
- what proves a journal was approved.

## Decision

**Identity.** The ledger sends the **journal id as `transactionId`**, and the **ledger id as `organizationId`**, because the ledger has no organization model yet and a ledger is the policy scope. Every retry of a journal's evaluation is therefore the same policy transaction, and ADR-011 makes the policy service return the same decision.

**The call happens outside any database transaction.** `submit` commits `DRAFT → PENDING_APPROVAL` first. `PolicyApproval` then:
1. builds the request from **persisted** state (entries, account types, the journal's type);
2. calls the policy service with no locks held;
3. applies the outcome in a new transaction that locks the journal row (`FOR UPDATE`), re-reads it, inserts the evidence and transitions the journal.

A slow policy service never holds ledger locks.

**Outcome mapping.**

| Policy outcome | Ledger effect | Evidence row |
| --- | --- | --- |
| `200 APPROVED` | `PENDING_APPROVAL → APPROVED` | yes |
| `200 REJECTED` | `PENDING_APPROVAL → REJECTED` | yes |
| `200 REVIEW_REQUIRED` | stays `PENDING_APPROVAL`; `manualReviewRequired = true` | yes |
| timeout · connection failure · `5xx` | stays `PENDING_APPROVAL`; retryable | no |
| `409 IDEMPOTENCY_CONFLICT` | stays `PENDING_APPROVAL`; alert, don't retry blindly | no |
| `400`, `409 CONTRACT_VERSION_UNSUPPORTED`, schema-invalid body, unknown `decision`, mismatched `transactionId` | stays `PENDING_APPROVAL`; contract violation | no |
| `401`/`403` | stays `PENDING_APPROVAL`; configuration error | no |

No failure ever becomes `REJECTED` or `APPROVED`. Failures are logged; they are never recorded as decisions.

**Evidence** (`journal_policy_decisions`, one row per journal) holds the decision id, policy version, decision, reason codes, evaluated-at, contract version, correlation id and received-at. It is append-only (triggers). The ledger lifecycle trigger now **requires matching evidence** for `APPROVED` and `REJECTED`, so no code path or SQL statement can approve a journal without a recorded policy decision. `POSTED` requires `APPROVED`, so posting requires it too. Policy rules are not copied into the ledger.

**Bounded retries** (Microsoft.Extensions.Http.Resilience; values configurable, defaults shown):

| Setting | Default | Meaning |
| --- | --- | --- |
| `Ledger:PolicyDecisionTimeoutMs` | 2000 | total budget, including backoff |
| `Ledger:PolicyAttemptTimeoutMs` | 800 | per attempt |
| `Ledger:PolicyMaxRetries` | 2 | extra attempts (3 in total) |
| backoff | exponential from 100 ms, with jitter | `Retry-After` is ignored so the budget stays short |

Only connection failures, attempt timeouts, `502`, `503` and `504` are retried. `400`, `401`/`403`, `409`, `500`, business decisions and contract violations are not.

**Ambiguous outcomes.** If the policy service committed a decision but the response was lost, the journal stays `PENDING_APPROVAL`, and the next `request-approval` (or an in-pipeline retry) gets the stored decision back. The ledger records it once (the evidence primary key), and the transition happens once (the row lock plus the trigger). Once evidence exists, the ledger never calls the policy service again for that journal.

**Transaction type.** Contract v1 requires `transactionType`, so journals gained an immutable `JournalType` (`PAYMENT`, `TRANSFER`, `ADJUSTMENT`, `FEE`, and `REVERSAL` for reversals only). Journals that existed before the migration are backfilled as `ADJUSTMENT`, the most review-prone type.

## Alternatives

- **Call the policy service inside the posting or submit transaction.** Simpler, but it holds row locks across a network call, and a timeout would roll back the submission.
- **Treat `REVIEW_REQUIRED` as a separate stored state.** Rejected in ADR-006; the evidence row already carries it.
- **Store the policy decision on the journal row.** It would need non-transition updates of a `PENDING_APPROVAL` row, which ADR-007 forbids. A separate append-only table keeps the journal row's rules intact.
- **Honour `Retry-After`.** The policy service sends 1 s, which would consume half the budget. The caller retries `request-approval` later instead.

## Consequences

- A policy outage makes new journals wait in `PENDING_APPROVAL`; nothing is lost and nothing is approved.
- A journal marked `REVIEW_REQUIRED` needs a manual-review workflow to proceed, and that workflow doesn't exist yet (see Known limitations).
- The request builder, retry values and failure mapping are fixed by tests: the client pipeline, the consumer contract tests and the multi-service suite.

## Known limitations

- **No manual-review workflow.** A `REVIEW_REQUIRED` journal stays pending indefinitely. A reviewer decision, with its own evidence, is future work.
- **No automatic re-evaluation of pending journals.** A caller must retry `request-approval`; a sweeper is future work.
- The `organizationId = ledgerId` mapping must change when organizations exist.
