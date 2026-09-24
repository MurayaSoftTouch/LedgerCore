# Ledger Domain (Milestone 1)

This document describes the ASP.NET ledger service's financial model as implemented. Decisions are recorded in [ADR-004](../adr/ADR-004-double-entry-immutable-posted-journals.md) (double entry and immutability), [ADR-006](../adr/ADR-006-journal-lifecycle.md) (lifecycle), [ADR-007](../adr/ADR-007-database-enforced-ledger-invariants.md) (database enforcement) and [ADR-008](../adr/ADR-008-money-representation.md) (money).

Code map:

| Concern | Location |
| --- | --- |
| Domain model (no EF or ASP.NET dependencies) | `services/ledger-api/src/LedgerCore.Ledger.Domain/` |
| Transactions and locking | `src/LedgerCore.Ledger.Api/Application/` |
| EF Core mapping and migrations | `src/LedgerCore.Ledger.Api/Persistence/` |
| Database guards (triggers and privileges) | `Persistence/Migrations/*_LedgerInvariantGuards.cs` |
| HTTP API | `src/LedgerCore.Ledger.Api/Endpoints/` |

## Scope: ledgers, not tenants

There is no organization or tenant model yet. A **ledger** (`ledgers`: id, unique code, name) is the explicit scope. Every account and journal belongs to exactly one ledger, and account codes are unique within it. When organizations arrive, they will own ledgers; nothing here pretends to be multi-tenant.

## Accounts

| Field | Notes |
| --- | --- |
| `id` | UUIDv7 |
| `ledger_id` | owning ledger |
| `code` | 1–32 characters: letters, digits, `.`, `_`, `-`; unique per ledger (`ux_accounts_ledger_code`) |
| `name` | 1–200 characters |
| `type` | `ASSET`, `LIABILITY`, `EQUITY`, `REVENUE`, `EXPENSE` (check constraint) |
| `currency` | one supported ISO 4217 code |
| `is_active`, `deactivated_at` | consistent by check constraint |
| `created_at` | |

- There is no hierarchy.
- Identity, name, type and currency never change (trigger `accounts_guard`).
- Deactivation is one-way: `POST .../accounts/{id}/deactivate`. An inactive account can't be reactivated.
- **Accounts are never deleted.** Journal entries reference them with `ON DELETE RESTRICT`, the runtime role has no `DELETE`, and a trigger rejects deletes even by the owner.
- An inactive account can't receive new entries or be posted to. Posted history that references it stays fully readable.

## Money

The .NET type is `decimal` inside the `Money` value object; PostgreSQL stores `numeric(22,4)`. Amounts are strictly positive, with at most 18 integer digits and at most the currency's minor units (KES 2, UGX 0, BHD 3, …). **The ledger never rounds:** excess precision is rejected. Over HTTP, amounts are decimal strings. Each account has one currency, and each journal has one currency that all its entries share. There is no FX. Details are in ADR-008.

## Journals and entries

A **journal** has:
- ledger and currency;
- description (1–500 characters);
- an optional `external_reference` that is unique per ledger (`ux_journals_ledger_external_reference`), so a retried create can't duplicate a journal;
- `status`;
- an actor and timestamp for each lifecycle step (`created_*`, `submitted_*`, `approved_*`, `rejected_*` + `rejection_reason`, `posted_*`);
- `reverses_journal_id` (reversals only).

Check constraints require every lifecycle column to be present exactly when the status implies it. No columns were added for future policy integration; Milestone 2/3 will add decision references with their own migration.

An **entry** has:
- `line_number` (1-based, unique per journal);
- `account_id`;
- `direction` (`DEBIT`/`CREDIT`);
- `amount` (> 0);
- an optional `memo`.

Direction carries the sign; amounts are never negative. Entries also store `ledger_id` and `currency`, so two composite foreign keys can enforce that every line's account and journal share the same ledger and currency:

```text
journal_entries (journal_id, ledger_id, currency) → journals (id, ledger_id, currency)
journal_entries (account_id, ledger_id, currency) → accounts (id, ledger_id, currency)
```

A journal holds 2–100 entries (the upper bound matches the policy contract's `accountContext`). The same account may appear on several lines.

## The double-entry invariant

For a journal to leave `DRAFT`, and again to become `POSTED`:

```text
count(DEBIT) ≥ 1  ∧  count(CREDIT) ≥ 1  ∧  SUM(DEBIT amounts) = SUM(CREDIT amounts)   (exact)
```

It is checked in two independent places:

1. `DoubleEntry.EnsureBalanced` in the domain, on `Submit` and on `Post`. Journal totals are bounded by the storage range.
2. `ledger_assert_balanced()` in PostgreSQL, called by the `journals_before_update` trigger on every transition to `PENDING_APPROVAL` or `POSTED`. It sums the persisted rows, not anything the client sent.

## Lifecycle

```text
DRAFT ──submit──▶ PENDING_APPROVAL ──approve──▶ APPROVED ──post──▶ POSTED
                          │
                          └──reject──▶ REJECTED
```

- The stored states are `DRAFT`, `PENDING_APPROVAL`, `APPROVED`, `REJECTED` and `POSTED`. **`REVERSED` is not stored.** The API derives `isReversed` and `reversedByJournalId` from the existence of a *posted* reversal.
- Policy timeouts and failures are **not** rejection; they leave the journal in `PENDING_APPROVAL` (ADR-006). The policy client is Milestone 3.
- Every edge is enforced by a domain method **and** by the `journals_before_update` trigger. There is no way to set `status` directly: the API has no PATCH, PUT or DELETE for journals.
- Every transition is appended to `journal_status_transitions` **by a database trigger**, with the actor and timestamp from the row. The runtime role can only read that table.

**Approval in Milestone 1 is not a production workflow.** Nothing reaches `APPROVED` through the public API; there is no approve endpoint. Tests use `TestApprovalRecorder`, which is internal, not registered in dependency injection, and unreachable over HTTP. Milestone 2/3 replaces it with a recorded policy decision.

## Posting transaction

`JournalCommands.PostAsync` runs in one PostgreSQL transaction (READ COMMITTED):

1. `SELECT … FROM journals WHERE id = $1 AND ledger_id = $2 FOR UPDATE`: lock first, then read.
2. Load the journal and its **persisted** entries. Client totals are never used.
3. `SELECT … FROM accounts WHERE id = ANY($1) ORDER BY id FOR SHARE`, then load the accounts. A concurrent deactivation waits for this transaction.
4. The domain's `Journal.Post` checks: status is `APPROVED` (`JOURNAL_ALREADY_POSTED` if already posted), the journal is balanced, and every account exists, is active, and is in the same ledger and currency.
5. `UPDATE journals SET status = 'POSTED', posted_at, posted_by`. The trigger re-validates the transition, the balance and account activity, and writes the audit row.
6. Commit. Any exception before commit rolls back everything, including the audit row.

Posting twice can't create two financial events: posting is a single state transition, and a `POSTED` row can't change.

## Concurrency

| Race | Mechanism | Tested by |
| --- | --- | --- |
| N concurrent posts of one journal | `FOR UPDATE` serialises them; later ones see `POSTED` → `JOURNAL_ALREADY_POSTED`. Trigger backstop: `POSTED → POSTED` is not a legal transition. | `PostingTests.ConcurrentPostingAttemptsProduceExactlyOnePosting` (8 connections) |
| A second poster during an in-flight post | It blocks on the row lock and doesn't read a stale `APPROVED` row. | `PostingTests.SecondPosterBlocksOnRowLockThenObservesPostedState`, which pauses A before COMMIT and observes B waiting in `pg_locks` |
| Deactivating an account during a post | The posting takes `FOR SHARE` on accounts; the deactivation's row update waits. | `PostingTests.DeactivationWaitsForInFlightPosting` |
| Adding an entry while the journal is being submitted or posted | Every journal command locks the journal row first; the entry trigger takes `FOR SHARE` on the journal and re-checks `DRAFT`. | trigger design (`journal_entries_guard`) |
| N concurrent reversals of one journal | `FOR UPDATE` on the original plus an existence check; backstop: the partial unique index `ux_journals_live_reversal`. | `ReversalTests.ConcurrentReversalRequestsCreateExactlyOneReversal` (8 connections) |

Tests synchronise on events (task gates, a commit-pausing EF interceptor, polling `pg_locks` for a waiter). They don't use fixed sleeps.

## Immutability: what actually protects posted history

| Attempt | Domain / API | Runtime role (`ledger_runtime`) | Trigger (applies to the owner too) |
| --- | --- | --- | --- |
| Change an entry's amount, account or direction | no such operation | no `UPDATE` privilege on `journal_entries` | `JOURNAL_ENTRY_IMMUTABLE` |
| Add or remove entries after `DRAFT` | `JOURNAL_INVALID_STATE` | no `DELETE` privilege | `JOURNAL_ENTRY_IMMUTABLE` |
| Change a posted journal's description, currency, reference, timestamps or status | no such operation | has `UPDATE` (needed for transitions) | `JOURNAL_IMMUTABLE` |
| Delete a posted journal | no such operation | no `DELETE` privilege | `JOURNAL_IMMUTABLE` |
| `TRUNCATE` any ledger table | — | no privilege | `LEDGER_HISTORY_IMMUTABLE` |
| Rewrite or delete audit rows | — | SELECT only | `AUDIT_IMMUTABLE` |
| Disable or drop triggers, run DDL | — | not the owner, so denied | — |
| Delete or retype an account | no such operation | no `DELETE`; `UPDATE` blocked by trigger | `ACCOUNT_DELETE_FORBIDDEN` / `ACCOUNT_IMMUTABLE` |

Every row of this table is exercised with raw SQL in `DatabaseGuardTests`.

**Not protected:** a role with DDL rights on the schema (`ledger_app`, or a superuser) can disable the triggers and then rewrite history. The database guarantees immutability against the application and against ordinary SQL, not against the schema owner. Tamper evidence is Milestone 5 scope (ADR-007).

## Reversals

`POST .../journals/{id}/reverse` (`JournalCommands.ReverseAsync`) runs in one transaction:

1. Lock the original `FOR UPDATE` and load it.
2. Refuse unless the original is `POSTED` and isn't itself a reversal.
3. Refuse if a non-rejected reversal already exists (`JOURNAL_ALREADY_REVERSED`).
4. Share-lock the accounts and require them to be active.
5. `Journal.CreateReversal` builds a new journal (new id and timestamps, `reverses_journal_id` = original) whose entries mirror the original line by line with `DEBIT ↔ CREDIT` swapped. The draft is **sealed**: no entries can be added.
6. Insert it as `DRAFT`, then submit it to `PENDING_APPROVAL` **in the same transaction**, then commit. No other session ever sees the draft. A deferred constraint trigger refuses to commit a reversal still in `DRAFT`.
7. The original row isn't written at all. `ReversalTests.OriginalRowIsUnchangedByReversal` compares its full JSON before and after.

The reversal then goes through approval and posting like any other journal, under the same invariants. When it becomes `POSTED`, the original reports `isReversed = true`. Database guarantees:
- the reversal is an exact mirror (`ledger_assert_reversal_mirrors`, a multiset comparison in both directions);
- there is at most one non-rejected reversal per journal;
- a reversal can't be reversed.

A rejected reversal doesn't block a new one.

## Balances

There is no balance table. Balances are derived from `POSTED` entries only. `LedgerReconstructionTests` rebuilds them with plain SQL, ignoring draft, pending, approved and rejected noise, and asserts:
- every posted journal balances;
- ledger-wide debits equal credits;
- net movements sum to zero and match the expected balances;
- a reversal returns the affected accounts to their pre-mistake movement.

## HTTP API (Milestone 1)

All journal commands require an `X-Actor-Id` header. It is **unverified and client-asserted** until authentication exists. Errors are RFC 9457 problem details with a stable `code`:
- 400: invalid input;
- 404: not found;
- 409: wrong state, conflict, or a database guard;
- 422: invariant violation (unbalanced, inactive account, currency mismatch).

| Method and path | Purpose |
| --- | --- |
| `POST /api/v1/ledgers` · `GET /api/v1/ledgers/{ledgerId}` | Create or read a ledger |
| `POST /api/v1/ledgers/{ledgerId}/accounts` · `GET …/accounts` · `GET …/accounts/{accountId}` | Open, list or read accounts |
| `POST …/accounts/{accountId}/deactivate` | Deactivate (one-way) |
| `POST /api/v1/ledgers/{ledgerId}/journals` · `GET …/journals/{journalId}` | Create a draft; read a journal with entries, totals and derived reversal state |
| `POST …/journals/{journalId}/entries` | Add an entry (draft only) |
| `POST …/journals/{journalId}/submit` | `DRAFT → PENDING_APPROVAL` |
| `POST …/journals/{journalId}/post` | `APPROVED → POSTED` |
| `POST …/journals/{journalId}/reverse` | Create the reversal (`PENDING_APPROVAL`) |

## Known limitations

- There is no production approval path until policy integration (Milestone 2/3), and no authentication; the actor is client-asserted.
- The schema owner or a superuser can bypass the triggers (see above).
- Currency-specific precision is enforced in the domain, not in SQL (ADR-008).
- There is no draft deletion, no entry removal from drafts, and no journal listing or search endpoint.
- Idempotency covers journal *creation* (`externalReference`) and the posting transition itself. Idempotency keys on commands and the outbox are Milestone 4 and Milestone 3 scope.
- `JsonStringEnumConverter` accepts enum names case-insensitively (`"asset"` is `ASSET`); output is always upper case.
