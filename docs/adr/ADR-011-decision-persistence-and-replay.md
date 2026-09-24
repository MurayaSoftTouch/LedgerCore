# ADR-011 — Policy Decision Persistence and Replay

- Status: Accepted
- Date: 2026-09-23 (Milestone 2)

## Context

The ledger retries policy calls (ADR-005). A retry must not create a second, possibly different, decision for the same journal. Decisions must stay explainable after policies change. And the choice of version for a request must not be something the caller can steer.

## Decision

**One immutable decision per `transactionId`.** Stored in `policy_decisions`:
- the ids: transaction, organization, policy, version and label;
- the decision and its reason codes;
- the evaluated inputs: transaction type, currency and amount;
- a request fingerprint;
- the contract version and correlation id.

Each matched rule goes in `policy_decision_matches`. Composite foreign keys guarantee a match references a rule of *the same version* as its decision. Account ids, the requesting principal and balances are **not** stored. Triggers make decisions and matches append-only: no update, delete or truncate, and matches only in the decision's own transaction.

**Binding: first evaluation wins.**
- The version is the one `ACTIVE` when the transaction is first evaluated. It is share-locked (`FOR SHARE`) until the decision commits, so an activation waits for in-flight evaluations. A trigger also refuses decisions from a non-`ACTIVE` version.
- The request's `requestedAt` is **not** used to select a version. It is client-supplied, so honouring it would let a caller backdate a request into an older, looser policy.
- Determinism over time comes from persistence: re-asking about a transaction returns the stored decision, whatever has been activated since.

**Idempotency** (contract 1.0.1):
- The fingerprint is SHA-256 over the decision-relevant fields: organization, type, currency, amount (numerically normalised, so `"100.50"` = `"100.5"`), and the account context as a sorted multiset.
- Same `transactionId` + same fingerprint → the stored decision, byte-identical, with `X-Decision-Replayed: true`.
- Same `transactionId` + different fingerprint → `409 IDEMPOTENCY_CONFLICT`; the stored decision is untouched.
- Concurrency is decided by PostgreSQL. The write is `INSERT … ON CONFLICT (transaction_id) DO NOTHING`, and the loser reads the winner's committed row. There is no check-then-insert.

**Fail-safe:**
- Anything that prevents a trustworthy evaluation returns `503 POLICY_EVALUATION_UNAVAILABLE` with `Retry-After` and records nothing. That includes a store failure, an invalid stored rule, or an unexpected exception.
- A domain validation error raised while rebuilding stored rules is treated as "cannot evaluate", never as a client error.
- A check constraint makes `APPROVED` impossible without a policy version.

## Alternatives

- **Effective-dated selection by `requestedAt`.** Rejected: it trusts client time, as explained above.
- **Re-evaluating on every request** without storing the decision. The answer could change between retries, and there would be no audit trail.
- **An idempotency key separate from `transactionId`.** Unnecessary: the ledger already guarantees `transactionId` is stable across retries (ADR-005).
- **Storing the full request.** More forensic data, but it keeps principal and account identifiers the rules don't need. The fingerprint proves request identity without retaining them.

## Consequences

- A transaction first decided `REVIEW_REQUIRED` because no policy existed stays that way. A new decision needs a new `transactionId`, which is the ledger's call in Milestone 3.
- Decision history grows by one row per transaction plus one per matched rule.
- Evaluations and activations of the same policy briefly serialise.

## Known limitations

- The fingerprint excludes `requestedBy`. A retry by a different principal replays the original decision. That's intended, since no rule reads the principal, but it would need revisiting if principal-based rules arrive.
- Retention and archival of decision history aren't designed yet.
- The row-inserted-in-this-transaction test uses a 32-bit `xmin` comparison. That's sound for live transactions, but it is theoretically ambiguous across transaction-ID wraparound for unfrozen rows.
