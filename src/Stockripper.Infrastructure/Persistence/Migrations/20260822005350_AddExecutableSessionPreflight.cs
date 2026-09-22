using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutableSessionPreflight : Migration
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

            migrationBuilder.AddColumn<string>(
                name: "observed_account_identifier",
                table: "sessions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "observed_endpoint_identity",
                table: "sessions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "observed_environment",
                table: "sessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "executable_session_preflights",
                columns: table => new
                {
                    preflight_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    activation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_connection_version = table.Column<string>(type: "jsonb", nullable: false),
                    observed_environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    observed_endpoint_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    observed_account_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    observed_capabilities = table.Column<string>(type: "jsonb", nullable: false),
                    broker_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    broker_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    capital_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconciliation_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    position_exposure = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: true),
                    open_order_exposure = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: true),
                    buying_power = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: true),
                    checks = table.Column<string>(type: "jsonb", nullable: false),
                    result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_executable_session_preflights", x => x.preflight_id);
                    table.CheckConstraint("ck_executable_session_preflights_capabilities", "jsonb_typeof(observed_capabilities) = 'array'");
                    table.CheckConstraint("ck_executable_session_preflights_checks", "jsonb_typeof(checks) = 'array' AND jsonb_array_length(checks) = 17");
                    table.CheckConstraint("ck_executable_session_preflights_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_executable_session_preflights_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_executable_session_preflights_environment", "observed_environment IS NULL OR observed_environment IN ('PAPER', 'LIVE')");
                    table.CheckConstraint("ck_executable_session_preflights_money", "(position_exposure IS NULL OR position_exposure >= 0) AND (open_order_exposure IS NULL OR open_order_exposure >= 0) AND (buying_power IS NULL OR buying_power >= 0)");
                    table.CheckConstraint("ck_executable_session_preflights_result", "result IN ('PASSED', 'FAILED')");
                    table.ForeignKey(
                        name: "FK_executable_session_preflights_goal_activations_activation_id",
                        column: x => x.activation_id,
                        principalTable: "goal_activations",
                        principalColumn: "activation_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_executable_session_preflights_goal_versions_goal_version_id",
                        column: x => x.goal_version_id,
                        principalTable: "goal_versions",
                        principalColumn: "goal_version_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_executable_session_preflights_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_sessions_broker_observation",
                table: "sessions",
                sql: "(mode IN ('REPLAY', 'RESEARCH') AND observed_environment IS NULL AND observed_endpoint_identity IS NULL AND observed_account_identifier IS NULL) OR (mode IN ('PAPER_REVIEW', 'PAPER_AUTO', 'LIVE_REVIEW', 'LIVE_AUTO') AND ((observed_environment IS NULL AND observed_endpoint_identity IS NULL AND observed_account_identifier IS NULL) OR (observed_environment IN ('PAPER', 'LIVE') AND observed_endpoint_identity IS NOT NULL AND observed_account_identifier IS NOT NULL)) AND (state NOT IN ('READY', 'RUNNING', 'STOPPING', 'RECONCILING', 'COMPLETED') OR observed_environment IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sessions_replay_input",
                table: "sessions",
                sql: "(mode = 'REPLAY' AND input_data_snapshot_id IS NOT NULL AND replay_seed IS NOT NULL) OR (mode <> 'REPLAY' AND input_data_snapshot_id IS NOT NULL AND replay_seed IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'SESSION_PREFLIGHT_COMPLETED', 'DECISION_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");

            migrationBuilder.CreateIndex(
                name: "IX_executable_session_preflights_activation_id",
                table: "executable_session_preflights",
                column: "activation_id");

            migrationBuilder.CreateIndex(
                name: "IX_executable_session_preflights_goal_version_id",
                table: "executable_session_preflights",
                column: "goal_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_executable_session_preflights_session_id",
                table: "executable_session_preflights",
                column: "session_id",
                unique: true);

            migrationBuilder.Sql(ProtectionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "executable_session_preflights");

            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS protect_session_broker_observation_update ON sessions;
                DROP FUNCTION IF EXISTS protect_session_broker_observation();
                DROP FUNCTION IF EXISTS protect_executable_session_preflight();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_sessions_broker_observation",
                table: "sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_sessions_replay_input",
                table: "sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "observed_account_identifier",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "observed_endpoint_identity",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "observed_environment",
                table: "sessions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_sessions_replay_input",
                table: "sessions",
                sql: "(mode = 'REPLAY' AND input_data_snapshot_id IS NOT NULL AND replay_seed IS NOT NULL) OR (mode = 'RESEARCH' AND input_data_snapshot_id IS NOT NULL AND replay_seed IS NULL) OR (mode IN ('PAPER_REVIEW', 'PAPER_AUTO', 'LIVE_REVIEW', 'LIVE_AUTO') AND replay_seed IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'DECISION_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");
        }

        private const string ProtectionSql = """
            CREATE FUNCTION protect_executable_session_preflight() RETURNS trigger AS $$
            DECLARE session_goal_version_id uuid;
            DECLARE session_activation_id uuid;
            DECLARE session_mode text;
            DECLARE session_state text;
            DECLARE activation_state text;
            DECLARE goal_definition jsonb;
            DECLARE has_failed_check boolean;
            BEGIN
                IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
                    RAISE EXCEPTION 'Executable Session preflight records cannot be removed.';
                END IF;
                IF TG_OP = 'UPDATE' THEN
                    RAISE EXCEPTION 'Executable Session preflight records are immutable.';
                END IF;

                SELECT stored_session.goal_version_id, stored_session.activation_id,
                       stored_session.mode, stored_session.state, stored_activation.state,
                       stored_goal.definition_json
                    INTO session_goal_version_id, session_activation_id,
                         session_mode, session_state, activation_state,
                         goal_definition
                    FROM sessions AS stored_session
                    JOIN goal_activations AS stored_activation
                      ON stored_activation.activation_id = stored_session.activation_id
                    JOIN goal_versions AS stored_goal
                      ON stored_goal.goal_version_id = stored_session.goal_version_id
                    WHERE stored_session.session_id = NEW.session_id
                    FOR SHARE OF stored_session, stored_activation, stored_goal;
                IF session_goal_version_id IS DISTINCT FROM NEW.goal_version_id
                    OR session_activation_id IS DISTINCT FROM NEW.activation_id
                    OR session_mode IS DISTINCT FROM 'PAPER_REVIEW'
                    OR session_state IS DISTINCT FROM 'PREFLIGHT'
                    OR goal_definition->'executionBinding'->'brokerConnectionVersion'
                        IS DISTINCT FROM NEW.broker_connection_version THEN
                    RAISE EXCEPTION 'Executable Session preflight binding is invalid.';
                END IF;

                SELECT EXISTS (
                    SELECT 1
                    FROM jsonb_array_elements(NEW.checks) AS item
                    WHERE (item->>'passed')::boolean = false)
                    INTO has_failed_check;
                IF (NEW.result = 'PASSED' AND has_failed_check)
                    OR (NEW.result = 'FAILED' AND NOT has_failed_check) THEN
                    RAISE EXCEPTION 'Executable Session preflight result is inconsistent.';
                END IF;

                IF NEW.result = 'PASSED' THEN
                    IF activation_state IS DISTINCT FROM 'ACTIVE'
                        OR NEW.observed_environment IS DISTINCT FROM 'PAPER'
                        OR NEW.observed_endpoint_identity IS DISTINCT FROM
                            goal_definition->'executionBinding'->>'endpointIdentity'
                        OR NEW.observed_account_identifier IS DISTINCT FROM
                            goal_definition->'executionBinding'->>'expectedAccountIdentifier'
                        OR NOT (NEW.observed_capabilities @>
                            (goal_definition->'executionBinding'->'requiredCapabilities'))
                        OR NOT EXISTS (
                            SELECT 1
                            FROM account_allocation_entries AS allocation
                            WHERE allocation.activation_id = NEW.activation_id
                              AND allocation.goal_version_id = NEW.goal_version_id
                              AND allocation.state = 'ACTIVE'
                              AND allocation.environment = NEW.observed_environment
                              AND allocation.endpoint_identity = NEW.observed_endpoint_identity
                              AND allocation.account_identifier = NEW.observed_account_identifier
                              AND allocation.broker_connection_version =
                                  NEW.broker_connection_version
                            FOR SHARE) THEN
                        RAISE EXCEPTION 'Executable Session preflight authority is invalid.';
                    END IF;
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_executable_session_preflight_insert_or_update
                BEFORE INSERT OR UPDATE ON executable_session_preflights
                FOR EACH ROW EXECUTE FUNCTION protect_executable_session_preflight();
            CREATE TRIGGER protect_executable_session_preflight_delete
                BEFORE DELETE ON executable_session_preflights
                FOR EACH ROW EXECUTE FUNCTION protect_executable_session_preflight();
            CREATE TRIGGER protect_executable_session_preflight_truncate
                BEFORE TRUNCATE ON executable_session_preflights
                FOR EACH STATEMENT EXECUTE FUNCTION protect_executable_session_preflight();

            CREATE FUNCTION protect_session_broker_observation() RETURNS trigger AS $$
            DECLARE evidence_result text;
            DECLARE evidence_environment text;
            DECLARE evidence_endpoint text;
            DECLARE evidence_account text;
            BEGIN
                IF OLD.observed_environment IS NOT NULL
                    AND (NEW.observed_environment IS DISTINCT FROM OLD.observed_environment
                        OR NEW.observed_endpoint_identity IS DISTINCT FROM OLD.observed_endpoint_identity
                        OR NEW.observed_account_identifier IS DISTINCT FROM OLD.observed_account_identifier) THEN
                    RAISE EXCEPTION 'Session Broker observation is immutable.';
                END IF;
                IF OLD.observed_environment IS NULL
                    AND (NEW.observed_environment IS NOT NULL
                        OR NEW.observed_endpoint_identity IS NOT NULL
                        OR NEW.observed_account_identifier IS NOT NULL)
                    AND NOT (OLD.state = 'PREFLIGHT'
                        AND NEW.state IN ('READY', 'FAILED')) THEN
                    RAISE EXCEPTION 'Session Broker observation changed outside preflight.';
                END IF;
                IF OLD.state = 'PREFLIGHT' AND NEW.state IN ('READY', 'FAILED')
                    AND OLD.mode IN ('PAPER_REVIEW', 'PAPER_AUTO', 'LIVE_REVIEW', 'LIVE_AUTO') THEN
                    SELECT result, observed_environment,
                           observed_endpoint_identity, observed_account_identifier
                        INTO evidence_result, evidence_environment,
                             evidence_endpoint, evidence_account
                        FROM executable_session_preflights
                        WHERE session_id = NEW.session_id
                        FOR SHARE;
                    IF evidence_result IS DISTINCT FROM
                            (CASE WHEN NEW.state = 'READY' THEN 'PASSED' ELSE 'FAILED' END)
                        OR evidence_environment IS DISTINCT FROM NEW.observed_environment
                        OR evidence_endpoint IS DISTINCT FROM NEW.observed_endpoint_identity
                        OR evidence_account IS DISTINCT FROM NEW.observed_account_identifier THEN
                        RAISE EXCEPTION 'Session state does not match executable preflight evidence.';
                    END IF;
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_session_broker_observation_update
                BEFORE UPDATE ON sessions
                FOR EACH ROW EXECUTE FUNCTION protect_session_broker_observation();
            """;
    }
}
