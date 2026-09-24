# ADR-010 — Deterministic, Structured Policy Evaluation

- Status: Accepted
- Date: 2026-09-23 (Milestone 2)

## Context

The same request, evaluated against the same policy version, must always produce the same decision, the same reason codes in the same order. Rules must be reviewable by people who don't read code. And they must never execute arbitrary logic supplied through an API.

## Decision

**Rules are data, interpreted by fixed code.** There are four types, each a Java record with a validating constructor and a matching per-type `CHECK` constraint in PostgreSQL:

| Type | Parameters | Matches when |
| --- | --- | --- |
| `AMOUNT_ABOVE` | `currency`, `threshold` (decimal, ≤ 4 dp) | request currency = `currency` **and** `totalAmount > threshold` (strict, exact `BigDecimal` comparison; there's no FX, so other currencies never match) |
| `TRANSACTION_TYPE` | `transactionTypes` (contract v1 values) | request type is in the set |
| `ACCOUNT_CONTEXT` | `accountTypes` and/or `accountIds`, optional `side` | **any** account in `accountContext` satisfies every configured criterion |
| `CURRENCY_NOT_ALLOWED` | `allowedCurrencies` | request currency is **not** in the set |

Rules only read contract-v1 fields. There are no expressions, SpEL, scripts or SQL. Adding a rule type is a code change.

**Each rule has:**
- an explicit `position` (1–100, unique per version);
- an `outcome`, which is `REVIEW_REQUIRED` or `REJECTED`;
- a `reasonCode` (the contract pattern).

**There is no "approve" rule.** Approval is the absence of any matching restriction, so adding a rule can only ever make decisions stricter.

**Algorithm** (`PolicyEvaluator`, a pure function with no clock or I/O):

1. Evaluate every rule in ascending `position`; nothing depends on row order.
2. Decision = the most restrictive matched outcome, `REJECTED > REVIEW_REQUIRED > APPROVED`. If nothing matches, the decision is `APPROVED`.
3. A `transactionType` this service doesn't know (a later 1.x value) adds `REVIEW_REQUIRED` / `TRANSACTION_TYPE_UNSUPPORTED` (ADR-005). It never masks a rejection.
4. Reason codes: rejected codes first, then review codes; within each group, service-generated codes first, then rule codes by position; duplicates removed. The list is empty only for `APPROVED`.

**Policy selection:**
- The organization's own policy applies if it has one; otherwise the single default policy (`organizationId = null`) applies.
- No applicable policy → `REVIEW_REQUIRED` / `NO_APPLICABLE_POLICY`.
- An applicable policy with no `ACTIVE` version → `REVIEW_REQUIRED` / `NO_ACTIVE_POLICY_VERSION`. It **does not fall back to the default policy**, because a paused organization policy must never become the (possibly looser) default.

Both cases return `policyVersion: "none"` (contract 1.0.1).

## Alternatives

- **A rules engine or DSL** (Drools, SpEL, CEL). Expressive, but it turns policy configuration into code execution, makes validation and review harder, and brings a runtime this project doesn't need.
- **First-match-wins ordering.** Simple to explain, but a misordered list could let an approving rule shadow a rejecting one. Most-restrictive-wins makes rule order irrelevant to the outcome; order only affects reporting.
- **Scores or weights.** Opaque, and there's no business requirement for them.
- **Allow-rules.** They would reintroduce precedence problems for no gain: the default is already approval.

## Consequences

- Decisions are explainable from the matched rules alone, since every matched rule is reported.
- A 20-repetition shuffled-order test pins determinism.
- Expressiveness is deliberately limited. "Amount above X for type Y" can't be expressed as a single rule; two rules plus most-restrictive semantics usually suffice, and anything else is a new rule type.

## Known limitations

- There are no FX-normalised thresholds (one `AMOUNT_ABOVE` rule per currency).
- There are no velocity or cumulative rules: they would need state the policy service doesn't own.
