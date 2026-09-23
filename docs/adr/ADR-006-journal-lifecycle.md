# ADR-006 — Journal Lifecycle

- Status: Proposed (to be confirmed by LMichy1 in Milestone 1 and by Ngetich-86 for the failure paths in Milestone 3)
- Date: 2026-09-23

## Context

The initial brief proposed:

```text
DRAFT → PENDING_APPROVAL → APPROVED → POSTED → REVERSED
                        ↘ REJECTED
```

Each state was reviewed against two questions: does it carry information no other state does, and does it force an immutable row to change?

## Decision

Stored states:

```text
            submit (balanced)
  DRAFT ─────────────────────▶ PENDING_APPROVAL ──policy APPROVED / reviewer approves──▶ APPROVED ──post (tx)──▶ POSTED
    │                              │   ▲                                                        │
 discard                policy REJECTED│  timeout / 5xx / REVIEW_REQUIRED                    post fails
 (delete)              or reviewer     │  (stays; decision recorded)                    (stays APPROVED,
                       rejects         └──┘                                                  retryable)
                           ▼
                        REJECTED
```

- **DRAFT.** Editable; no financial effect. Can be discarded (deleted) because it never had any effect.
- **PENDING_APPROVAL.** Content is frozen. Every policy call and its outcome, including timeouts, is appended to a decision log for the journal. `REVIEW_REQUIRED` is recorded here and doesn't have its own state: manual review is a way of reaching approval, not a separate position in the ledger.
- **APPROVED.** Kept distinct from `POSTED`. Approval can arrive asynchronously (a human reviewer), and posting can still fail afterwards, for example on a closed period, a closed account or lost concurrency. Collapsing the two would force approval and posting into one transaction that also depends on a human.
- **REJECTED.** Terminal. Retained for audit.
- **POSTED.** Terminal and immutable (ADR-004).

**`REVERSED` is derived, not stored.** A posted journal is "reversed" when a posted journal exists whose `reverses_journal_id` points to it. Storing `REVERSED` on the original row would mean updating a posted row, which contradicts ADR-004. A unique index on `reverses_journal_id` enforces at most one reversal per journal.

Reversal journals follow the same lifecycle. Whether they require policy approval is a policy decision: `transactionType: REVERSAL` is sent like any other type.

## Alternatives

- **Brief's model as-is, with `REVERSED` as a stored state.** Simpler queries, but it requires mutating posted rows.
- **Separate `IN_REVIEW` state.** Makes review queues a simple status filter. Rejected for now: the decision log already distinguishes "awaiting review" from "awaiting retry". Revisit if review queue queries become complex.
- **`FAILED` state for policy timeouts.** Rejected: it's indistinguishable in meaning from "still pending", and it invites a code path that treats `FAILED` as final.
- **Skip `APPROVED` (approve and post atomically).** Only works if approval is always synchronous, which manual review rules out.

## Consequences

- Queries for "reversed journals" need a join or an `EXISTS` query; a view can hide this.
- The decision log becomes a first-class table in Milestone 1/3.
- Transition rules are enforced in the domain model and guarded in the database (Milestone 1/4).

## Known limitations

- Draft deletion leaves no trace. If auditors later need draft history, add `DISCARDED` as a stored state.
- Expiry of long-pending journals is not modelled.
