# ADR-006 — Journal Lifecycle

- Status: Accepted (Milestone 1, LMichy1). The failure-path rows remain subject to confirmation by Ngetich-86 when the policy client lands in Milestone 3.
- Date: proposed 2026-09-23; accepted 2026-09-23

## Review history

| Date | Change | By |
| --- | --- | --- |
| 2026-09-23 | Proposed in Milestone 0 | LMichy1 |
| 2026-09-23 | Accepted in Milestone 1, with the refinements listed under "Refinements at acceptance". The original decision text is kept below them. | LMichy1 |

## Context

The initial brief proposed:

```text
DRAFT → PENDING_APPROVAL → APPROVED → POSTED → REVERSED
                        ↘ REJECTED
```

Each state was reviewed against two questions: does it carry information no other state does, and does it force an immutable row to change?

## Decision

Stored states (`journals.status`): `DRAFT`, `PENDING_APPROVAL`, `APPROVED`, `REJECTED`, `POSTED`. `REVERSED` is **not** stored.

```text
            submit (balanced)
  DRAFT ─────────────────────▶ PENDING_APPROVAL ──approval──▶ APPROVED ──post (tx)──▶ POSTED
                                   │   ▲                          │
                          rejection│   │ timeout / 5xx /       post fails
                                   │   │ REVIEW_REQUIRED       (stays APPROVED,
                                   ▼   └─(stays; recorded)      retryable)
                                REJECTED
```

- **DRAFT.** Entries can be added; no financial effect.
- **PENDING_APPROVAL.** Content is frozen. `REVIEW_REQUIRED` is recorded here and doesn't have its own state: manual review is a way of reaching approval, not a separate position in the ledger.
- **APPROVED.** Kept distinct from `POSTED`. Approval can arrive asynchronously (a human reviewer), and posting can still fail afterwards (inactive account, lost concurrency). Collapsing the two would force approval and posting into one transaction that also depends on a human.
- **REJECTED.** Terminal. Retained for audit. Reserved for an explicit business rejection.
- **POSTED.** Terminal and immutable (ADR-004).

### Adopted decision 1: `REVERSED` is derived, never stored

A posted journal is never updated to record that it was reversed. It *is reversed* when a journal in `POSTED` state exists whose `reverses_journal_id` points to it. The API exposes this as `isReversed` and `reversedByJournalId`, computed at read time.

```text
journal A (status = POSTED, never modified again)
        ▲
        └── reverses_journal_id ── journal B (status = POSTED)   ⇒  A.isReversed = true
```

Storing `REVERSED` on A would mean updating a posted row, which contradicts ADR-004. It would also let the flag disagree with the existence of B.

### Adopted decision 2: operational failure is not rejection

A policy timeout, an unavailable policy service, an internal policy error or a contract incompatibility leaves the journal in `PENDING_APPROVAL` until a trustworthy decision exists. Only an explicit `REJECTED` decision moves it to `REJECTED`. The business never said no, so the ledger must not record that it did. It also never said yes, so the journal must not move forward (ADR-005).

### Refinements at acceptance (Milestone 1)

These came out of implementing the lifecycle. They narrow the proposal; they don't change its shape.

1. **Reversals skip visible DRAFT.** A reversal's entries are computed from the original, so an editable draft would only create a window for tampering. The reversal is inserted and moved to `PENDING_APPROVAL` in a single transaction. Nothing outside that transaction ever sees it in `DRAFT`. From `PENDING_APPROVAL` onwards it follows the normal lifecycle, so policy can decide on `transactionType: REVERSAL` in Milestone 2.
2. **At most one live reversal.** A partial unique index `ON journals (reverses_journal_id) WHERE status <> 'REJECTED'` allows one pending, approved or posted reversal per journal. If a reversal is rejected, a new one may be requested. A reversal cannot itself be reversed; the fix is a new correcting journal (ADR-004).
3. **Submit requires balance.** `DRAFT → PENDING_APPROVAL` enforces the double-entry invariant, so policy is only ever asked about balanced journals. `POSTED` re-checks it in the database.
4. **No draft discard in Milestone 1.** The proposal allowed deleting drafts. Milestone 1 ships no delete operation of any kind, and the runtime database role has no `DELETE` privilege on ledger tables (ADR-007). Discarding drafts can be added later as a separate decision.
5. **Every status change is audited by the database.** A trigger appends to `journal_status_transitions`. The application can't skip or rewrite that record (ADR-007).
6. **Approval in Milestone 1 is not a production workflow.** Nothing reaches `APPROVED` through the public API. Tests use an internal, test-only approval recorder. Milestone 2/3 replaces it with a recorded policy decision.

## Alternatives

- **Brief's model as-is, with `REVERSED` as a stored state.** Simpler queries, but it requires mutating posted rows.
- **Separate `IN_REVIEW` state.** Makes review queues a simple status filter. Rejected for now: the decision log (Milestone 3) will distinguish "awaiting review" from "awaiting retry". Revisit if review queue queries become complex.
- **`FAILED` state for policy timeouts.** Rejected: it's indistinguishable in meaning from "still pending", and it invites a code path that treats `FAILED` as final.
- **Skip `APPROVED` (approve and post atomically).** Only works if approval is always synchronous, which manual review rules out.
- **Unique index on `reverses_journal_id` including rejected reversals** (the Milestone 0 wording). Rejected at acceptance: one policy rejection of a reversal would block that journal from ever being corrected.

## Consequences

- Queries for "reversed journals" need an `EXISTS` query on posted reversals.
- Transition rules are enforced in the domain model **and** in a PostgreSQL trigger (ADR-007).
- Failure-path handling (decision log, retry) is Milestone 3 scope; the state model already accommodates it.

## Known limitations

- There is no draft deletion and no expiry of long-pending journals.
- There is no production approval path until policy integration (Milestone 2/3).
