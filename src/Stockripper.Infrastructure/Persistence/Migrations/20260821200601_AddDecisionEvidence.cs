using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.CreateTable(
                name: "decisions",
                columns: table => new
                {
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    strategy_version = table.Column<string>(type: "jsonb", nullable: false),
                    input_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    proposed_action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    instrument_identifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: true),
                    limit_price = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: true),
                    explanation = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<decimal>(type: "numeric(18,12)", precision: 18, scale: 12, nullable: true),
                    guard_results = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decisions", x => x.decision_id);
                    table.CheckConstraint("ck_decisions_action", "proposed_action IN ('UNKNOWN', 'NO_ACTION', 'BUY', 'SELL')");
                    table.CheckConstraint("ck_decisions_confidence", "confidence IS NULL OR (confidence >= 0 AND confidence <= 1)");
                    table.CheckConstraint("ck_decisions_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_decisions_state", "state IN ('PROPOSED', 'GUARD_REJECTED', 'INTENT_CREATED', 'NO_ACTION', 'FAILED')");
                    table.ForeignKey(
                        name: "FK_decisions_data_snapshots_input_snapshot_id",
                        column: x => x.input_snapshot_id,
                        principalTable: "data_snapshots",
                        principalColumn: "data_snapshot_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_decisions_goal_versions_goal_version_id",
                        column: x => x.goal_version_id,
                        principalTable: "goal_versions",
                        principalColumn: "goal_version_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_decisions_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'DECISION_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");

            migrationBuilder.CreateIndex(
                name: "IX_decisions_goal_version_id",
                table: "decisions",
                column: "goal_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_decisions_input_snapshot_id",
                table: "decisions",
                column: "input_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_decisions_session_id",
                table: "decisions",
                column: "session_id");

            migrationBuilder.Sql(ProtectionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "decisions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS protect_decision_record();");
        }

        private const string ProtectionSql = """
            CREATE FUNCTION protect_decision_record() RETURNS trigger AS $$
            DECLARE session_goal_version_id uuid;
            DECLARE session_snapshot_id uuid;
            BEGIN
                IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
                    RAISE EXCEPTION 'Decision records cannot be removed.';
                END IF;
                IF TG_OP = 'UPDATE' THEN
                    RAISE EXCEPTION 'Decision records are immutable.';
                END IF;
                SELECT goal_version_id, input_data_snapshot_id
                    INTO session_goal_version_id, session_snapshot_id
                    FROM sessions
                    WHERE session_id = NEW.session_id;
                IF session_goal_version_id IS DISTINCT FROM NEW.goal_version_id
                    OR session_snapshot_id IS DISTINCT FROM NEW.input_snapshot_id THEN
                    RAISE EXCEPTION 'Decision binding does not match the Session.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_decision_insert_or_update
                BEFORE INSERT OR UPDATE ON decisions
                FOR EACH ROW EXECUTE FUNCTION protect_decision_record();
            CREATE TRIGGER protect_decision_delete
                BEFORE DELETE ON decisions
                FOR EACH ROW EXECUTE FUNCTION protect_decision_record();
            CREATE TRIGGER protect_decision_truncate
                BEFORE TRUNCATE ON decisions
                FOR EACH STATEMENT EXECUTE FUNCTION protect_decision_record();
            """;
    }
}
