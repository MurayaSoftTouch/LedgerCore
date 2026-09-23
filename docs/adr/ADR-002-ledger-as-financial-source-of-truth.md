# ADR-002 — ASP.NET Ledger as Financial Source of Truth

- Status: Accepted
- Date: 2026-09-23

## Context

Financial state (accounts, journals, entries, balances) needs exactly one authoritative owner. If two services can each compute or store a balance, they will eventually disagree, and neither is provably correct.

## Decision

The ASP.NET Core ledger service (`services/ledger-api`) is the only component that:

- writes accounts, journals and journal entries;
- validates the double-entry invariant;
- computes balances;
- decides whether a journal becomes `POSTED`.

It persists to its own PostgreSQL database (`ledger`, owner role `ledger_app`). Other services receive derived facts through explicit contracts (the policy decision request today, outbox events later), never through the ledger's tables.

Isolation is enforced by the database, not only by convention. The local init script revokes `CONNECT` from `PUBLIC`, so `policy_app` cannot open the `ledger` database (verified in Milestone 0: `User does not have CONNECT privilege`).

## Alternatives

- **Shared database, shared schema.** Simplest to start, but any service can then mutate ledger rows and bypass posting invariants.
- **Shared database, separate schemas with cross-schema grants.** Weaker than separate databases, because a single mis-grant reopens the coupling.
- **Event-sourced ledger with balances as projections in other services.** Strong audit properties, but it moves balance authority out of a single transactional boundary and needs messaging infrastructure this project deliberately avoids.

## Consequences

- Balance queries always go to the ledger.
- The policy service must make decisions without balances (see ADR-003 and ADR-005). Any future rule that needs a balance has to be designed so that the ledger supplies a derived fact, or the ledger enforces it itself.
- The ledger's availability bounds the availability of all financial writes.

## Known limitations

- A single PostgreSQL cluster hosts both databases locally. This isolates access, not resources: a noisy policy workload can still affect ledger latency. That is acceptable for local development and CI.
- The ledger does not yet expose balances or journals (Milestone 1).
