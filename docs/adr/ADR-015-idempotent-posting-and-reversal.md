# ADR-015 — Idempotent Posting and Reversal

- Status: Accepted
- Date: 2026-09-24 (Milestone 4, LMichy1)
- Reviews required: @Ngetich-86 (API change used by `tests/integration` and `compose-smoke.sh`; outbox binding)

## Context

A client that sends `post` and loses the response can't tell whether the journal was posted. Before Milestone 4, a retry got `409 JOURNAL_ALREADY_POSTED`. That was safe, because posting is a single locked transition (ADR-004, ADR-006), but it was ambiguous: the retry could not tell "you posted it" from "someone else posted it". `reverse` had the same problem with `409 JOURNAL_ALREADY_REVERSED`.

The initial backlog (L4) asked for posting "with an idempotency key". Milestone 3 made the policy side idempotent on `transactionId` (ADR-011); the ledger's own commands were not.

## Decision

**A required `Idempotency-Key` header on `post` and `reverse`.**
- 8–128 characters from `A–Z a–z 0–9 . _ : ~ -`. A UUID is recommended.
- A missing key is `400 IDEMPOTENCY_KEY_REQUIRED`; a malformed or oversized key is `400 IDEMPOTENCY_KEY_INVALID`.
- Other commands don't take a key:
  - `submit` and `request-approval` are already idempotent by state;
  - journal creation uses `externalReference`.

**Claims: a `command_idempotency` table in the ledger database.**
- Each claim records the ledger, operation (`POST_JOURNAL` or `REVERSE_JOURNAL`), key, request fingerprint, target journal, result journal, requester, correlation id and created-at.
- The primary key `(ledger_id, operation, idempotency_key)` scopes a key to one ledger and one operation.

**The fingerprint is SHA-256 over a versioned, JSON-encoded tuple.**
- Posting: operation, ledger, journal, actor.
- Reversal: operation, ledger, original journal, actor, requested description.
- The correlation id and all other transport metadata are excluded, so a genuine retry always matches.

**The protocol runs in one transaction.**
1. Lock the target journal (`FOR UPDATE`). Requests for the same journal now run one at a time.
2. If a claim for the key is committed:
   - with the same fingerprint → **replay**: return the result journal as persisted now, write nothing, and add `Idempotency-Replayed: true`;
   - with a different fingerprint → `409 IDEMPOTENCY_CONFLICT`.
3. Validate, and for a reversal, insert the reversal draft.
4. Claim the key with `INSERT … ON CONFLICT ON CONSTRAINT pk_command_idempotency DO NOTHING`. The primary key is the only authority.
   - A concurrent claim of the same key blocks here.
   - If that claim commits, this transaction rolls back and runs once more, which then takes step 2.
5. Perform the effect: posting plus `JournalPosted`, or submitting the reversal. Then commit.

**There is no in-progress state.** The claim commits or rolls back together with the effect. A failed attempt leaves nothing behind and never consumes the key, so no expiry or recovery job is needed.

**The first response is read back from the database.** The response to the original request therefore equals every replay; timestamps are at PostgreSQL's microsecond precision.

**Database guards** (migration `PostingIdempotency`):
- Claims:
  - append-only, with no update, delete or truncate, and `SELECT, INSERT` only for `ledger_runtime`;
  - their journals must belong to their ledger;
  - a deferred check requires each claim to describe an effect that happened by commit.
- A `POSTED` transition requires a posting claim **and** `APPROVED` evidence for the same journal. There is at most one posting claim per journal.
- Every reversal journal requires exactly one reversal claim for its original.
- The `JournalPosted` event must name the journal's `APPROVED` decision id.
- `journal_status_transitions` gains:
  - `policy_decision_id`, filled for `APPROVED`, `REJECTED` and `POSTED`;
  - `idempotency_key`, filled for `POSTED` and for a reversal's `PENDING_APPROVAL`.

**Posting never calls the policy service.** It relies only on the persisted, immutable evidence. Once a journal is `APPROVED`, posting works while the policy service is down.

## Alternatives

- **Optional key.** This keeps old clients working, but a keyless retry is still ambiguous, and the database could not require a claim for every posting. It was rejected: at this stage every caller is ours to update.
- **A durable `IN_PROGRESS` claim written before the effect**, in its own transaction. This would allow long-running commands, but it needs expiry and recovery rules, and a crash would leave keys unusable until they expire. Our commands are short and single-transaction, so it was rejected.
- **Storing the response body** and replaying the stored bytes. It was rejected in favour of rebuilding the replay from authoritative persisted state:
  - the posted journal is immutable, so the replay is stable;
  - a replayed reversal shows its current lifecycle state, which is what a retrying client needs.
- **A key scope per journal instead of per ledger.** That would let one key be reused across journals and hide client bugs, so it was rejected: reusing a key for another journal is a conflict.
- **Select-then-insert without a unique constraint.** This races, so it was rejected. The primary key decides.

## Consequences

- `post` and `reverse` are breaking API changes: callers must send the header. `tests/integration` and `compose-smoke.sh` were updated.
- Concurrent identical requests produce one effect. The requests that waited get replays, not errors.
- Concurrent requests with different keys still serialize on the journal lock, and the posting state machine refuses the second posting.
- An SQL write that bypasses the application can't post or reverse without a matching claim, unless it runs as a superuser with triggers disabled.
- Journals posted before this migration have no claim. Their audit rows have no key or decision id, because the old transitions aren't rewritten.

## Known limitations

- Claims are kept forever; there is no retention or expiry.
- The actor is client-asserted until end-user authentication exists (Milestone 5), so the fingerprint's actor field is only as trustworthy as that header.
- A replayed reversal returns the reversal's *current* state, not a byte-for-byte copy of the first response.
