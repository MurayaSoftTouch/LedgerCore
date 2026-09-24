# ADR-009 — Immutable Policy Versions

- Status: Accepted
- Date: 2026-09-23 (Milestone 2)

## Context

Every decision must be explainable later against the exact rules that produced it (ADR-003). If rules can be edited in place, a decision's recorded `policyVersion` would point at rules that no longer exist in that form. The approval logic is financial control logic: changing it has to be a visible, dated event.

## Decision

- **A policy** (`policies`) is a stable identity: a `key` (unique, e.g. `default-limits`), a name, and an optional `organizationId` scope (null means the default policy). It is immutable once created.
- **A policy version** (`policy_versions`) holds the rules. **Its rules are immutable from creation**, not just from activation. They're written in the same transaction that inserts the version, and never again. The trigger checks `xmin` against the current transaction.
- **Lifecycle:** `DRAFT → ACTIVE → RETIRED`, plus `DRAFT → RETIRED` to withdraw an unused draft. `RETIRED` is terminal. All three states are needed:
  - `DRAFT` separates "written" from "in force", so a version can be reviewed before it takes effect;
  - `ACTIVE` marks the one version in force;
  - `RETIRED` keeps history queryable without it being usable.

  Restoring old behaviour means creating a new version with the same rules.
- **One `ACTIVE` version per policy.** Activating a draft retires the current active version **in the same transaction**, so there is never a moment with zero or two active versions. `activated_at` and `retired_at` record the interval during which each version was in force.
- **Numbers are 1, 2, 3, … per policy.** The next number is allocated under the policy's row lock. A unique constraint on `(policy_id, version_number)` and a trigger requiring `max + 1` make gaps and duplicates impossible even outside the service.
- **Enforcement:**
  - The service locks the policy row (`FOR UPDATE`) for every version change.
  - A partial unique index `ON policy_versions (policy_id) WHERE status = 'ACTIVE'` is the backstop.
  - Triggers allow only the lifecycle transitions and freeze identity and activation evidence.
  - Versions, rules and policies can't be deleted or truncated.

## Alternatives

- **Editable drafts** (add or remove rules until activation). More convenient, but "the draft I reviewed" and "the draft that was activated" could differ. Freezing at creation means a draft is reviewed exactly as it will run. If a rule is wrong, create the next version.
- **Several simultaneously active versions with effective-date ranges.** This would allow scheduled changes. It was rejected for the MVP because it makes "which rules apply?" depend on clock ranges and overlaps, and the contract gives no trustworthy transaction time to select by (see ADR-011).
- **Reactivating a retired version.** This would muddle the activation history. A new version with copied rules is explicit and dated.

## Consequences

- A typo in a rule costs a new version number. That's intended.
- Version history is append-only and complete.
- Activation briefly serialises with in-flight evaluations of the same policy: they share-lock the active version (ADR-011).

## Known limitations

- There's no scheduled activation; activation takes effect when it commits.
- There's no four-eyes approval on activation. It's a single authorised call, and authorization is Milestone 5.
- `policy_app` owns the schema, so it could drop the triggers (see policy-engine.md, "Database isolation").
