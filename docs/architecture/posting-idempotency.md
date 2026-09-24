# Posting Idempotency and Financial Hardening

This document covers Milestone 4: how `post` and `reverse` stay correct under retries, concurrency and failure, how posting is bound to its approval evidence, and how the ledger reconciles itself. The idempotency decision is [ADR-015](../adr/ADR-015-idempotent-posting-and-reversal.md). The journal lifecycle is [ADR-006](../adr/ADR-006-journal-lifecycle.md).

## API

```http
POST /api/v1/ledgers/{ledgerId}/journals/{journalId}/post
X-Actor-Id: clerk-17
Idempotency-Key: 5a0e7a4e-1c1d-4bb8-9d4f-6c2a1f0e8b21
```

| Situation | Response |
| --- | --- |
| First request | `200` with the posted journal and its policy decision |
| Same key, same request | `200` with the same body, plus `Idempotency-Replayed: true`; nothing is written |
| Same key, different request (another journal, another actor) | `409 IDEMPOTENCY_CONFLICT`; nothing changes |
| New key, journal already posted | `409 JOURNAL_ALREADY_POSTED` |
| No key | `400 IDEMPOTENCY_KEY_REQUIRED` |
| Key too short, too long (> 128), or with other characters | `400 IDEMPOTENCY_KEY_INVALID` |

`reverse` works the same way: `201`, and the replay returns the **same reversal journal**. A reused key is `409 IDEMPOTENCY_CONFLICT` if the original journal or the `description` differs.

The key is scoped by ledger and operation. The same key may be used once for a posting and once for a reversal.

## Request fingerprint

`CommandFingerprint` is a SHA-256 over a versioned, JSON-encoded tuple.

| Operation | Fields |
| --- | --- |
| `POST_JOURNAL` | version, operation, ledger id, journal id, actor (`X-Actor-Id`) |
| `REVERSE_JOURNAL` | version, operation, ledger id, original journal id, actor, requested `description` (null and `""` differ) |

These are excluded, so a genuine retry always matches:
- `X-Correlation-Id`;
- timestamps;
- every other header.

## Protocol

One transaction per request, in `JournalCommands.IdempotentAsync`:

```text
BEGIN
  SELECT … FROM journals WHERE id = target FOR UPDATE          -- same-journal requests queue here
  claim := SELECT … FROM command_idempotency WHERE (ledger, op, key)
  claim exists?  same fingerprint → replay result (no writes)     -- ROLLBACK
                 different       → 409 IDEMPOTENCY_CONFLICT       -- ROLLBACK
  prepare (validate; a reversal inserts its draft)
  INSERT INTO command_idempotency … ON CONFLICT ON CONSTRAINT pk DO NOTHING
     0 rows → another transaction committed this key → ROLLBACK, run once more (→ replay or conflict)
  effect (POSTED + JournalPosted event, or submit the reversal)
COMMIT                                                          -- deferred checks run here
```

- **Crash and unknown outcome.**
  - If the transaction committed, the claim exists and any retry replays it.
  - If it didn't commit, the claim doesn't exist either, and a retry with the same key runs normally.
  - A retry that arrives while the original is still in flight waits on the journal lock, then replays.
  - The client never has to guess.
- **No stale `IN_PROGRESS` claims.** A claim is never written outside the effect's transaction, so a crash can't leave a key permanently unusable. No expiry is needed.
- **Replay from persisted state.** The response is rebuilt from the database, including for the first request, so the original and the replay are identical. A posted journal is immutable, so its replay never changes. A replayed reversal shows its current lifecycle state.

## Concurrency

| Race | Result | Mechanism | Test |
| --- | --- | --- | --- |
| 8 identical requests (same journal, same key) | one posting; 1 original + 7 replays with identical results | journal row lock, then the claim lookup | `PostingIdempotencyTests.ConcurrentIdenticalRequestsPostOnceAndAllReturnTheSameResult` |
| 8 requests with different keys (same journal) | one posting; 7 × `JOURNAL_ALREADY_POSTED` | row lock + posting state machine; backstop `ux_command_idempotency_posting` | `…ConcurrentRequestsWithDifferentKeysStillPostOnce` |
| One key on two journals at once | one wins; the other gets `IDEMPOTENCY_CONFLICT` and stays postable | the claim's primary key (`ON CONFLICT DO NOTHING` waits on the uncommitted claim) | `…ConcurrentUseOfOneKeyOnTwoJournalsIsDecidedByTheDatabase` |
| A retry while the original is in flight | waits, then replays | journal row lock | `…RetryWhileTheOriginalIsStillInFlightWaitsAndReplays` |
| 8 identical reversal requests | one reversal, 7 replays | original's row lock, then the claim lookup | `ReversalIdempotencyTests.ConcurrentIdenticalRequestsCreateOneReversal` |
| 8 reversal requests with different keys | one live reversal; 7 × `JOURNAL_ALREADY_REVERSED` | row lock; backstop `ux_journals_live_reversal` | `…ConcurrentRequestsWithDifferentKeysCreateOneLiveReversal` |

Idempotency supplements the Milestone 1 protections and replaces none of them. The posting transition is still a single locked state change that can happen once.

## Approval-evidence binding

Posting requires trustworthy `APPROVED` evidence for **that** journal:

- **Application.**
  - The approval flow refuses a decision whose `transactionId` is not the journal.
  - `PostAsync` reads the journal's evidence and refuses anything but `APPROVED` (`APPROVAL_EVIDENCE_REQUIRED`).
- **Database.**
  - The `POSTED` transition requires an `APPROVED` row in `journal_policy_decisions` for the journal, in addition to the Milestone 3 rule that `APPROVED` required it.
  - Evidence is keyed by journal, its `decision_id` is unique, and it is append-only.
- **Event.** A `JournalPosted` event can't commit unless its `policyDecisionId` is the journal's `APPROVED` decision.
- **Audit.** The `APPROVED` and `POSTED` transitions record that decision id.

The chain is: journal → policy transaction (the journal id) → policy decision (`decision_id`) → evidence row → posting transition and `JournalPosted` event.

**Posting doesn't need the policy service.** It uses only the persisted evidence and makes no network call. `IdempotencyApiTests.PostingNeverCallsThePolicyService`, and the multi-service test `PostingIsIdempotentAndNeedsNoPolicyServiceOnceApproved` (which posts with the policy service cut off), prove it.

## Reversals and policy

This is the existing ADR-006 behaviour, not a new decision:
- A reversal is created and submitted atomically, then evaluated by the real policy service as `transactionType: REVERSAL`.
- It needs `APPROVED` evidence to post like any journal.
- A rejected reversal or one under review can't post.
- A replayed `reverse` requests approval again only while no decision is recorded, so a reversal created while the policy service was down gets approved by a later retry.
- There is no bypass.

Reversal history is never hidden:
- the original stays `POSTED` and unchanged;
- the reversal is a separate posted journal;
- "reversed" is derived (ADR-006).

## Reconciliation

`GET /api/v1/ledgers/{ledgerId}/reconciliation` (`LedgerReconciliation`) recomputes everything from `POSTED` entries with plain SQL, in one read-only `REPEATABLE READ` snapshot:

| Check | Expectation |
| --- | --- |
| `currencies[]` | per currency, total debits = total credits (currencies never mix) |
| `unbalancedJournals` | empty: every posted journal has a debit, a credit and equal sums |
| `mismatchedReversals` | empty: every posted reversal exactly mirrors a posted original |
| `accounts[]` | net movement (debits − credits) per account, summing to zero per currency |
| `consistent` | all of the above |

- Draft, pending, approved-but-unposted and rejected journals never count.
- An original and its reversal both count, and net to zero for the affected accounts.
- The database guards make an inconsistent report impossible without bypassing them. `ReconciliationTests.DataThatBypassedTheGuardsIsReported` tampers as a superuser with triggers disabled and shows that the report catches it.
- Scheduled and cross-system reconciliation is Milestone 5.

## Audit

`journal_status_transitions` answers these questions for every transition:

| Question | Column |
| --- | --- |
| what happened | `from_status`, `to_status` |
| to which journal | `journal_id` |
| when | `occurred_at` |
| by whom | `actor` |
| which decision authorized it | `policy_decision_id`, for `APPROVED`, `REJECTED` and `POSTED` |
| which idempotent command caused it | `idempotency_key`, for `POSTED` and for a reversal's `PENDING_APPROVAL` |

- Rows are written only by a trigger, and are append-only.
- A replay adds no rows.
- The claim row adds the requester, the fingerprint and the correlation id.
- No credentials are stored.

## Outbox

- There is exactly one `JournalPosted` event per journal: the unique index on `(aggregate_id, event_type)`, and a replay writes nothing.
- Its payload names the posting's `APPROVED` decision.
- There is no publisher. Delivery will be at-least-once, and consumers deduplicate on the event id (ADR-014).

## Failure injection

Each case injects a failure inside the transaction and then asserts:
- the journal is still `APPROVED`;
- there is no `POSTED` transition, no event and no claim;
- the audit row count is unchanged;
- a retry with the **same key** succeeds normally.

| Injected at | Test |
| --- | --- |
| the claim insert | `PostingIdempotencyTests.InjectedFailureAtAnyStatementLeavesNothingAndTheKeyStillWorks("INSERT INTO command_idempotency")` |
| the posting write | `…("UPDATE journals SET")` |
| the outbox insert | `…("INSERT INTO outbox_events")` |
| commit | `…InjectedFailureAtCommitLeavesNothingAndTheKeyStillWorks` |
| the reversal claim, the reversal submit, and commit | `ReversalIdempotencyTests.InjectedFailureLeavesNoReversalAndTheKeyStillWorks` |

The audit row is written by a trigger inside the posting `UPDATE`, so no boundary exists between the posting and its audit.

## Known limitations

- Claims are kept forever; there is no retention.
- The actor in the fingerprint is client-asserted until Milestone 5.
- Journals posted before this migration have no claim, and their older audit rows have no decision id or key.
- The schema owner can still bypass guards with DDL, and a superuser can disable triggers (ADR-007). The reconciliation report detects the financial effect of such a bypass.
- The reconciliation report is on demand. Scheduling, alerting and cross-system comparison are Milestone 5.
