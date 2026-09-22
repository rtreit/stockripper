using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperReviewIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.CreateTable(
                name: "risk_evaluations",
                columns: table => new
                {
                    risk_evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    risk_policy_version = table.Column<string>(type: "jsonb", nullable: false),
                    allocation_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position_exposure = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    open_order_exposure = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    requested_exposure = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    buying_power = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    checks = table.Column<string>(type: "jsonb", nullable: false),
                    result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_risk_evaluations", x => x.risk_evaluation_id);
                    table.CheckConstraint("ck_risk_evaluations_checks", "jsonb_typeof(checks) = 'array' AND jsonb_array_length(checks) = 13");
                    table.CheckConstraint("ck_risk_evaluations_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_risk_evaluations_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_risk_evaluations_money", "position_exposure >= 0 AND open_order_exposure >= 0 AND requested_exposure > 0 AND buying_power >= 0");
                    table.CheckConstraint("ck_risk_evaluations_result", "result IN ('ACCEPTED', 'REJECTED')");
                    table.ForeignKey(
                        name: "FK_risk_evaluations_account_allocation_entries_allocation_entr~",
                        column: x => x.allocation_entry_id,
                        principalTable: "account_allocation_entries",
                        principalColumn: "allocation_entry_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_risk_evaluations_decisions_decision_id",
                        column: x => x.decision_id,
                        principalTable: "decisions",
                        principalColumn: "decision_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_risk_evaluations_goal_versions_goal_version_id",
                        column: x => x.goal_version_id,
                        principalTable: "goal_versions",
                        principalColumn: "goal_version_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_risk_evaluations_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "execution_intents",
                columns: table => new
                {
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    broker_connection_version = table.Column<string>(type: "jsonb", nullable: false),
                    environment = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    endpoint_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    instrument_identifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    order_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    limit_price = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    time_in_force = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    risk_evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_intents", x => x.intent_id);
                    table.CheckConstraint("ck_execution_intents_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_execution_intents_environment", "environment = 'PAPER'");
                    table.CheckConstraint("ck_execution_intents_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_execution_intents_mode", "mode = 'PAPER_REVIEW'");
                    table.CheckConstraint("ck_execution_intents_money", "quantity > 0 AND limit_price > 0");
                    table.CheckConstraint("ck_execution_intents_order", "order_type = 'LIMIT' AND time_in_force = 'DAY'");
                    table.CheckConstraint("ck_execution_intents_side", "side IN ('BUY', 'SELL')");
                    table.CheckConstraint("ck_execution_intents_state", "state IN ('CREATED', 'RISK_ACCEPTED', 'RISK_REJECTED', 'REVIEW_PENDING', 'APPROVED', 'REJECTED', 'EXPIRED', 'SUBMISSION_PENDING', 'SUBMITTED', 'SUBMISSION_UNKNOWN', 'CANCELED', 'FAILED')");
                    table.ForeignKey(
                        name: "FK_execution_intents_decisions_decision_id",
                        column: x => x.decision_id,
                        principalTable: "decisions",
                        principalColumn: "decision_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_execution_intents_goal_versions_goal_version_id",
                        column: x => x.goal_version_id,
                        principalTable: "goal_versions",
                        principalColumn: "goal_version_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_execution_intents_risk_evaluations_risk_evaluation_id",
                        column: x => x.risk_evaluation_id,
                        principalTable: "risk_evaluations",
                        principalColumn: "risk_evaluation_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_execution_intents_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_approvals",
                columns: table => new
                {
                    approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    action_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    intent_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operator_note = table.Column<string>(type: "text", nullable: true),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_approvals", x => x.approval_id);
                    table.CheckConstraint("ck_review_approvals_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_review_approvals_expiry", "expires_at > action_time");
                    table.CheckConstraint("ck_review_approvals_intent_digest", "intent_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_review_approvals_result", "result IN ('APPROVED', 'REJECTED')");
                    table.ForeignKey(
                        name: "FK_review_approvals_execution_intents_intent_id",
                        column: x => x.intent_id,
                        principalTable: "execution_intents",
                        principalColumn: "intent_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'SESSION_PREFLIGHT_COMPLETED', 'DECISION_RECORDED', 'RISK_EVALUATED', 'EXECUTION_INTENT_CREATED', 'EXECUTION_INTENT_STATE_CHANGED', 'REVIEW_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");

            migrationBuilder.CreateIndex(
                name: "IX_execution_intents_decision_id",
                table: "execution_intents",
                column: "decision_id");

            migrationBuilder.CreateIndex(
                name: "IX_execution_intents_goal_version_id",
                table: "execution_intents",
                column: "goal_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_execution_intents_idempotency_key",
                table: "execution_intents",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_execution_intents_risk_evaluation_id",
                table: "execution_intents",
                column: "risk_evaluation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_execution_intents_session_id",
                table: "execution_intents",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_review_approvals_intent_id",
                table: "review_approvals",
                column: "intent_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_risk_evaluations_allocation_entry_id",
                table: "risk_evaluations",
                column: "allocation_entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_risk_evaluations_decision_id",
                table: "risk_evaluations",
                column: "decision_id");

            migrationBuilder.CreateIndex(
                name: "IX_risk_evaluations_goal_version_id",
                table: "risk_evaluations",
                column: "goal_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_risk_evaluations_intent_id",
                table: "risk_evaluations",
                column: "intent_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_risk_evaluations_session_id",
                table: "risk_evaluations",
                column: "session_id");

            migrationBuilder.Sql(ProtectionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TeardownSql);

            migrationBuilder.DropTable(
                name: "review_approvals");

            migrationBuilder.DropTable(
                name: "execution_intents");

            migrationBuilder.DropTable(
                name: "risk_evaluations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'SESSION_PREFLIGHT_COMPLETED', 'DECISION_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");
        }

        private const string TeardownSql = """
            DROP TRIGGER IF EXISTS protect_review_approval_write ON review_approvals;
            DROP TRIGGER IF EXISTS protect_review_approval_delete ON review_approvals;
            DROP TRIGGER IF EXISTS protect_review_approval_truncate ON review_approvals;
            DROP FUNCTION IF EXISTS protect_review_approval();
            DROP TRIGGER IF EXISTS protect_execution_intent_write ON execution_intents;
            DROP TRIGGER IF EXISTS protect_execution_intent_delete ON execution_intents;
            DROP TRIGGER IF EXISTS protect_execution_intent_truncate ON execution_intents;
            DROP FUNCTION IF EXISTS protect_execution_intent();
            DROP TRIGGER IF EXISTS protect_risk_evaluation_write ON risk_evaluations;
            DROP TRIGGER IF EXISTS protect_risk_evaluation_delete ON risk_evaluations;
            DROP TRIGGER IF EXISTS protect_risk_evaluation_truncate ON risk_evaluations;
            DROP FUNCTION IF EXISTS protect_risk_evaluation();
            """;

        private const string ProtectionSql = """
            CREATE FUNCTION protect_risk_evaluation() RETURNS trigger AS $$
            DECLARE has_failed_check boolean;
            DECLARE valid_check_count integer;
            BEGIN
                IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
                    RAISE EXCEPTION 'Risk evaluation records cannot be removed.';
                END IF;
                IF TG_OP = 'UPDATE' THEN
                    RAISE EXCEPTION 'Risk evaluation records are immutable.';
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM sessions AS stored_session
                    JOIN decisions AS decision
                      ON decision.decision_id = NEW.decision_id
                     AND decision.session_id = stored_session.session_id
                     AND decision.goal_version_id = stored_session.goal_version_id
                    JOIN goal_versions AS goal
                      ON goal.goal_version_id = stored_session.goal_version_id
                    JOIN goal_activations AS activation
                      ON activation.activation_id = stored_session.activation_id
                    JOIN account_allocation_entries AS allocation
                      ON allocation.allocation_entry_id = NEW.allocation_entry_id
                     AND allocation.activation_id = stored_session.activation_id
                     AND allocation.goal_version_id = stored_session.goal_version_id
                    JOIN executable_session_preflights AS preflight
                      ON preflight.session_id = stored_session.session_id
                    WHERE stored_session.session_id = NEW.session_id
                      AND stored_session.state = 'RUNNING'
                      AND stored_session.mode = 'PAPER_REVIEW'
                      AND decision.state = 'PROPOSED'
                      AND decision.proposed_action IN ('BUY', 'SELL')
                      AND goal.mode = 'PAPER_REVIEW'
                      AND activation.goal_version_id = NEW.goal_version_id
                      AND activation.state = 'ACTIVE'
                      AND (activation.expiry_time IS NULL
                        OR NEW.evaluated_at < activation.expiry_time)
                      AND goal.definition_json->'riskPolicyVersion' = NEW.risk_policy_version
                      AND allocation.state = 'ACTIVE'
                      AND allocation.environment = 'PAPER'
                      AND allocation.endpoint_identity =
                          goal.definition_json->'executionBinding'->>'endpointIdentity'
                      AND allocation.account_identifier =
                          goal.definition_json->'executionBinding'->>'expectedAccountIdentifier'
                      AND allocation.broker_connection_version =
                          goal.definition_json->'executionBinding'->'brokerConnectionVersion'
                      AND preflight.result = 'PASSED'
                      AND preflight.goal_version_id = NEW.goal_version_id
                      AND preflight.activation_id = stored_session.activation_id
                    FOR SHARE OF stored_session, decision, goal, activation, allocation, preflight) THEN
                    RAISE EXCEPTION 'Risk evaluation authority is invalid.';
                END IF;

                SELECT EXISTS (
                    SELECT 1
                    FROM jsonb_array_elements(NEW.checks) AS item
                    WHERE (item->>'passed')::boolean = false)
                    INTO has_failed_check;
                SELECT count(DISTINCT item->>'code')
                    INTO valid_check_count
                    FROM jsonb_array_elements(NEW.checks) AS item
                    WHERE item->>'code' IN (
                        'AUTHORITY', 'PREFLIGHT', 'RISK_POLICY', 'EXECUTION_SCHEDULE',
                        'KILL_SWITCH', 'RECONCILIATION', 'ACCOUNT_BINDING', 'INSTRUMENT',
                        'ORDER_TYPE', 'PRICE_PROTECTION', 'CAPITAL_ALLOCATION',
                        'BUYING_POWER', 'DATA_FRESHNESS');
                IF valid_check_count <> 13
                    OR (NEW.result = 'ACCEPTED' AND has_failed_check)
                    OR (NEW.result = 'REJECTED' AND NOT has_failed_check) THEN
                    RAISE EXCEPTION 'Risk evaluation results are inconsistent.';
                END IF;
                IF NEW.result = 'ACCEPTED'
                    AND (NEW.requested_exposure > NEW.buying_power
                        OR NOT EXISTS (
                            SELECT 1
                            FROM account_allocation_entries AS allocation
                            WHERE allocation.allocation_entry_id = NEW.allocation_entry_id
                              AND NEW.position_exposure + NEW.open_order_exposure
                                  + NEW.requested_exposure <= allocation.allocation_amount
                            FOR SHARE)) THEN
                    RAISE EXCEPTION 'Accepted risk exceeds current capital authority.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_risk_evaluation_write
                BEFORE INSERT OR UPDATE ON risk_evaluations
                FOR EACH ROW EXECUTE FUNCTION protect_risk_evaluation();
            CREATE TRIGGER protect_risk_evaluation_delete
                BEFORE DELETE ON risk_evaluations
                FOR EACH ROW EXECUTE FUNCTION protect_risk_evaluation();
            CREATE TRIGGER protect_risk_evaluation_truncate
                BEFORE TRUNCATE ON risk_evaluations
                FOR EACH STATEMENT EXECUTE FUNCTION protect_risk_evaluation();

            CREATE FUNCTION protect_execution_intent() RETURNS trigger AS $$
            DECLARE review_result text;
            DECLARE review_digest text;
            BEGIN
                IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
                    RAISE EXCEPTION 'Execution intent records cannot be removed.';
                END IF;
                IF TG_OP = 'UPDATE' THEN
                    IF NEW.intent_id IS DISTINCT FROM OLD.intent_id
                        OR NEW.decision_id IS DISTINCT FROM OLD.decision_id
                        OR NEW.goal_version_id IS DISTINCT FROM OLD.goal_version_id
                        OR NEW.session_id IS DISTINCT FROM OLD.session_id
                        OR NEW.mode IS DISTINCT FROM OLD.mode
                        OR NEW.broker_connection_version IS DISTINCT FROM OLD.broker_connection_version
                        OR NEW.environment IS DISTINCT FROM OLD.environment
                        OR NEW.endpoint_identity IS DISTINCT FROM OLD.endpoint_identity
                        OR NEW.account_identifier IS DISTINCT FROM OLD.account_identifier
                        OR NEW.instrument_identifier IS DISTINCT FROM OLD.instrument_identifier
                        OR NEW.side IS DISTINCT FROM OLD.side
                        OR NEW.order_type IS DISTINCT FROM OLD.order_type
                        OR NEW.quantity IS DISTINCT FROM OLD.quantity
                        OR NEW.limit_price IS DISTINCT FROM OLD.limit_price
                        OR NEW.time_in_force IS DISTINCT FROM OLD.time_in_force
                        OR NEW.expires_at IS DISTINCT FROM OLD.expires_at
                        OR NEW.risk_evaluation_id IS DISTINCT FROM OLD.risk_evaluation_id
                        OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                        OR NEW.created_at IS DISTINCT FROM OLD.created_at
                        OR NEW.content_digest IS DISTINCT FROM OLD.content_digest
                        OR OLD.state IS DISTINCT FROM 'REVIEW_PENDING'
                        OR NEW.state NOT IN ('APPROVED', 'REJECTED') THEN
                        RAISE EXCEPTION 'Execution intent immutable content changed.';
                    END IF;
                    SELECT result, intent_digest
                        INTO review_result, review_digest
                        FROM review_approvals
                        WHERE intent_id = NEW.intent_id
                        FOR SHARE;
                    IF review_result IS DISTINCT FROM NEW.state
                        OR review_digest IS DISTINCT FROM NEW.content_digest THEN
                        RAISE EXCEPTION 'Execution intent state lacks exact review evidence.';
                    END IF;
                    RETURN NEW;
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM risk_evaluations AS risk
                    JOIN decisions AS decision ON decision.decision_id = NEW.decision_id
                    JOIN sessions AS stored_session ON stored_session.session_id = NEW.session_id
                    JOIN goal_versions AS goal ON goal.goal_version_id = NEW.goal_version_id
                    WHERE risk.risk_evaluation_id = NEW.risk_evaluation_id
                      AND risk.intent_id = NEW.intent_id
                      AND risk.decision_id = NEW.decision_id
                      AND risk.goal_version_id = NEW.goal_version_id
                      AND risk.session_id = NEW.session_id
                      AND decision.session_id = NEW.session_id
                      AND decision.goal_version_id = NEW.goal_version_id
                      AND stored_session.state = 'RUNNING'
                      AND stored_session.mode = 'PAPER_REVIEW'
                      AND NEW.environment = 'PAPER'
                      AND NEW.endpoint_identity =
                          goal.definition_json->'executionBinding'->>'endpointIdentity'
                      AND NEW.account_identifier =
                          goal.definition_json->'executionBinding'->>'expectedAccountIdentifier'
                      AND NEW.broker_connection_version =
                          goal.definition_json->'executionBinding'->'brokerConnectionVersion'
                      AND risk.requested_exposure = NEW.quantity * NEW.limit_price
                      AND ((risk.result = 'ACCEPTED' AND NEW.state = 'REVIEW_PENDING')
                        OR (risk.result = 'REJECTED' AND NEW.state = 'RISK_REJECTED'))
                    FOR SHARE OF risk, decision, stored_session, goal) THEN
                    RAISE EXCEPTION 'Execution intent binding is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_execution_intent_write
                BEFORE INSERT OR UPDATE ON execution_intents
                FOR EACH ROW EXECUTE FUNCTION protect_execution_intent();
            CREATE TRIGGER protect_execution_intent_delete
                BEFORE DELETE ON execution_intents
                FOR EACH ROW EXECUTE FUNCTION protect_execution_intent();
            CREATE TRIGGER protect_execution_intent_truncate
                BEFORE TRUNCATE ON execution_intents
                FOR EACH STATEMENT EXECUTE FUNCTION protect_execution_intent();

            CREATE FUNCTION protect_review_approval() RETURNS trigger AS $$
            BEGIN
                IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
                    RAISE EXCEPTION 'Review approval records cannot be removed.';
                END IF;
                IF TG_OP = 'UPDATE' THEN
                    RAISE EXCEPTION 'Review approval records are immutable.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM execution_intents AS intent
                    JOIN sessions AS stored_session ON stored_session.session_id = intent.session_id
                    JOIN goal_activations AS activation
                      ON activation.activation_id = stored_session.activation_id
                    JOIN account_allocation_entries AS allocation
                      ON allocation.activation_id = activation.activation_id
                    JOIN executable_session_preflights AS preflight
                      ON preflight.session_id = stored_session.session_id
                    WHERE intent.intent_id = NEW.intent_id
                      AND intent.state = 'REVIEW_PENDING'
                      AND intent.content_digest = NEW.intent_digest
                      AND NEW.action_time < intent.expires_at
                      AND NEW.expires_at <= intent.expires_at
                      AND (NEW.result = 'REJECTED'
                        OR (NEW.result = 'APPROVED'
                          AND stored_session.state = 'RUNNING'
                          AND stored_session.mode = 'PAPER_REVIEW'
                          AND activation.state = 'ACTIVE'
                          AND (activation.expiry_time IS NULL
                            OR NEW.action_time < activation.expiry_time)
                          AND allocation.state = 'ACTIVE'
                          AND allocation.goal_version_id = intent.goal_version_id
                          AND allocation.environment = 'PAPER'
                          AND allocation.endpoint_identity = intent.endpoint_identity
                          AND allocation.account_identifier = intent.account_identifier
                          AND allocation.broker_connection_version = intent.broker_connection_version
                          AND preflight.result = 'PASSED'))
                    FOR SHARE OF intent, stored_session, activation, allocation, preflight) THEN
                    RAISE EXCEPTION 'Review approval authority is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_review_approval_write
                BEFORE INSERT OR UPDATE ON review_approvals
                FOR EACH ROW EXECUTE FUNCTION protect_review_approval();
            CREATE TRIGGER protect_review_approval_delete
                BEFORE DELETE ON review_approvals
                FOR EACH ROW EXECUTE FUNCTION protect_review_approval();
            CREATE TRIGGER protect_review_approval_truncate
                BEFORE TRUNCATE ON review_approvals
                FOR EACH STATEMENT EXECUTE FUNCTION protect_review_approval();
            """;
    }
}
