# ADR-008 — Money Representation and Currency Precision

- Status: Accepted
- Date: 2026-09-23 (Milestone 1)

## Context

ADR-004 established exact decimals and no floating point. Milestone 1 has to choose the concrete types, the precision limits, the currency policy, and what happens when an amount has too many decimal places.

## Decision

- **In .NET:** `decimal`, which is base-10 and exact for every value the ledger accepts, wrapped in the `Money` value object (amount plus `Currency`). `Money` can only be constructed through `Money.Of`, which requires:
  - `amount > 0`, because direction carries the sign (ADR-004);
  - `amount ≤ 999 999 999 999 999 999.9999` (18 integer digits, 4 decimals), which is the storage range;
  - no more decimal places than the currency's ISO 4217 minor units, e.g. KES 2, UGX 0, BHD 3.
- **In PostgreSQL:** `numeric(22,4)` for entry amounts, with `CHECK (amount > 0)`. Sums use `numeric` arithmetic and are exact.
- **Over HTTP:** amounts are decimal **strings** (`"125000.50"`), the same as the policy contract (ADR-005). JSON numbers are rejected with 400. Responses format amounts with exactly the currency's minor units.
- **Rounding: none.** The ledger never rounds. An amount with excess precision (`"1.001"` in KES) is rejected with `AMOUNT_PRECISION_EXCEEDED`, not adjusted. Trailing zeros are not excess precision (`1.5000` is 1.5).
- **Equality is exact.** Balance checks compare `decimal`/`numeric` values with `==`/`<>`, never with a tolerance. One minor unit off is unbalanced.
- **Currencies:** a closed list (`KES, UGX, TZS, RWF, USD, EUR, GBP, JPY, BHD, KWD`), each with its minor units. An unknown code is rejected, not guessed at.
- **Currency policy:** each account has one currency, and each journal has one currency. Every entry must use an account in the journal's currency. This is enforced in the domain and by composite foreign keys. There is no FX and there are no multi-currency journals.

## Alternatives

- **Integer minor units (`bigint` cents).** Exact and fast. Rejected: every consumer needs the exponent table, the contract would diverge from human-readable amounts, and changing a currency's exponent later becomes a data migration.
- **Round half-even to minor units.** Common in billing. Rejected for a ledger: silent rounding hides upstream bugs, and a rounded amount is no longer the amount the caller asserted.
- **`numeric` without precision.** Unlimited range, but no storage-level bound, and it would drift from the contract's pattern.

## Consequences

- Callers must send amounts already expressed in the currency's precision.
- Adding a currency means a code change in `Currency`, which is deliberate.
- Precision per currency is enforced in the domain only. The database enforces the global `numeric(22,4)` bound and positivity, but not currency-specific scale. See the known limitation below.

## Known limitations

- A raw SQL insert could store `1.0010` in a KES entry. It would still have to balance, and it can only be inserted while the journal is `DRAFT`, but its scale wouldn't be validated. A `currencies` table with a scale check trigger would close this gap if non-application writers ever appear.
- The minor-unit table is static; ISO 4217 changes need a release.
