using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LedgerCore.Ledger.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PolicyApprovalEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "transaction_type",
                table: "journals",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "ADJUSTMENT");

            migrationBuilder.CreateTable(
                name: "journal_policy_decisions",
                columns: table => new
                {
                    journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason_codes = table.Column<List<string>>(type: "text[]", nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    contract_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_policy_decisions", x => x.journal_id);
                    table.CheckConstraint("ck_journal_policy_decisions_decision", "decision IN ('APPROVED', 'REJECTED', 'REVIEW_REQUIRED')");
                    table.CheckConstraint("ck_journal_policy_decisions_reasons", "(decision = 'APPROVED') = (cardinality(reason_codes) = 0)");
                    table.CheckConstraint("ck_journal_policy_decisions_version", "length(policy_version) BETWEEN 1 AND 128");
                    table.ForeignKey(
                        name: "fk_journal_policy_decisions_journals_journal_id",
                        column: x => x.journal_id,
                        principalTable: "journals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_journals_transaction_type",
                table: "journals",
                sql: "transaction_type IN ('PAYMENT', 'TRANSFER', 'ADJUSTMENT', 'REVERSAL', 'FEE')");

            migrationBuilder.CreateIndex(
                name: "ux_journal_policy_decisions_decision",
                table: "journal_policy_decisions",
                column: "decision_id",
                unique: true);

            // Hand-written (Milestone 3, ADR-012). Journals created before this migration were not
            // classified; they are backfilled as ADJUSTMENT, the most review-prone type. The default
            // is then dropped so every new journal must state its type.
            migrationBuilder.Sql(
                """
                ALTER TABLE journals ALTER COLUMN transaction_type DROP DEFAULT;
                -- Reversals are always REVERSAL. NOT VALID: enforced for new rows without rewriting
                -- (immutable) pre-migration rows.
                ALTER TABLE journals ADD CONSTRAINT ck_journals_reversal_type
                    CHECK ((reverses_journal_id IS NULL) = (transaction_type <> 'REVERSAL')) NOT VALID;

                -- Evidence is recorded only for a PENDING_APPROVAL journal, and never changes.
                CREATE FUNCTION journal_policy_decisions_before_insert() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF (SELECT status FROM journals WHERE id = NEW.journal_id FOR SHARE) IS DISTINCT FROM 'PENDING_APPROVAL' THEN
                        PERFORM ledger_violation('JOURNAL_NOT_PENDING_APPROVAL',
                            'policy decisions are recorded only for PENDING_APPROVAL journals');
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER journal_policy_decisions_before_insert BEFORE INSERT ON journal_policy_decisions
                    FOR EACH ROW EXECUTE FUNCTION journal_policy_decisions_before_insert();

                CREATE FUNCTION journal_policy_decisions_immutable() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM ledger_violation('POLICY_EVIDENCE_IMMUTABLE', 'recorded policy decisions cannot be changed');
                    RETURN NULL;
                END $$;

                CREATE TRIGGER journal_policy_decisions_immutable BEFORE UPDATE OR DELETE ON journal_policy_decisions
                    FOR EACH ROW EXECUTE FUNCTION journal_policy_decisions_immutable();
                CREATE TRIGGER journal_policy_decisions_no_truncate BEFORE TRUNCATE ON journal_policy_decisions
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_forbid_truncate();

                -- The lifecycle guard from LedgerInvariantGuards, unchanged except: transaction_type is
                -- immutable, and APPROVED / REJECTED require matching evidence.
                CREATE OR REPLACE FUNCTION journals_before_update() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.status IN ('POSTED', 'REJECTED') THEN
                        PERFORM ledger_violation('JOURNAL_IMMUTABLE',
                            format('journal %s is %s and cannot change', OLD.id, OLD.status));
                    END IF;

                    IF NEW.id <> OLD.id OR NEW.ledger_id <> OLD.ledger_id OR NEW.currency <> OLD.currency
                       OR NEW.transaction_type <> OLD.transaction_type
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

                    -- Milestone 3: APPROVED and REJECTED only with a matching recorded policy decision
                    -- (journal_policy_decisions). POSTED requires APPROVED, so posting does too.
                    IF NEW.status = 'APPROVED' AND NOT EXISTS (
                        SELECT 1 FROM journal_policy_decisions d WHERE d.journal_id = NEW.id AND d.decision = 'APPROVED') THEN
                        PERFORM ledger_violation('APPROVAL_EVIDENCE_REQUIRED',
                            format('journal %s has no recorded APPROVED policy decision', NEW.id));
                    END IF;
                    IF NEW.status = 'REJECTED' AND NOT EXISTS (
                        SELECT 1 FROM journal_policy_decisions d WHERE d.journal_id = NEW.id AND d.decision = 'REJECTED') THEN
                        PERFORM ledger_violation('REJECTION_EVIDENCE_REQUIRED',
                            format('journal %s has no recorded REJECTED policy decision', NEW.id));
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

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ledger_runtime') THEN
                        REVOKE ALL ON journal_policy_decisions FROM ledger_runtime;
                        GRANT SELECT, INSERT ON journal_policy_decisions TO ledger_runtime;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION journals_before_update() RETURNS trigger
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

                DROP TRIGGER journal_policy_decisions_no_truncate ON journal_policy_decisions;
                DROP TRIGGER journal_policy_decisions_immutable ON journal_policy_decisions;
                DROP TRIGGER journal_policy_decisions_before_insert ON journal_policy_decisions;
                DROP FUNCTION journal_policy_decisions_immutable();
                DROP FUNCTION journal_policy_decisions_before_insert();
                ALTER TABLE journals DROP CONSTRAINT ck_journals_reversal_type;
                """);

            migrationBuilder.DropTable(
                name: "journal_policy_decisions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_journals_transaction_type",
                table: "journals");

            migrationBuilder.DropColumn(
                name: "transaction_type",
                table: "journals");
        }
    }
}
