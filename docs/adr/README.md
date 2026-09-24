# Architecture Decision Records

Each ADR records one decision: the context that forced it, what was chosen, what was rejected, and what it costs us. ADRs are immutable once `Accepted`; a changed decision gets a new ADR that supersedes the old one.

| ADR | Title | Status |
| --- | --- | --- |
| [ADR-001](ADR-001-monorepo-architecture.md) | Monorepo architecture | Accepted |
| [ADR-002](ADR-002-ledger-as-financial-source-of-truth.md) | ASP.NET ledger as financial source of truth | Accepted |
| [ADR-003](ADR-003-dedicated-policy-service.md) | Dedicated Spring Boot policy service | Accepted |
| [ADR-004](ADR-004-double-entry-immutable-posted-journals.md) | Double-entry and immutable posted journals | Accepted |
| [ADR-005](ADR-005-versioned-ledger-policy-contract.md) | Versioned ledger-policy contract | Accepted |
| [ADR-006](ADR-006-journal-lifecycle.md) | Journal lifecycle | Accepted |
| [ADR-007](ADR-007-database-enforced-ledger-invariants.md) | Database-enforced ledger invariants and runtime role separation | Accepted |
| [ADR-008](ADR-008-money-representation.md) | Money representation and currency precision | Accepted |
| [ADR-009](ADR-009-immutable-policy-versions.md) | Immutable policy versions | Accepted |
| [ADR-010](ADR-010-deterministic-policy-evaluation.md) | Deterministic, structured policy evaluation | Accepted |
| [ADR-011](ADR-011-decision-persistence-and-replay.md) | Policy decision persistence and replay | Accepted |
| [ADR-012](ADR-012-ledger-policy-reliability-contract.md) | Ledger–policy reliability contract | Accepted |
| [ADR-013](ADR-013-service-to-service-authentication.md) | Service-to-service authentication | Accepted |
| [ADR-014](ADR-014-transactional-outbox-foundation.md) | Transactional outbox foundation | Accepted |

New ADRs: copy the section headings of an existing one (Context, Decision, Alternatives, Consequences, Known limitations) and take the next number.
