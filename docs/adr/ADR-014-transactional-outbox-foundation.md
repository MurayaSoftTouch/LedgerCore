# ADR-014 — Transactional Outbox Foundation

- Status: Accepted
- Date: 2026-09-23 (Milestone 3)
- Reviews required: @LMichy1 (the write sits in the posting transaction)

## Context

Other systems will need to learn that a journal was posted, for example reconciliation (Milestone 5). Publishing from application code after the commit loses events if the process dies in between. Publishing before the commit announces postings that might then roll back. There is no message broker in this project, and Kafka is explicitly out of scope.

## Decision

- An `outbox_events` table in the **ledger database**: id, aggregate type and id, event type, `jsonb` payload, created-at, and a nullable published-at.
- `JournalCommands.PostAsync` inserts a **`JournalPosted`** event in the **same transaction** as the `POSTED` transition. The event exists if and only if the posting committed.
- The payload holds the journal id, ledger id, transaction type, currency, total (as a decimal string), entry count, `reversesJournalId`, the policy decision id, posted-at and posted-by. It carries no balances.
- **Guards:**
  - a unique `(aggregate_id, event_type)`, so there is one `JournalPosted` per journal;
  - a deferred constraint trigger that refuses to commit a `JournalPosted` event unless its journal is `POSTED`;
  - rows are immutable except for setting `published_at` once;
  - no delete or truncate;
  - the runtime role has `SELECT`, `INSERT`, and `UPDATE (published_at)` only.
- **There is no publisher in Milestone 3.** A future relay will read unpublished events in `created_at` order (an index exists for this), deliver them, and set `published_at`.

## Delivery semantics (when a relay exists)

Delivery is **at-least-once**, not exactly-once. A relay can deliver an event and crash before marking it published, and it will deliver it again. Consumers must deduplicate on the event `id`. Ordering is by `created_at` per ledger; there is no global ordering guarantee across relays.

## Alternatives

- **Publish directly after commit.** Loses events on a crash.
- **Change data capture** (logical decoding, Debezium). Powerful, but it needs infrastructure this project doesn't run.
- **LISTEN/NOTIFY.** Not durable: notifications sent while no listener is connected are lost.

## Consequences

- Posting writes one more row, inside the transaction that already exists.
- The outbox grows until a relay and a retention policy exist.

## Known limitations

- **No relay, no delivery and no retention yet.** Only atomic persistence is implemented and tested.
- Only `JournalPosted` is emitted. Rejections and reversals appear only through their own posting.
