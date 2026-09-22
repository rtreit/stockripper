using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialDurableFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_chain_heads",
                columns: table => new
                {
                    chain_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false),
                    last_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_chain_heads", x => x.chain_id);
                    table.CheckConstraint("ck_audit_chain_heads_hash", "last_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_audit_chain_heads_sequence", "last_sequence >= 0");
                });

            migrationBuilder.CreateTable(
                name: "goal_drafts",
                columns: table => new
                {
                    draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    definition_json = table.Column<string>(type: "jsonb", nullable: false),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_editor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goal_drafts", x => x.draft_id);
                    table.CheckConstraint("ck_goal_drafts_publication", "(state = 'DRAFT' AND published_at IS NULL) OR (state = 'PUBLISHED' AND published_at IS NOT NULL)");
                    table.CheckConstraint("ck_goal_drafts_revision", "revision > 0");
                    table.CheckConstraint("ck_goal_drafts_state", "state IN ('DRAFT', 'PUBLISHED')");
                    table.CheckConstraint("ck_goal_drafts_version", "version_number > 0");
                });

            migrationBuilder.CreateTable(
                name: "goal_versions",
                columns: table => new
                {
                    goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    definition_json = table.Column<string>(type: "jsonb", nullable: false),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_goal_versions", x => x.goal_version_id);
                    table.CheckConstraint("ck_goal_versions_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_goal_versions_mode", "mode IN ('REPLAY', 'RESEARCH', 'PAPER_REVIEW', 'PAPER_AUTO', 'LIVE_REVIEW', 'LIVE_AUTO')");
                    table.CheckConstraint("ck_goal_versions_version", "version_number > 0");
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    related_record_identifiers = table.Column<string>(type: "jsonb", nullable: false),
                    previous_state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    new_state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source_reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    canonical_payload = table.Column<string>(type: "text", nullable: false),
                    previous_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.event_id);
                    table.CheckConstraint("ck_audit_events_event_hash", "event_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_audit_events_previous_hash", "previous_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_audit_events_sequence", "sequence > 0");
                    table.CheckConstraint("ck_audit_events_type", "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'INTEGRITY_VERIFICATION_FAILED')");
                    table.ForeignKey(
                        name: "FK_audit_events_goal_versions_goal_version_id",
                        column: x => x.goal_version_id,
                        principalTable: "goal_versions",
                        principalColumn: "goal_version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_goal_version_id",
                table: "audit_events",
                column: "goal_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_sequence",
                table: "audit_events",
                column: "sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_goal_drafts_goal_id_version_number",
                table: "goal_drafts",
                columns: new[] { "goal_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_goal_drafts_proposed_goal_version_id",
                table: "goal_drafts",
                column: "proposed_goal_version_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_goal_versions_goal_id_version_number",
                table: "goal_versions",
                columns: new[] { "goal_id", "version_number" },
                unique: true);

            migrationBuilder.InsertData(
                table: "audit_chain_heads",
                columns: new[] { "chain_id", "last_sequence", "last_hash" },
                values: new object[]
                {
                    "primary",
                    0L,
                    "0000000000000000000000000000000000000000000000000000000000000000"
                });

            migrationBuilder.Sql(
                """
                CREATE FUNCTION reject_audit_event_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION 'Audit events are append-only.';
                END;
                $function$;

                CREATE TRIGGER audit_events_no_update_delete
                BEFORE UPDATE OR DELETE ON audit_events
                FOR EACH STATEMENT
                EXECUTE FUNCTION reject_audit_event_mutation();

                CREATE TRIGGER audit_events_no_truncate
                BEFORE TRUNCATE ON audit_events
                FOR EACH STATEMENT
                EXECUTE FUNCTION reject_audit_event_mutation();

                CREATE FUNCTION reject_goal_version_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    RAISE EXCEPTION 'Goal versions are immutable.';
                END;
                $function$;

                CREATE TRIGGER goal_versions_no_update_delete
                BEFORE UPDATE OR DELETE ON goal_versions
                FOR EACH STATEMENT
                EXECUTE FUNCTION reject_goal_version_mutation();

                CREATE TRIGGER goal_versions_no_truncate
                BEFORE TRUNCATE ON goal_versions
                FOR EACH STATEMENT
                EXECUTE FUNCTION reject_goal_version_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_chain_heads");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "goal_drafts");

            migrationBuilder.DropTable(
                name: "goal_versions");

            migrationBuilder.Sql("DROP FUNCTION reject_audit_event_mutation();");
            migrationBuilder.Sql("DROP FUNCTION reject_goal_version_mutation();");
        }
    }
}
