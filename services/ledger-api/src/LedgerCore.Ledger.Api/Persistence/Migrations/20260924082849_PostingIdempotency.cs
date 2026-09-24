using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LedgerCore.Ledger.Api.Persistence.Migrations
{
    /// <summary>
    /// Milestone 4 (ADR-015): idempotency claims for posting and reversal, the audit's decision and
    /// key columns, and the guards that bind posting to its claim and its APPROVED evidence.
    /// </summary>
    public partial class PostingIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                table: "journal_status_transitions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "policy_decision_id",
                table: "journal_status_transitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "command_idempotency",
                columns: table => new
                {
                    ledger_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", nullable: false),
                    target_journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_command_idempotency", x => new { x.ledger_id, x.operation, x.idempotency_key });
                    table.CheckConstraint("ck_command_idempotency_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_command_idempotency_key", "idempotency_key ~ '^[A-Za-z0-9._:~-]{8,128}$'");
                    table.CheckConstraint("ck_command_idempotency_operation", "operation IN ('POST_JOURNAL', 'REVERSE_JOURNAL')");
                    table.CheckConstraint("ck_command_idempotency_result", "(operation = 'POST_JOURNAL' AND result_journal_id = target_journal_id) OR (operation = 'REVERSE_JOURNAL' AND result_journal_id <> target_journal_id)");
                    table.ForeignKey(
                        name: "fk_command_idempotency_ledgers_ledger_id",
                        column: x => x.ledger_id,
                        principalTable: "ledgers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_command_idempotency_result_journal",
                        column: x => x.result_journal_id,
                        principalTable: "journals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_command_idempotency_target_journal",
                        column: x => x.target_journal_id,
                        principalTable: "journals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_command_idempotency_posting",
                table: "command_idempotency",
                column: "target_journal_id",
                unique: true,
                filter: "operation = 'POST_JOURNAL'");

            migrationBuilder.CreateIndex(
                name: "ux_command_idempotency_reversal",
                table: "command_idempotency",
                column: "result_journal_id",
                unique: true,
                filter: "operation = 'REVERSE_JOURNAL'");

            // Hand-written guards (Milestone 4, ADR-015). Claims are append-only, belong to their
            // ledger, and at commit describe an effect that happened. Posting requires a claim and
            // APPROVED evidence; every reversal requires a claim. The audit records the authorizing
            // decision and the idempotency key; the JournalPosted event must name that decision.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION command_idempotency_before_insert() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM journals WHERE id = NEW.target_journal_id AND ledger_id = NEW.ledger_id)
                       OR NOT EXISTS (SELECT 1 FROM journals WHERE id = NEW.result_journal_id AND ledger_id = NEW.ledger_id) THEN
                        PERFORM ledger_violation('IDEMPOTENCY_LEDGER_MISMATCH',
                            format('claim %s in ledger %s names a journal of another ledger', NEW.idempotency_key, NEW.ledger_id));
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER command_idempotency_before_insert BEFORE INSERT ON command_idempotency
                    FOR EACH ROW EXECUTE FUNCTION command_idempotency_before_insert();

                CREATE FUNCTION command_idempotency_immutable() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM ledger_violation('IDEMPOTENCY_RECORD_IMMUTABLE', 'idempotency records cannot be changed or deleted');
                    RETURN NULL;
                END $$;

                CREATE TRIGGER command_idempotency_immutable BEFORE UPDATE OR DELETE ON command_idempotency
                    FOR EACH ROW EXECUTE FUNCTION command_idempotency_immutable();
                CREATE TRIGGER command_idempotency_no_truncate BEFORE TRUNCATE ON command_idempotency
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_forbid_truncate();

                -- Deferred: EF and the command write the claim and its effect in separate statements.
                CREATE FUNCTION command_idempotency_effect_committed() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.operation = 'POST_JOURNAL'
                       AND (SELECT status FROM journals WHERE id = NEW.target_journal_id) IS DISTINCT FROM 'POSTED' THEN
                        PERFORM ledger_violation('IDEMPOTENCY_EFFECT_MISSING',
                            format('posting claim %s for journal %s, which is not POSTED', NEW.idempotency_key, NEW.target_journal_id));
                    END IF;
                    IF NEW.operation = 'REVERSE_JOURNAL'
                       AND (SELECT reverses_journal_id FROM journals WHERE id = NEW.result_journal_id) IS DISTINCT FROM NEW.target_journal_id THEN
                        PERFORM ledger_violation('IDEMPOTENCY_EFFECT_MISSING',
                            format('reversal claim %s: journal %s does not reverse journal %s', NEW.idempotency_key, NEW.result_journal_id, NEW.target_journal_id));
                    END IF;
                    RETURN NULL;
                END $$;

                CREATE CONSTRAINT TRIGGER command_idempotency_effect_committed AFTER INSERT ON command_idempotency
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION command_idempotency_effect_committed();

                -- journal_status_transitions.policy_decision_id has no foreign key on purpose: only the
                -- audit trigger writes it, copied from the immutable evidence row, and a foreign key
                -- would pre-empt the evidence table's own TRUNCATE guard.

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

                    -- Milestone 4 (ADR-015): posting itself requires APPROVED evidence (not only via
                    -- APPROVED), and a posting claim written earlier in the same transaction.
                    IF NEW.status = 'POSTED' AND NOT EXISTS (
                        SELECT 1 FROM journal_policy_decisions d WHERE d.journal_id = NEW.id AND d.decision = 'APPROVED') THEN
                        PERFORM ledger_violation('APPROVAL_EVIDENCE_REQUIRED',
                            format('journal %s has no recorded APPROVED policy decision to post under', NEW.id));
                    END IF;
                    IF NEW.status = 'POSTED' AND NOT EXISTS (
                        SELECT 1 FROM command_idempotency c WHERE c.operation = 'POST_JOURNAL' AND c.target_journal_id = NEW.id) THEN
                        PERFORM ledger_violation('IDEMPOTENCY_CLAIM_REQUIRED',
                            format('journal %s is posted only by an idempotent post command', NEW.id));
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

                CREATE OR REPLACE FUNCTION journals_reversal_not_left_draft() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF (SELECT status FROM journals WHERE id = NEW.id) = 'DRAFT' THEN
                        PERFORM ledger_violation('REVERSAL_LEFT_IN_DRAFT',
                            format('reversal journal %s must be submitted in the transaction that creates it', NEW.id));
                    END IF;
                    -- Milestone 4 (ADR-015): every reversal is created by an idempotent reverse command.
                    IF NOT EXISTS (SELECT 1 FROM command_idempotency c
                                    WHERE c.operation = 'REVERSE_JOURNAL' AND c.result_journal_id = NEW.id
                                      AND c.target_journal_id = NEW.reverses_journal_id) THEN
                        PERFORM ledger_violation('IDEMPOTENCY_CLAIM_REQUIRED',
                            format('reversal journal %s is created only by an idempotent reverse command', NEW.id));
                    END IF;
                    RETURN NULL;
                END $$;

                CREATE OR REPLACE FUNCTION journals_audit_transition() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
                BEGIN
                    INSERT INTO journal_status_transitions
                        (journal_id, from_status, to_status, actor, occurred_at, reason, policy_decision_id, idempotency_key)
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
                        CASE WHEN NEW.status = 'REJECTED' THEN NEW.rejection_reason END,
                        -- Milestone 4: the decision that authorized the transition ...
                        CASE WHEN NEW.status IN ('APPROVED', 'REJECTED', 'POSTED') THEN
                            (SELECT d.decision_id FROM journal_policy_decisions d WHERE d.journal_id = NEW.id) END,
                        -- ... and the idempotent command that caused it.
                        CASE
                            WHEN NEW.status = 'POSTED' THEN
                                (SELECT c.idempotency_key FROM command_idempotency c
                                  WHERE c.operation = 'POST_JOURNAL' AND c.target_journal_id = NEW.id)
                            WHEN NEW.status = 'PENDING_APPROVAL' AND NEW.reverses_journal_id IS NOT NULL THEN
                                (SELECT c.idempotency_key FROM command_idempotency c
                                  WHERE c.operation = 'REVERSE_JOURNAL' AND c.result_journal_id = NEW.id)
                        END);
                    RETURN NULL;
                END $$;

                CREATE OR REPLACE FUNCTION outbox_events_journal_posted() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF (SELECT status FROM journals WHERE id = NEW.aggregate_id) IS DISTINCT FROM 'POSTED' THEN
                        PERFORM ledger_violation('OUTBOX_AGGREGATE_NOT_POSTED',
                            format('JournalPosted event for journal %s, which is not POSTED', NEW.aggregate_id));
                    END IF;
                    -- Milestone 4: the event names the APPROVED decision the journal was posted under.
                    IF (NEW.payload ->> 'policyDecisionId') IS DISTINCT FROM (
                        SELECT d.decision_id::text FROM journal_policy_decisions d
                         WHERE d.journal_id = NEW.aggregate_id AND d.decision = 'APPROVED') THEN
                        PERFORM ledger_violation('OUTBOX_EVIDENCE_MISMATCH',
                            format('JournalPosted event for journal %s does not reference its APPROVED policy decision', NEW.aggregate_id));
                    END IF;
                    RETURN NULL;
                END $$;

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ledger_runtime') THEN
                        REVOKE ALL ON command_idempotency FROM ledger_runtime;
                        GRANT SELECT, INSERT ON command_idempotency TO ledger_runtime;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

            // Restore the Milestone 3 function bodies before the table they read is dropped.
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

                CREATE OR REPLACE FUNCTION journals_reversal_not_left_draft() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF (SELECT status FROM journals WHERE id = NEW.id) = 'DRAFT' THEN
                        PERFORM ledger_violation('REVERSAL_LEFT_IN_DRAFT',
                            format('reversal journal %s must be submitted in the transaction that creates it', NEW.id));
                    END IF;
                    RETURN NULL;
                END $$;

                CREATE OR REPLACE FUNCTION journals_audit_transition() RETURNS trigger
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

                CREATE OR REPLACE FUNCTION outbox_events_journal_posted() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF (SELECT status FROM journals WHERE id = NEW.aggregate_id) IS DISTINCT FROM 'POSTED' THEN
                        PERFORM ledger_violation('OUTBOX_AGGREGATE_NOT_POSTED',
                            format('JournalPosted event for journal %s, which is not POSTED', NEW.aggregate_id));
                    END IF;
                    RETURN NULL;
                END $$;

                DROP TRIGGER command_idempotency_effect_committed ON command_idempotency;
                DROP TRIGGER command_idempotency_no_truncate ON command_idempotency;
                DROP TRIGGER command_idempotency_immutable ON command_idempotency;
                DROP TRIGGER command_idempotency_before_insert ON command_idempotency;
                DROP FUNCTION command_idempotency_effect_committed();
                DROP FUNCTION command_idempotency_immutable();
                DROP FUNCTION command_idempotency_before_insert();
                """);

            migrationBuilder.DropTable(
                name: "command_idempotency");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "journal_status_transitions");

            migrationBuilder.DropColumn(
                name: "policy_decision_id",
                table: "journal_status_transitions");
        }
    }
}
