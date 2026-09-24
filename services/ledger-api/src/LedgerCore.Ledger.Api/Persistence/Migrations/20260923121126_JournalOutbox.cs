using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LedgerCore.Ledger.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JournalOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbox_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    aggregate_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_events", x => x.id);
                    table.CheckConstraint("ck_outbox_events_published_after_created", "published_at IS NULL OR published_at >= created_at");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_unpublished",
                table: "outbox_events",
                column: "created_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_outbox_events_aggregate_event",
                table: "outbox_events",
                columns: new[] { "aggregate_id", "event_type" },
                unique: true);

            // Hand-written guards (ADR-014): the event is immutable except for being marked published
            // once; it is never deleted; a JournalPosted event must describe a journal that is POSTED
            // when the transaction commits (deferred, since EF does not order the two writes).
            migrationBuilder.Sql(
                """
                CREATE FUNCTION outbox_events_before_update() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.id <> OLD.id OR NEW.aggregate_type <> OLD.aggregate_type OR NEW.aggregate_id <> OLD.aggregate_id
                       OR NEW.event_type <> OLD.event_type OR NEW.payload <> OLD.payload OR NEW.created_at <> OLD.created_at
                       OR OLD.published_at IS NOT NULL THEN
                        PERFORM ledger_violation('OUTBOX_IMMUTABLE', 'outbox events can only be marked published, once');
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER outbox_events_before_update BEFORE UPDATE ON outbox_events
                    FOR EACH ROW EXECUTE FUNCTION outbox_events_before_update();

                CREATE FUNCTION outbox_events_no_delete() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM ledger_violation('OUTBOX_IMMUTABLE', 'outbox events cannot be deleted');
                    RETURN NULL;
                END $$;

                CREATE TRIGGER outbox_events_no_delete BEFORE DELETE ON outbox_events
                    FOR EACH ROW EXECUTE FUNCTION outbox_events_no_delete();
                CREATE TRIGGER outbox_events_no_truncate BEFORE TRUNCATE ON outbox_events
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_forbid_truncate();

                CREATE FUNCTION outbox_events_journal_posted() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF (SELECT status FROM journals WHERE id = NEW.aggregate_id) IS DISTINCT FROM 'POSTED' THEN
                        PERFORM ledger_violation('OUTBOX_AGGREGATE_NOT_POSTED',
                            format('JournalPosted event for journal %s, which is not POSTED', NEW.aggregate_id));
                    END IF;
                    RETURN NULL;
                END $$;

                CREATE CONSTRAINT TRIGGER outbox_events_journal_posted AFTER INSERT ON outbox_events
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW WHEN (NEW.event_type = 'JournalPosted')
                    EXECUTE FUNCTION outbox_events_journal_posted();

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ledger_runtime') THEN
                        REVOKE ALL ON outbox_events FROM ledger_runtime;
                        GRANT SELECT, INSERT ON outbox_events TO ledger_runtime;
                        GRANT UPDATE (published_at) ON outbox_events TO ledger_runtime;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER outbox_events_journal_posted ON outbox_events;
                DROP TRIGGER outbox_events_no_truncate ON outbox_events;
                DROP TRIGGER outbox_events_no_delete ON outbox_events;
                DROP TRIGGER outbox_events_before_update ON outbox_events;
                DROP FUNCTION outbox_events_journal_posted();
                DROP FUNCTION outbox_events_no_delete();
                DROP FUNCTION outbox_events_before_update();
                """);

            migrationBuilder.DropTable(
                name: "outbox_events");
        }
    }
}
