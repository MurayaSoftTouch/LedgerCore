# Milestone 1 — Ledger Domain Foundation

- Owner: @LMichy1
- Branch: `feat/m1-ledger-domain`
- Reviewers needed before merge: @Ngetich-86 for `infra/` and the CI changes; any second contributor for `services/ledger-api/`
- Not GitHub issues: these are local work items. GitHub assigns issue numbers when someone with write access creates the issues.

Out of scope: policy engine, cross-service HTTP, outbox, reconciliation subsystem, authentication.

Shared definition of done for every item: the acceptance criteria are covered by automated tests; `dotnet build` (warnings as errors), `dotnet test` and `dotnet format --verify-no-changes` pass; docs are updated; the change is committed as LMichy1.

---

## M1-01 Account model

- **Problem.** Journals need a chart of accounts to post against, scoped to something explicit.
- **Scope.** A `Ledger` (explicit scope; no pretend multi-tenancy) and an `Account` within a ledger: code, name, type (`ASSET`/`LIABILITY`/`EQUITY`/`REVENUE`/`EXPENSE`), currency, active flag, timestamps. Deactivation. No hierarchy, no deletion.
- **Acceptance criteria.**
  - Codes are unique per ledger (DB unique constraint).
  - An unknown type is rejected with 400.
  - An inactive account can't be posted to, but history referencing it stays readable.
  - Accounts can't be deleted: FK `RESTRICT`, and the runtime role has no `DELETE`.
  - Identity fields are immutable (trigger).
- **Dependencies.** ADR-002.
- **Tests.** Domain unit tests; API tests (create, duplicate code, invalid type); posting to an inactive account against PostgreSQL.
- **Status.** Done (committed locally; not pushed or reviewed).

## M1-02 Journal and journal-entry model

- **Problem.** The double-entry invariant needs a persistent journal with explicit-direction entries.
- **Scope.** `Journal` (ledger, currency, description, optional external reference, status, actor and timestamp per transition, `reverses_journal_id`) and `JournalEntry` (account, `DEBIT`/`CREDIT`, positive amount, memo, line number).
- **Acceptance criteria.**
  - `amount > 0` in both the domain and a DB check.
  - Entries must use accounts of the same ledger and currency (composite FKs).
  - The external reference is unique per ledger.
  - No speculative columns.
- **Dependencies.** M1-01.
- **Tests.** Domain tests for valid draft, invalid amount, invalid or foreign account, currency mismatch.
- **Status.** Done (committed locally; not pushed or reviewed).

## M1-03 Double-entry validation

- **Problem.** An unbalanced journal must never reach `POSTED`.
- **Scope.** A domain validator (≥1 debit, ≥1 credit, exact `SUM(debits) = SUM(credits)`) applied on submit and on post, plus a PostgreSQL trigger that re-checks it on `PENDING_APPROVAL` and `POSTED` from persisted rows.
- **Acceptance criteria.**
  - Exact decimal equality, with no tolerance.
  - The trigger rejects an unbalanced post even when the application is bypassed.
- **Dependencies.** M1-02.
- **Tests.** Cases for one-cent imbalance, multi-entry, large amounts, reordered entries, duplicate account, many debits with one credit, one debit with many credits, and a raw-SQL bypass attempt.
- **Status.** Done (committed locally; not pushed or reviewed).

## M1-04 Journal lifecycle

- **Problem.** ADR-006 was only Proposed.
- **Scope.**
  - Accept ADR-006.
  - Enforce transitions in the domain and in a DB trigger.
  - Audit every transition in `journal_status_transitions`.
  - Provide an internal, test-only approval mechanism; no approve endpoint.
- **Acceptance criteria.**
  - Only ADR-006 edges are possible.
  - `REVERSED` is never stored.
  - Timeouts and failures aren't modelled as rejection.
- **Dependencies.** M1-02.
- **Tests.** A transition matrix in the domain; illegal transitions rejected by the DB trigger.
- **Status.** Done (committed locally; not pushed or reviewed).

## M1-05 Posting transaction

- **Problem.** Posting must be atomic, authoritative and safe under concurrency.
- **Scope.** A single PostgreSQL transaction that:
  - locks the journal (`FOR UPDATE`);
  - re-reads the persisted entries;
  - locks the referenced accounts (`FOR SHARE`) and checks they're active;
  - validates, transitions to `POSTED`, and has the trigger write the audit row.
- **Acceptance criteria.**
  - Client totals are never trusted.
  - Posting twice fails with 409 and creates no second event.
  - N concurrent posts produce exactly one success.
  - A failure injected before commit leaves no partial state.
- **Dependencies.** M1-03, M1-04.
- **Tests.** Real concurrent connections; commit-time fault injection.
- **Status.** Done (committed locally; not pushed or reviewed).

## M1-06 Reversal model

- **Problem.** Corrections must never mutate posted history.
- **Scope.** A reverse command that atomically creates a new journal mirroring the original with directions swapped. The new journal references the original, enters `PENDING_APPROVAL`, and posts under the same invariants.
- **Acceptance criteria.**
  - Only `POSTED`, non-reversal journals can be reversed.
  - At most one non-rejected reversal (partial unique index).
  - Concurrent requests create exactly one reversal.
  - The original is byte-for-byte unchanged.
  - The DB verifies the mirror property.
- **Dependencies.** M1-05.
- **Tests.** Successful reversal, exact-opposite entries, original unchanged, double reversal rejected, concurrent reversal.
- **Status.** Done (committed locally; not pushed or reviewed).

## M1-07 Persistence and migrations

- **Problem.** The domain needs an authoritative PostgreSQL schema.
- **Scope.**
  - EF Core (Npgsql) mapping.
  - Reviewed migrations with PKs, FKs, unique and check constraints, indexes and triggers.
  - A separate runtime role (`ledger_runtime`) with DML only, so triggers can't be disabled by the app.
  - No automatic schema sync at startup.
- **Acceptance criteria.**
  - Migrations apply cleanly to an empty database.
  - The generated SQL has been reviewed.
  - The app connects as `ledger_runtime`.
- **Dependencies.** M1-01 to M1-06.
- **Tests.** The integration fixture applies the migrations as the owner role and runs everything as the runtime role.
- **Status.** Done (committed locally; not pushed or reviewed).

## M1-08 Integration testing

- **Problem.** Database behaviour (locks, constraints, triggers) can't be proven with mocks.
- **Scope.** Testcontainers PostgreSQL 18.6 using the real init script; API, posting, immutability, reversal, concurrency and reconstruction suites.
- **Acceptance criteria.**
  - Every database-relevant case in the M1 brief runs against real PostgreSQL.
  - The ledger reconstruction proof passes.
- **Dependencies.** M1-07.
- **Tests.** This item is the tests.
- **Status.** Done (committed locally; not pushed or reviewed).

## M1-09 Documentation and verification

- **Problem.** The decisions and guarantees must be written down precisely, including what is *not* guaranteed.
- **Scope.** `docs/architecture/ledger-domain.md`, ADR-006 accepted, new ADRs for new decisions, and the README updated for implemented features only; full quality gates run.
- **Acceptance criteria.**
  - The docs match the code.
  - Protections are attributed correctly (domain vs DB trigger vs DB privilege).
- **Dependencies.** All of the above.
- **Tests.** Quality gates: `dotnet restore`, `build`, `test`, `format --verify-no-changes`, Spring `verify`, `contracts/validate.sh`.
- **Status.** Done (committed locally; not pushed or reviewed).
