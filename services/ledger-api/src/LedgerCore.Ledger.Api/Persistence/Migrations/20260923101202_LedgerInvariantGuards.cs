using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LedgerCore.Ledger.Api.Persistence.Migrations
{
    /// <summary>
    /// Hand-written database guards for the ledger invariants (ADR-004, ADR-006, ADR-007).
    /// The domain model enforces the same rules first; these triggers make them hold even for SQL
    /// that bypasses the application. Every violation raises SQLSTATE LC001 with a message that
    /// starts with a domain error code, e.g. <c>JOURNAL_UNBALANCED: ...</c>.
    /// </summary>
    public partial class LedgerInvariantGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION ledger_violation(code text, detail text) RETURNS void
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION USING ERRCODE = 'LC001', MESSAGE = code || ': ' || detail;
                END $$;

                -- Forbid TRUNCATE on every ledger table (a statement-level bypass of row triggers).
                CREATE FUNCTION ledger_forbid_truncate() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM ledger_violation('LEDGER_HISTORY_IMMUTABLE', 'TRUNCATE of ' || TG_TABLE_NAME || ' is not allowed');
                    RETURN NULL;
                END $$;

                -- Ledgers: immutable, never deleted.
                CREATE FUNCTION ledgers_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM ledger_violation('LEDGER_IMMUTABLE', 'ledgers cannot be ' || lower(TG_OP) || 'd');
                    RETURN NULL;
                END $$;

                CREATE TRIGGER ledgers_guard BEFORE UPDATE OR DELETE ON ledgers
                    FOR EACH ROW EXECUTE FUNCTION ledgers_guard();

                -- Accounts: identity, type and currency never change; deactivation is one-way; never deleted.
                CREATE FUNCTION accounts_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        PERFORM ledger_violation('ACCOUNT_DELETE_FORBIDDEN', 'accounts are deactivated, never deleted');
                    END IF;
                    IF NEW.id <> OLD.id OR NEW.ledger_id <> OLD.ledger_id OR NEW.code <> OLD.code
                       OR NEW.name <> OLD.name OR NEW.type <> OLD.type OR NEW.currency <> OLD.currency
                       OR NEW.created_at <> OLD.created_at THEN
                        PERFORM ledger_violation('ACCOUNT_IMMUTABLE', 'account identity, name, type and currency cannot change');
                    END IF;
                    IF NOT OLD.is_active AND (NEW.is_active OR NEW.deactivated_at IS DISTINCT FROM OLD.deactivated_at) THEN
                        PERFORM ledger_violation('ACCOUNT_IMMUTABLE', 'an inactive account cannot be changed');
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER accounts_guard BEFORE UPDATE OR DELETE ON accounts
                    FOR EACH ROW EXECUTE FUNCTION accounts_guard();

                -- Balance check from persisted rows: >= 1 debit, >= 1 credit, SUM(debits) = SUM(credits) exactly.
                CREATE FUNCTION ledger_assert_balanced(p_journal_id uuid) RETURNS void
                LANGUAGE plpgsql AS $$
                DECLARE
                    t record;
                BEGIN
                    SELECT count(*) FILTER (WHERE direction = 'DEBIT')                 AS debit_count,
                           count(*) FILTER (WHERE direction = 'CREDIT')                AS credit_count,
                           coalesce(sum(amount) FILTER (WHERE direction = 'DEBIT'), 0)  AS debits,
                           coalesce(sum(amount) FILTER (WHERE direction = 'CREDIT'), 0) AS credits
                      INTO t
                      FROM journal_entries
                     WHERE journal_id = p_journal_id;

                    IF t.debit_count = 0 OR t.credit_count = 0 THEN
                        PERFORM ledger_violation('JOURNAL_UNBALANCED',
                            format('journal %s needs at least one debit and one credit', p_journal_id));
                    END IF;
                    IF t.debits <> t.credits THEN
                        PERFORM ledger_violation('JOURNAL_UNBALANCED',
                            format('journal %s debits %s <> credits %s', p_journal_id, t.debits, t.credits));
                    END IF;
                END $$;

                -- A reversal must mirror its original exactly: same accounts and amounts, directions swapped.
                CREATE FUNCTION ledger_assert_reversal_mirrors(p_reversal_id uuid, p_original_id uuid) RETURNS void
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF (SELECT status FROM journals WHERE id = p_original_id) <> 'POSTED' THEN
                        PERFORM ledger_violation('JOURNAL_NOT_POSTED', 'only posted journals can be reversed');
                    END IF;
                    IF EXISTS (
                        (SELECT account_id, CASE direction WHEN 'DEBIT' THEN 'CREDIT' ELSE 'DEBIT' END, amount
                           FROM journal_entries WHERE journal_id = p_original_id
                         EXCEPT ALL
                         SELECT account_id, direction, amount FROM journal_entries WHERE journal_id = p_reversal_id)
                        UNION ALL
                        (SELECT account_id, direction, amount FROM journal_entries WHERE journal_id = p_reversal_id
                         EXCEPT ALL
                         SELECT account_id, CASE direction WHEN 'DEBIT' THEN 'CREDIT' ELSE 'DEBIT' END, amount
                           FROM journal_entries WHERE journal_id = p_original_id)
                    ) THEN
                        PERFORM ledger_violation('REVERSAL_MISMATCH',
                            format('journal %s does not exactly mirror journal %s', p_reversal_id, p_original_id));
                    END IF;
                END $$;

                -- New journals start as DRAFT. A reversal may only target a posted, non-reversal journal.
                CREATE FUNCTION journals_before_insert() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    original record;
                BEGIN
                    IF NEW.status <> 'DRAFT' THEN
                        PERFORM ledger_violation('JOURNAL_INVALID_STATE', 'journals must be created as DRAFT');
                    END IF;
                    IF NEW.reverses_journal_id IS NOT NULL THEN
                        SELECT status, reverses_journal_id INTO original
                          FROM journals WHERE id = NEW.reverses_journal_id FOR SHARE;
                        IF original.status IS DISTINCT FROM 'POSTED' THEN
                            PERFORM ledger_violation('JOURNAL_NOT_POSTED', 'only posted journals can be reversed');
                        END IF;
                        IF original.reverses_journal_id IS NOT NULL THEN
                            PERFORM ledger_violation('REVERSAL_OF_REVERSAL', 'a reversal journal cannot itself be reversed');
                        END IF;
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER journals_before_insert BEFORE INSERT ON journals
                    FOR EACH ROW EXECUTE FUNCTION journals_before_insert();

                -- Status changes follow ADR-006 exactly; nothing else about a journal ever changes;
                -- POSTED and REJECTED rows are frozen.
                CREATE FUNCTION journals_before_update() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.status IN ('POSTED', 'REJECTED') THEN
                        PERFORM ledger_violation('JOURNAL_IMMUTABLE',
                            format('journal %s is %s and cannot change', OLD.id, OLD.status));
                    END IF;

                    IF NEW.id <> OLD.id OR NEW.ledger_id <> OLD.ledger_id OR NEW.currency <> OLD.currency
                       OR NEW.description <> OLD.description
                       OR NEW.external_reference IS DISTINCT FROM OLD.external_reference
                       OR NEW.reverses_journal_id IS DISTINCT FROM OLD.reverses_journal_id
                       OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by
                       OR (OLD.submitted_at IS NOT NULL AND (NEW.submitted_at IS DISTINCT FROM OLD.submitted_at
                                                          OR NEW.submitted_by IS DISTINCT FROM OLD.submitted_by)) THEN
                        PERFORM ledger_violation('JOURNAL_IMMUTABLE', 'journal identity and history cannot change');
                    END IF;

                    IF (OLD.status, NEW.status) NOT IN (
                        ('DRAFT', 'PENDING_APPROVAL'),
                        ('PENDING_APPROVAL', 'APPROVED'),
                        ('PENDING_APPROVAL', 'REJECTED'),
                        ('APPROVED', 'POSTED')) THEN
                        PERFORM ledger_violation('JOURNAL_INVALID_TRANSITION',
                            format('%s -> %s is not a permitted transition', OLD.status, NEW.status));
                    END IF;

                    IF NEW.status = 'POSTED' AND (NEW.approved_at IS DISTINCT FROM OLD.approved_at
                                                  OR NEW.approved_by IS DISTINCT FROM OLD.approved_by) THEN
                        PERFORM ledger_violation('JOURNAL_IMMUTABLE', 'approval evidence cannot change at posting');
                    END IF;

                    IF NEW.status IN ('PENDING_APPROVAL', 'POSTED') THEN
                        PERFORM ledger_assert_balanced(NEW.id);
                        IF NEW.reverses_journal_id IS NOT NULL THEN
                            PERFORM ledger_assert_reversal_mirrors(NEW.id, NEW.reverses_journal_id);
                        END IF;
                    END IF;

                    IF NEW.status = 'POSTED' THEN
                        -- FOR SHARE blocks a concurrent deactivation until this transaction ends.
                        PERFORM 1 FROM accounts
                         WHERE id IN (SELECT account_id FROM journal_entries WHERE journal_id = NEW.id)
                         ORDER BY id
                           FOR SHARE;
                        IF EXISTS (SELECT 1
                                     FROM journal_entries e JOIN accounts a ON a.id = e.account_id
                                    WHERE e.journal_id = NEW.id AND NOT a.is_active) THEN
                            PERFORM ledger_violation('ACCOUNT_INACTIVE',
                                format('journal %s references an inactive account', NEW.id));
                        END IF;
                    END IF;

                    RETURN NEW;
                END $$;

                CREATE TRIGGER journals_before_update BEFORE UPDATE ON journals
                    FOR EACH ROW EXECUTE FUNCTION journals_before_update();

                -- Only drafts could ever be deleted (no such operation exists in Milestone 1).
                CREATE FUNCTION journals_before_delete() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.status <> 'DRAFT' OR OLD.reverses_journal_id IS NOT NULL THEN
                        PERFORM ledger_violation('JOURNAL_IMMUTABLE',
                            format('journal %s is %s and cannot be deleted', OLD.id, OLD.status));
                    END IF;
                    RETURN OLD;
                END $$;

                CREATE TRIGGER journals_before_delete BEFORE DELETE ON journals
                    FOR EACH ROW EXECUTE FUNCTION journals_before_delete();

                -- A reversal is inserted as a draft and must be submitted in the same transaction (ADR-006).
                CREATE FUNCTION journals_reversal_not_left_draft() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF (SELECT status FROM journals WHERE id = NEW.id) = 'DRAFT' THEN
                        PERFORM ledger_violation('REVERSAL_LEFT_IN_DRAFT',
                            format('reversal journal %s must be submitted in the transaction that creates it', NEW.id));
                    END IF;
                    RETURN NULL;
                END $$;

                CREATE CONSTRAINT TRIGGER journals_reversal_not_left_draft AFTER INSERT ON journals
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW WHEN (NEW.reverses_journal_id IS NOT NULL)
                    EXECUTE FUNCTION journals_reversal_not_left_draft();

                -- Entries are never updated, and are inserted or deleted only while their journal is DRAFT.
                -- FOR SHARE waits for any concurrent status change of the journal, then re-reads it.
                CREATE FUNCTION journal_entries_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    journal_status text;
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        PERFORM ledger_violation('JOURNAL_ENTRY_IMMUTABLE', 'journal entries cannot be updated');
                    END IF;
                    SELECT status INTO journal_status FROM journals
                     WHERE id = CASE WHEN TG_OP = 'DELETE' THEN OLD.journal_id ELSE NEW.journal_id END
                       FOR SHARE;
                    IF journal_status IS DISTINCT FROM 'DRAFT' THEN
                        PERFORM ledger_violation('JOURNAL_ENTRY_IMMUTABLE',
                            format('entries of a %s journal cannot be %s', journal_status,
                                   CASE TG_OP WHEN 'INSERT' THEN 'added' ELSE 'removed' END));
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER journal_entries_guard BEFORE INSERT OR UPDATE OR DELETE ON journal_entries
                    FOR EACH ROW EXECUTE FUNCTION journal_entries_guard();

                -- Audit: every status change is recorded by the database, with the actor the row carries.
                -- SECURITY DEFINER lets it write a table the runtime role cannot write.
                CREATE FUNCTION journals_audit_transition() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
                BEGIN
                    INSERT INTO journal_status_transitions (journal_id, from_status, to_status, actor, occurred_at, reason)
                    VALUES (
                        NEW.id,
                        CASE WHEN TG_OP = 'INSERT' THEN NULL ELSE OLD.status END,
                        NEW.status,
                        CASE NEW.status
                            WHEN 'DRAFT' THEN NEW.created_by
                            WHEN 'PENDING_APPROVAL' THEN NEW.submitted_by
                            WHEN 'APPROVED' THEN NEW.approved_by
                            WHEN 'REJECTED' THEN NEW.rejected_by
                            WHEN 'POSTED' THEN NEW.posted_by
                        END,
                        CASE NEW.status
                            WHEN 'DRAFT' THEN NEW.created_at
                            WHEN 'PENDING_APPROVAL' THEN NEW.submitted_at
                            WHEN 'APPROVED' THEN NEW.approved_at
                            WHEN 'REJECTED' THEN NEW.rejected_at
                            WHEN 'POSTED' THEN NEW.posted_at
                        END,
                        CASE WHEN NEW.status = 'REJECTED' THEN NEW.rejection_reason END);
                    RETURN NULL;
                END $$;

                CREATE TRIGGER journals_audit_insert AFTER INSERT ON journals
                    FOR EACH ROW EXECUTE FUNCTION journals_audit_transition();
                CREATE TRIGGER journals_audit_status AFTER UPDATE OF status ON journals
                    FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
                    EXECUTE FUNCTION journals_audit_transition();

                CREATE FUNCTION journal_status_transitions_guard() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM ledger_violation('AUDIT_IMMUTABLE', 'journal status transitions are append-only');
                    RETURN NULL;
                END $$;

                CREATE TRIGGER journal_status_transitions_guard BEFORE UPDATE OR DELETE ON journal_status_transitions
                    FOR EACH ROW EXECUTE FUNCTION journal_status_transitions_guard();

                CREATE TRIGGER ledgers_no_truncate BEFORE TRUNCATE ON ledgers
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_forbid_truncate();
                CREATE TRIGGER accounts_no_truncate BEFORE TRUNCATE ON accounts
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_forbid_truncate();
                CREATE TRIGGER journals_no_truncate BEFORE TRUNCATE ON journals
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_forbid_truncate();
                CREATE TRIGGER journal_entries_no_truncate BEFORE TRUNCATE ON journal_entries
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_forbid_truncate();
                CREATE TRIGGER journal_status_transitions_no_truncate BEFORE TRUNCATE ON journal_status_transitions
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_forbid_truncate();

                -- Runtime role (ADR-007). Default privileges grant it SELECT/INSERT/UPDATE on new tables;
                -- narrow that further where the application never needs a privilege.
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ledger_runtime') THEN
                        REVOKE ALL ON ledgers, accounts, journals, journal_entries, journal_status_transitions,
                                      __ef_migrations_history FROM ledger_runtime;
                        GRANT SELECT, INSERT ON ledgers TO ledger_runtime;
                        GRANT SELECT, INSERT, UPDATE ON accounts TO ledger_runtime;
                        GRANT SELECT, INSERT, UPDATE ON journals TO ledger_runtime;
                        GRANT SELECT, INSERT ON journal_entries TO ledger_runtime;
                        GRANT SELECT ON journal_status_transitions TO ledger_runtime;
                        GRANT SELECT ON __ef_migrations_history TO ledger_runtime;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER journal_status_transitions_no_truncate ON journal_status_transitions;
                DROP TRIGGER journal_entries_no_truncate ON journal_entries;
                DROP TRIGGER journals_no_truncate ON journals;
                DROP TRIGGER accounts_no_truncate ON accounts;
                DROP TRIGGER ledgers_no_truncate ON ledgers;
                DROP TRIGGER journal_status_transitions_guard ON journal_status_transitions;
                DROP TRIGGER journals_audit_status ON journals;
                DROP TRIGGER journals_audit_insert ON journals;
                DROP TRIGGER journal_entries_guard ON journal_entries;
                DROP TRIGGER journals_reversal_not_left_draft ON journals;
                DROP TRIGGER journals_before_delete ON journals;
                DROP TRIGGER journals_before_update ON journals;
                DROP TRIGGER journals_before_insert ON journals;
                DROP TRIGGER accounts_guard ON accounts;
                DROP TRIGGER ledgers_guard ON ledgers;
                DROP FUNCTION journal_status_transitions_guard();
                DROP FUNCTION journals_audit_transition();
                DROP FUNCTION journal_entries_guard();
                DROP FUNCTION journals_reversal_not_left_draft();
                DROP FUNCTION journals_before_delete();
                DROP FUNCTION journals_before_update();
                DROP FUNCTION journals_before_insert();
                DROP FUNCTION ledger_assert_reversal_mirrors(uuid, uuid);
                DROP FUNCTION ledger_assert_balanced(uuid);
                DROP FUNCTION accounts_guard();
                DROP FUNCTION ledgers_guard();
                DROP FUNCTION ledger_forbid_truncate();
                DROP FUNCTION ledger_violation(text, text);
                """);
        }
    }
}
