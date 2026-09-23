# ADR-003 — Dedicated Spring Boot Policy Service

- Status: Accepted
- Date: 2026-09-23

## Context

Approval rules (amount thresholds, transaction-type and account restrictions, manual-review triggers) change far more often than posting mechanics, and they are owned by a different contributor. Every approval must also be traceable to the exact rule set that produced it.

## Decision

A separate Spring Boot service (`services/policy-service`, Java 21, Spring Boot 4.1) owns:

- policy definitions and immutable, numbered policy versions;
- evaluation of a `PolicyDecisionRequest` into `APPROVED`, `REJECTED` or `REVIEW_REQUIRED`, with reason codes;
- a record of every decision, identified by `decisionId` and `policyVersion`.

It persists to its own database (`policy`, owner `policy_app`) and holds no ledger balances or journal entries.

**Build tool: Maven** (via the checked-in Maven wrapper, `mvnw`), because:

- the wrapper pins the Maven version, so neither CI nor contributors depend on the Ubuntu-packaged Maven 3.6.3 on the dev machine;
- a declarative POM, with no build logic, suits a small service and keeps the Java build readable for the .NET-focused contributors who will review it;
- Gradle is not installed locally, and its main advantages (build caching, custom build logic) don't matter at this size.

## Alternatives

- **Rules inside the ledger.** One fewer network hop and no fail-closed path to design. But policy changes would redeploy the financial core, and rule authorship would couple to ledger internals.
- **A rules engine (Drools etc.) embedded in either service.** Powerful, but the planned rules are thresholds and allow/deny lists, which don't justify a DSL runtime.
- **Gradle.** Viable; rejected for the reasons above, not on technical merit.

## Consequences

- Every approval now crosses a network boundary. The ledger must handle timeouts and failures explicitly and fail closed (ADR-005).
- Policy versions are immutable once used, so a past decision can always be explained against the rules that were live at the time.
- Two runtimes to operate, patch and monitor.

## Known limitations

- Milestone 0 contains no policy logic; the service exposes health, info and an empty OpenAPI document only.
- Rules that need balances cannot be expressed without the ledger supplying derived facts (ADR-002).
