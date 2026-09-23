# ADR-004 — Double-Entry and Immutable Posted Journals

- Status: Accepted
- Date: 2026-09-23

## Context

The ledger's credibility rests on two properties:

1. Every posted journal balances.
2. Posted history never changes.

If either can be violated, even by an operator with good intentions, reconciliation stops being meaningful.

## Decision

**Invariant 1: balance.** For every journal in `POSTED` state, within a single currency:

```text
SUM(entry.amount WHERE side = DEBIT) == SUM(entry.amount WHERE side = CREDIT)
```

with at least one debit and one credit line and every line amount strictly positive. The invariant is checked:

- when a draft is submitted (so only balanced journals ever reach the policy service); and
- again inside the database transaction that performs the `POSTED` transition. That in-transaction check is authoritative.

Milestone 1/4 will add a database-level guard (a deferred constraint trigger on posting), so the invariant holds even against code that bypasses the domain model.

**Invariant 2: immutability.** Once `POSTED`, a journal and its entries are never updated or deleted. There is no `UPDATE`/`DELETE` path in the API. The application role will be denied those privileges on posted rows (Milestone 4).

**Corrections.** A correction is a new journal:

```text
original journal (POSTED)
      ↓
reversal journal (POSTED; each line mirrors the original with the side swapped; references original)
      ↓
corrected journal (POSTED, normal lifecycle)
```

A journal can be reversed at most once. A reversal cannot itself be reversed; the fix is a new correcting journal.

**Amounts** are exact decimals (`numeric` in PostgreSQL, `decimal` in C#). Floating point is never used for money anywhere, including the wire contract, where amounts are decimal strings.

## Alternatives

- **Allow edits with an audit log.** Common in business apps. But the audit log becomes the real ledger, and the "current" table can silently diverge from it.
- **Single-entry with signed amounts.** Simpler schema, but loses the self-checking property that makes errors detectable.
- **Minor-unit integers for amounts.** Exact and fast, but every consumer needs currency exponent tables, and some currencies/instruments need more precision than two places. Decimal strings with bounded scale were chosen instead.

## Consequences

- Mistakes are permanent in history and corrected visibly. That is the intended audit property.
- Reversals double the row count for corrections; balance queries must include reversal journals (they naturally do, since reversals are ordinary posted journals).
- Multi-currency journals are out of scope for v1.

## Known limitations

- Milestone 0 only documents these invariants; nothing enforces them yet.
- Partial reversals are not supported; a partial correction is expressed as a full reversal plus a new journal.
