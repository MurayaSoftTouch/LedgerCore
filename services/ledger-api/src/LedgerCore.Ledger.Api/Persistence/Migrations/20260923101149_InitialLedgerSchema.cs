using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LedgerCore.Ledger.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedgerSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ledgers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledgers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ledger_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.UniqueConstraint("ak_accounts_id_ledger_id_currency", x => new { x.id, x.ledger_id, x.currency });
                    table.CheckConstraint("ck_accounts_active_state", "(is_active AND deactivated_at IS NULL) OR (NOT is_active AND deactivated_at IS NOT NULL)");
                    table.CheckConstraint("ck_accounts_type", "type IN ('ASSET', 'LIABILITY', 'EQUITY', 'REVENUE', 'EXPENSE')");
                    table.ForeignKey(
                        name: "fk_accounts_ledgers_ledger_id",
                        column: x => x.ledger_id,
                        principalTable: "ledgers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "journals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ledger_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    external_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    reverses_journal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejected_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    posted_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journals", x => x.id);
                    table.UniqueConstraint("ak_journals_id_ledger_id_currency", x => new { x.id, x.ledger_id, x.currency });
                    table.CheckConstraint("ck_journals_approved_fields", "(status IN ('APPROVED', 'POSTED') AND approved_at IS NOT NULL AND approved_by IS NOT NULL) OR (status NOT IN ('APPROVED', 'POSTED') AND approved_at IS NULL AND approved_by IS NULL)");
                    table.CheckConstraint("ck_journals_not_self_reversal", "reverses_journal_id IS NULL OR reverses_journal_id <> id");
                    table.CheckConstraint("ck_journals_posted_fields", "(status = 'POSTED' AND posted_at IS NOT NULL AND posted_by IS NOT NULL) OR (status <> 'POSTED' AND posted_at IS NULL AND posted_by IS NULL)");
                    table.CheckConstraint("ck_journals_rejected_fields", "(status = 'REJECTED' AND rejected_at IS NOT NULL AND rejected_by IS NOT NULL AND rejection_reason IS NOT NULL) OR (status <> 'REJECTED' AND rejected_at IS NULL AND rejected_by IS NULL AND rejection_reason IS NULL)");
                    table.CheckConstraint("ck_journals_status", "status IN ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'REJECTED', 'POSTED')");
                    table.CheckConstraint("ck_journals_submitted_fields", "(status = 'DRAFT' AND submitted_at IS NULL AND submitted_by IS NULL) OR (status <> 'DRAFT' AND submitted_at IS NOT NULL AND submitted_by IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_journals_ledgers_ledger_id",
                        column: x => x.ledger_id,
                        principalTable: "ledgers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_journals_reversed_journal",
                        columns: x => new { x.reverses_journal_id, x.ledger_id, x.currency },
                        principalTable: "journals",
                        principalColumns: new[] { "id", "ledger_id", "currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ledger_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(22,4)", nullable: false),
                    memo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entries", x => x.id);
                    table.CheckConstraint("ck_journal_entries_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_journal_entries_direction", "direction IN ('DEBIT', 'CREDIT')");
                    table.CheckConstraint("ck_journal_entries_line_number", "line_number >= 1");
                    table.ForeignKey(
                        name: "fk_journal_entries_account",
                        columns: x => new { x.account_id, x.ledger_id, x.currency },
                        principalTable: "accounts",
                        principalColumns: new[] { "id", "ledger_id", "currency" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_journal_entries_journal",
                        columns: x => new { x.journal_id, x.ledger_id, x.currency },
                        principalTable: "journals",
                        principalColumns: new[] { "id", "ledger_id", "currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "journal_status_transitions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    journal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    to_status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_status_transitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_journal_status_transitions_journals_journal_id",
                        column: x => x.journal_id,
                        principalTable: "journals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_accounts_ledger_code",
                table: "accounts",
                columns: new[] { "ledger_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_account_id_ledger_id_currency",
                table: "journal_entries",
                columns: new[] { "account_id", "ledger_id", "currency" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_journal_id_ledger_id_currency",
                table: "journal_entries",
                columns: new[] { "journal_id", "ledger_id", "currency" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_journal_id_line_number",
                table: "journal_entries",
                columns: new[] { "journal_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_status_transitions_journal_id_id",
                table: "journal_status_transitions",
                columns: new[] { "journal_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_journals_ledger_id_status",
                table: "journals",
                columns: new[] { "ledger_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_journals_reverses_journal_id_ledger_id_currency",
                table: "journals",
                columns: new[] { "reverses_journal_id", "ledger_id", "currency" });

            migrationBuilder.CreateIndex(
                name: "ux_journals_ledger_external_reference",
                table: "journals",
                columns: new[] { "ledger_id", "external_reference" },
                unique: true,
                filter: "external_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_journals_live_reversal",
                table: "journals",
                column: "reverses_journal_id",
                unique: true,
                filter: "reverses_journal_id IS NOT NULL AND status <> 'REJECTED'");

            migrationBuilder.CreateIndex(
                name: "ux_ledgers_code",
                table: "ledgers",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "journal_entries");

            migrationBuilder.DropTable(
                name: "journal_status_transitions");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "journals");

            migrationBuilder.DropTable(
                name: "ledgers");
        }
    }
}
