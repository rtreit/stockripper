using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResearchWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_sessions_replay_input",
                table: "sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.CreateTable(
                name: "research_results",
                columns: table => new
                {
                    research_result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    activation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data_snapshot_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    strategy_version = table.Column<string>(type: "jsonb", nullable: false),
                    data_policy_version = table.Column<string>(type: "jsonb", nullable: false),
                    source_observations = table.Column<string>(type: "jsonb", nullable: false),
                    output_metrics = table.Column<string>(type: "jsonb", nullable: false),
                    evidence = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_research_results", x => x.research_result_id);
                    table.CheckConstraint("ck_research_results_content_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_research_results_snapshot_digest", "data_snapshot_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_research_results_time", "completed_at >= started_at");
                    table.ForeignKey(
                        name: "FK_research_results_data_snapshots_data_snapshot_id",
                        column: x => x.data_snapshot_id,
                        principalTable: "data_snapshots",
                        principalColumn: "data_snapshot_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_research_results_goal_activations_activation_id",
                        column: x => x.activation_id,
                        principalTable: "goal_activations",
                        principalColumn: "activation_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_research_results_goal_versions_goal_version_id",
                        column: x => x.goal_version_id,
                        principalTable: "goal_versions",
                        principalColumn: "goal_version_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_research_results_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_sessions_replay_input",
                table: "sessions",
                sql: "(mode = 'REPLAY' AND input_data_snapshot_id IS NOT NULL AND replay_seed IS NOT NULL) OR (mode = 'RESEARCH' AND input_data_snapshot_id IS NOT NULL AND replay_seed IS NULL) OR (mode IN ('PAPER_REVIEW', 'PAPER_AUTO', 'LIVE_REVIEW', 'LIVE_AUTO') AND replay_seed IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'DECISION_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");

            migrationBuilder.CreateIndex(
                name: "IX_research_results_activation_id",
                table: "research_results",
                column: "activation_id");

            migrationBuilder.CreateIndex(
                name: "IX_research_results_data_snapshot_id",
                table: "research_results",
                column: "data_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_research_results_goal_version_id",
                table: "research_results",
                column: "goal_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_research_results_session_id",
                table: "research_results",
                column: "session_id",
                unique: true);

            migrationBuilder.Sql(ProtectionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "research_results");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sessions_replay_input",
                table: "sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sessions_replay_input",
                table: "sessions",
                sql: "(mode = 'REPLAY' AND input_data_snapshot_id IS NOT NULL AND replay_seed IS NOT NULL) OR (mode <> 'REPLAY' AND input_data_snapshot_id IS NULL AND replay_seed IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'DECISION_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS protect_research_result();");
        }

        private const string ProtectionSql = """
            CREATE FUNCTION protect_research_result() RETURNS trigger AS $$
            DECLARE stored_goal_version_id uuid;
            DECLARE stored_activation_id uuid;
            DECLARE stored_snapshot_id uuid;
            DECLARE stored_mode text;
            DECLARE stored_state text;
            BEGIN
                IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
                    RAISE EXCEPTION 'Research result records cannot be removed.';
                END IF;
                IF TG_OP = 'UPDATE' THEN
                    RAISE EXCEPTION 'Research result records are immutable.';
                END IF;
                SELECT goal_version_id, activation_id, input_data_snapshot_id, mode, state
                    INTO stored_goal_version_id, stored_activation_id, stored_snapshot_id,
                         stored_mode, stored_state
                    FROM sessions
                    WHERE session_id = NEW.session_id;
                IF stored_goal_version_id IS DISTINCT FROM NEW.goal_version_id
                    OR stored_activation_id IS DISTINCT FROM NEW.activation_id
                    OR stored_snapshot_id IS DISTINCT FROM NEW.data_snapshot_id
                    OR stored_mode IS DISTINCT FROM 'RESEARCH'
                    OR stored_state IS DISTINCT FROM 'COMPLETED' THEN
                    RAISE EXCEPTION 'Research result binding does not match the completed Session.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_research_result_insert_or_update
                BEFORE INSERT OR UPDATE ON research_results
                FOR EACH ROW EXECUTE FUNCTION protect_research_result();
            CREATE TRIGGER protect_research_result_delete
                BEFORE DELETE ON research_results
                FOR EACH ROW EXECUTE FUNCTION protect_research_result();
            CREATE TRIGGER protect_research_result_truncate
                BEFORE TRUNCATE ON research_results
                FOR EACH STATEMENT EXECUTE FUNCTION protect_research_result();
            """;
    }
}
