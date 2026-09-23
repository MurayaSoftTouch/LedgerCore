# ADR-007 — Database-Enforced Ledger Invariants and Runtime Role Separation

- Status: Accepted
- Date: 2026-09-23 (Milestone 1)

## Context

ADR-004 requires that posted journals balance and never change. Milestone 0 documented this; Milestone 1 has to enforce it. If only the C# domain model enforces the rules, then anything that reaches the database another way can break them silently: a future code path, a hand-written SQL fix, a migration, or a second service. ADR-004 said a database guard would come "in Milestone 1/4" and that privileges would be restricted "in Milestone 4". This ADR brings both forward to Milestone 1 and makes them concrete.

## Decision

**Three layers, each independently sufficient for the rules it covers:**

1. **Domain model** (`LedgerCore.Ledger.Domain`): explicit transition methods, exact double-entry validation, and immutable fields with no setters. It produces the precise error codes the API returns.
2. **PostgreSQL constraints and triggers** (migration `LedgerInvariantGuards`), which apply to every role, including the schema owner:
   - `journals_before_update` allows only the ADR-006 transitions and freezes `POSTED`/`REJECTED` rows entirely. On `PENDING_APPROVAL` and `POSTED` it re-checks balance from the persisted entries, and on `POSTED` it re-checks that the accounts are active, taking a `FOR SHARE` lock on them.
   - `journal_entries_guard` forbids any `UPDATE`, and allows `INSERT`/`DELETE` only while the journal is `DRAFT` (taking a `FOR SHARE` lock on the journal row).
   - Reversal guards: the target must be `POSTED` and not itself a reversal. The entries must exactly mirror the original (`EXCEPT ALL` in both directions). A reversal can't be committed while `DRAFT` (a deferred constraint trigger). At most one non-rejected reversal per journal (partial unique index).
   - Composite foreign keys `(account_id, ledger_id, currency)` and `(journal_id, ledger_id, currency)` make cross-ledger and cross-currency entries impossible. Check constraints enforce `amount > 0`, valid enum values, and lifecycle columns that are present exactly when the state implies them.
   - `TRUNCATE` triggers on every ledger table; update/delete guards on ledgers, accounts and the audit table.
   - `journals_audit_transition` (`SECURITY DEFINER`) appends every status change to `journal_status_transitions`.
3. **Least-privilege runtime role.** `ledger_app` owns the schema and runs migrations. The API connects as `ledger_runtime`, which has:
   - `ledgers`: SELECT, INSERT
   - `accounts`, `journals`: SELECT, INSERT, UPDATE
   - `journal_entries`: SELECT, INSERT
   - `journal_status_transitions`, `__ef_migrations_history`: SELECT
   - no DELETE, TRUNCATE, REFERENCES, TRIGGER or DDL anywhere. Because it isn't the owner, it can't disable or drop the triggers.

Violations raised by triggers use SQLSTATE `LC001`, with a message that starts with the domain error code, e.g. `JOURNAL_UNBALANCED: ...`. The API maps these to 409 and logs a warning, since reaching a database guard means either a race the domain couldn't see or a bypass.

## Alternatives

- **Application-only enforcement.** Simpler, and one place to change rules. Rejected: it can't protect history from any other writer, and "immutable" would be a convention, not a guarantee.
- **Stored procedures as the only write path** (revoke all table DML and expose `post_journal()` and similar). The strongest option, but the business logic would then live in plpgsql and the domain model would become a thin shell. Kept as a future option if more writers appear.
- **Deferred constraint trigger for balance at commit.** Would allow the unbalanced intermediate states EF Core produces. Not needed: entries are frozen before `PENDING_APPROVAL`, so a row-level check at the transition is exact. A deferred trigger is used only for "reversal not left in DRAFT", where commit time is the right moment.
- **Row-level security.** Row-level security filters and restricts rows; it isn't designed to express state transitions, and it makes errors less explicit than the trigger messages.

## Consequences

- Rules are defined twice, in C# and in SQL. The PostgreSQL integration suite (`DatabaseGuardTests`) pins the SQL side by attacking it with raw SQL as both roles.
- Migrations must run as `ledger_app`. The runtime connection string must never be the owner's.
- Every future schema change to a ledger table has to consider the triggers, and the privilege block at the end of `LedgerInvariantGuards` has to be extended for new tables. The init script's default privileges only grant SELECT/INSERT/UPDATE.

## Known limitations

- **The schema owner and superusers can still alter history** by dropping or disabling triggers (`ALTER TABLE ... DISABLE TRIGGER`), then writing. The database protects history from the application and from ordinary SQL, not from someone holding DDL rights. Tamper *evidence* (hash-chained entries, or an external append-only log) is Milestone 5 scope.
- The runtime role's privileges are applied by the migration only if `ledger_runtime` exists (it does in Compose and in the tests). A deployment that skips the init script must create it.
- The actor recorded on transitions is client-asserted (`X-Actor-Id`) until authentication exists.
