# Contributor Ownership

Ownership means accountability for a design and for reviewing changes to it. It does not mean exclusivity: anyone may contribute anywhere, and cross-review is expected. `CODEOWNERS` requests review from the primary owner automatically.

## Contributors

| GitHub | Primary areas |
| --- | --- |
| [@LMichy1](https://github.com/LMichy1) | Ledger domain: accounts, journals, entries, posting, double-entry validation, reversals, ledger persistence, reconciliation rules |
| [@MurayaSoftTouch](https://github.com/MurayaSoftTouch) | Policy service: policy definitions and versions, thresholds, decisions, policy API and persistence, Java testing |
| [@Ngetich-86](https://github.com/Ngetich-86) | Integration: contracts, contract testing, Docker Compose, PostgreSQL infrastructure, Testcontainers, outbox and reconciliation infrastructure, GitHub Actions, observability |

## Path ownership (mirrors `.github/CODEOWNERS`)

| Path | Owners |
| --- | --- |
| `services/ledger-api/` | @LMichy1 |
| `services/policy-service/` | @MurayaSoftTouch |
| `contracts/` | @Ngetich-86 @MurayaSoftTouch @LMichy1 (both sides of the contract plus the contract owner) |
| `tests/` | @Ngetich-86 |
| `infra/`, `docker-compose.yml` | @Ngetich-86 |
| `.github/` | @Ngetich-86 |
| `docs/adr/` | all three |

## Milestones

| # | Milestone | Planned primary | Supporting | Status |
| --- | --- | --- | --- | --- |
| 0 | Repository foundation | @MurayaSoftTouch (planned) → **@LMichy1 (actual)** | — | Complete locally |
| 1 | Ledger domain foundation | @LMichy1 | — | Next |
| 2 | Policy service | @MurayaSoftTouch | — | Planned |
| 3 | Cross-service reliability | @Ngetich-86 | — | Planned |
| 4 | Posting, reversal and idempotency | @LMichy1 | @Ngetich-86 | Planned |
| 5 | Reconciliation, observability and security | @Ngetich-86 | @MurayaSoftTouch | Planned |
| 6 | Final release | all | — | Planned |

**Record of deviation.** The plan assigned Milestone 0 to MurayaSoftTouch, but LMichy1 did the work. Commits are authored as LMichy1 so that history reflects who actually did it. Because Milestone 0 touches every owner's area, each owner should review their own paths before it merges: @MurayaSoftTouch for `services/policy-service/`, @Ngetich-86 for `contracts/`, `infra/` and `.github/`.

### Scope per milestone

- **M0.** Monorepo, collaboration config, both service scaffolds (health, OpenAPI, validated config, tests), contract v1 skeleton, PostgreSQL Compose, base CI, ADRs, backlog.
- **M1.** Accounts, journals, journal entries, PostgreSQL mappings and migrations, double-entry invariants, posting model.
- **M2.** Policy definitions, versions, decision engine, `POST /v1/policy-decisions`, Spring integration tests.
- **M3.** Ledger policy client, contract tests, fail-closed handling, outbox, integration tests, Compose improvements (containerised services).
- **M4.** Posting transaction, immutability enforcement, reversal workflow, idempotency, concurrency.
- **M5.** Reconciliation, structured logs and correlation IDs end to end, readiness with dependencies, security review, failure simulations.
- **M6.** Full integration verification, documentation, CI hardening, release.

## Review expectations

- Every PR is reviewed by a real contributor other than its author, using their own GitHub account.
- A contract change needs approval from the owners of both sides.
- Nobody reviews, approves or commits on behalf of someone else, even if the credentials happen to be on the same machine.
