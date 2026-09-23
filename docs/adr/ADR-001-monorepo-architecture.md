# ADR-001 — Monorepo Architecture

- Status: Accepted
- Date: 2026-09-23

## Context

LedgerCore has two services in different ecosystems (.NET and Java) joined by one contract. The riskiest change in the system is a contract change: if the ledger and policy service disagree about a field, the ledger must fail closed, which in production means transactions stop. Three contributors each own a different side of that boundary.

## Decision

One repository holds both services, the contract (`contracts/`), cross-service tests (`tests/`) and local infrastructure (`infra/`, `docker-compose.yml`).

- A contract change and the code on both sides that adopts it land in one pull request, reviewed by owners of both sides (enforced via `CODEOWNERS`).
- Each service keeps its own native build (`dotnet`, Maven wrapper). There is no umbrella build tool; CI runs each toolchain in its own parallel job.
- Services do not share source code. The only shared artefact is the contract in `contracts/`.

## Alternatives

- **Repository per service plus a contract repository.** Gives independent release cadence, but a contract change needs three coordinated PRs and a published artefact before either side can test it. That overhead buys nothing at a team size of three.
- **Monorepo with a single build tool (Bazel, Nx).** Unified caching and affected-target detection, but adds a third toolchain every contributor must learn, for two services.

## Consequences

- Contract drift is visible in a single diff.
- Every push builds both services. At Milestone 0 sizes that takes a few minutes; add path filters once build times justify the added complexity.
- Repository-wide history mixes both services; Conventional Commit scopes (`ledger`, `policy`, `contracts`, `infra`, `ci`) keep it navigable.

## Known limitations

- The services cannot be versioned or released independently without extra tagging conventions; not needed until there is a deployment target.
- Git access control is repository-wide: `CODEOWNERS` enforces review, not write isolation.
