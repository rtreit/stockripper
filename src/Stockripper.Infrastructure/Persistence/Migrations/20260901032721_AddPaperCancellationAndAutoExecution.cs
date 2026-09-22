using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperCancellationAndAutoExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_intents_mode",
                table: "execution_intents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.AlterColumn<Guid>(
                name: "approval_id",
                table: "pre_submit_evaluations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "cancellation_requests",
                columns: table => new
                {
                    cancellation_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_connection_version = table.Column<string>(type: "jsonb", nullable: false),
                    environment = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    endpoint_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    broker_client_order_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    broker_order_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    broker_request_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    resolution_event_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    prepared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cancellation_requests", x => x.cancellation_request_id);
                    table.CheckConstraint("ck_cancellation_requests_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_cancellation_requests_environment", "environment = 'PAPER'");
                    table.CheckConstraint("ck_cancellation_requests_lifecycle", "(state IN ('PREPARED', 'RECONCILIATION_REQUIRED') AND broker_request_reference IS NULL AND resolution_event_reference IS NULL) OR (state = 'REQUESTED' AND broker_request_reference IS NOT NULL AND resolution_event_reference IS NULL) OR (state IN ('CONFIRMED', 'SUPERSEDED') AND resolution_event_reference IS NOT NULL)");
                    table.CheckConstraint("ck_cancellation_requests_state", "state IN ('PREPARED', 'REQUESTED', 'RECONCILIATION_REQUIRED', 'CONFIRMED', 'SUPERSEDED')");
                    table.ForeignKey(
                        name: "FK_cancellation_requests_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_intents_mode",
                table: "execution_intents",
                sql: "mode IN ('PAPER_REVIEW', 'PAPER_AUTO')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'SESSION_PREFLIGHT_COMPLETED', 'DECISION_RECORDED', 'RISK_EVALUATED', 'EXECUTION_INTENT_CREATED', 'EXECUTION_INTENT_STATE_CHANGED', 'REVIEW_RECORDED', 'KILL_SWITCH_CONFIGURED', 'KILL_SWITCH_STATE_CHANGED', 'RECONCILIATION_COMPLETED', 'EXPOSURE_RESERVED', 'EXPOSURE_RELEASED', 'EXPOSURE_ADJUSTED', 'PRE_SUBMIT_EVALUATED', 'ORDER_PREPARED', 'ORDER_STATE_CHANGED', 'CANCELLATION_PREPARED', 'CANCELLATION_STATE_CHANGED', 'BROKER_ORDER_EVENT_RECORDED', 'BROKER_EXECUTION_EVENT_RECORDED', 'FILL_RECORDED', 'POSITION_SNAPSHOT_RECORDED', 'POST_FILL_EVALUATED', 'POST_FILL_SAFETY_RESPONSE_CREATED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");

            migrationBuilder.CreateIndex(
                name: "IX_cancellation_requests_idempotency_key",
                table: "cancellation_requests",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cancellation_requests_order_id",
                table: "cancellation_requests",
                column: "order_id",
                unique: true);

            migrationBuilder.Sql(ProtectionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DropProtectionSql);

            migrationBuilder.DropTable(
                name: "cancellation_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execution_intents_mode",
                table: "execution_intents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.AlterColumn<Guid>(
                name: "approval_id",
                table: "pre_submit_evaluations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_intents_mode",
                table: "execution_intents",
                sql: "mode = 'PAPER_REVIEW'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'SESSION_PREFLIGHT_COMPLETED', 'DECISION_RECORDED', 'RISK_EVALUATED', 'EXECUTION_INTENT_CREATED', 'EXECUTION_INTENT_STATE_CHANGED', 'REVIEW_RECORDED', 'KILL_SWITCH_CONFIGURED', 'KILL_SWITCH_STATE_CHANGED', 'RECONCILIATION_COMPLETED', 'EXPOSURE_RESERVED', 'EXPOSURE_RELEASED', 'EXPOSURE_ADJUSTED', 'PRE_SUBMIT_EVALUATED', 'ORDER_PREPARED', 'ORDER_STATE_CHANGED', 'BROKER_ORDER_EVENT_RECORDED', 'BROKER_EXECUTION_EVENT_RECORDED', 'FILL_RECORDED', 'POSITION_SNAPSHOT_RECORDED', 'POST_FILL_EVALUATED', 'POST_FILL_SAFETY_RESPONSE_CREATED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");
        }

        private const string ProtectionSql = """
            CREATE FUNCTION protect_cancellation_request() RETURNS trigger AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Cancellation requests cannot be removed.';
                END IF;
                IF TG_OP = 'INSERT' THEN
                    IF NEW.state <> 'PREPARED'
                        OR NEW.last_updated_at <> NEW.prepared_at
                        OR NEW.idempotency_key <> 'stockripper-cancel:' || NEW.order_id::text
                        OR NOT EXISTS (
                            SELECT 1 FROM orders AS order_record
                            WHERE order_record.order_id = NEW.order_id
                              AND order_record.state = 'CANCEL_PENDING'
                              AND order_record.environment = NEW.environment
                              AND order_record.broker_connection_version = NEW.broker_connection_version
                              AND order_record.endpoint_identity = NEW.endpoint_identity
                              AND order_record.account_identifier = NEW.account_identifier
                              AND order_record.broker_client_order_id = NEW.broker_client_order_id
                              AND order_record.broker_order_id = NEW.broker_order_id
                            FOR SHARE OF order_record) THEN
                        RAISE EXCEPTION 'Cancellation request authority is invalid.';
                    END IF;
                    RETURN NEW;
                END IF;
                IF NEW.cancellation_request_id IS DISTINCT FROM OLD.cancellation_request_id
                    OR NEW.order_id IS DISTINCT FROM OLD.order_id
                    OR NEW.broker_connection_version IS DISTINCT FROM OLD.broker_connection_version
                    OR NEW.environment IS DISTINCT FROM OLD.environment
                    OR NEW.endpoint_identity IS DISTINCT FROM OLD.endpoint_identity
                    OR NEW.account_identifier IS DISTINCT FROM OLD.account_identifier
                    OR NEW.broker_client_order_id IS DISTINCT FROM OLD.broker_client_order_id
                    OR NEW.broker_order_id IS DISTINCT FROM OLD.broker_order_id
                    OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                    OR NEW.reason IS DISTINCT FROM OLD.reason
                    OR NEW.prepared_at IS DISTINCT FROM OLD.prepared_at
                    OR NEW.content_digest IS DISTINCT FROM OLD.content_digest
                    OR NEW.last_updated_at < OLD.last_updated_at
                    OR NOT ((OLD.state = 'PREPARED' AND NEW.state IN (
                            'REQUESTED', 'RECONCILIATION_REQUIRED', 'CONFIRMED', 'SUPERSEDED'))
                        OR (OLD.state IN ('REQUESTED', 'RECONCILIATION_REQUIRED')
                            AND NEW.state IN ('CONFIRMED', 'SUPERSEDED'))) THEN
                    RAISE EXCEPTION 'Cancellation request transition is invalid.';
                END IF;
                IF NEW.state IN ('CONFIRMED', 'SUPERSEDED') AND NOT EXISTS (
                    SELECT 1 FROM broker_execution_events AS event
                    WHERE event.order_id = NEW.order_id
                      AND event.event_reference = NEW.resolution_event_reference
                      AND ((NEW.state = 'CONFIRMED' AND event.kind = 'CANCELED')
                        OR (NEW.state = 'SUPERSEDED'
                            AND event.kind IN ('FILLED', 'REJECTED', 'EXPIRED')))
                    FOR SHARE OF event) THEN
                    RAISE EXCEPTION 'Cancellation resolution lacks Broker truth.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_cancellation_request_write
                BEFORE INSERT OR UPDATE ON cancellation_requests
                FOR EACH ROW EXECUTE FUNCTION protect_cancellation_request();
            CREATE TRIGGER protect_cancellation_request_delete
                BEFORE DELETE ON cancellation_requests
                FOR EACH ROW EXECUTE FUNCTION protect_cancellation_request();

            CREATE FUNCTION ensure_cancel_pending_request() RETURNS trigger AS $$
            BEGIN
                IF NEW.state = 'CANCEL_PENDING' AND NOT EXISTS (
                    SELECT 1 FROM cancellation_requests WHERE order_id = NEW.order_id) THEN
                    RAISE EXCEPTION 'Cancel-pending Order lacks a cancellation request.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE CONSTRAINT TRIGGER ensure_cancel_pending_request
                AFTER INSERT OR UPDATE OF state ON orders
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION ensure_cancel_pending_request();

            CREATE OR REPLACE FUNCTION protect_pre_submit_evaluation() RETURNS trigger AS $$
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Pre-submit evaluations are append-only.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM execution_intents AS intent
                    JOIN exposure_reservations AS reservation
                      ON reservation.reservation_id = NEW.reservation_id
                    JOIN reconciliations AS reconciliation
                      ON reconciliation.reconciliation_id = NEW.reconciliation_id
                    WHERE intent.intent_id = NEW.intent_id
                      AND intent.content_digest = NEW.intent_digest
                      AND intent.environment = 'PAPER'
                      AND ((intent.mode = 'PAPER_AUTO'
                            AND intent.state = 'RISK_ACCEPTED'
                            AND NEW.approval_id IS NULL)
                        OR (intent.mode = 'PAPER_REVIEW'
                            AND intent.state = 'APPROVED'
                            AND NEW.approval_id IS NOT NULL
                            AND EXISTS (
                                SELECT 1 FROM review_approvals AS review
                                WHERE review.approval_id = NEW.approval_id
                                  AND review.intent_id = intent.intent_id
                                  AND review.result = 'APPROVED'
                                  AND review.intent_digest = intent.content_digest
                                  AND NEW.evaluated_at < review.expires_at
                                FOR SHARE OF review)))
                      AND reservation.intent_id = intent.intent_id
                      AND reservation.state = 'RESERVED'
                      AND reservation.amount = intent.quantity * intent.limit_price
                      AND reconciliation.goal_version_id = intent.goal_version_id
                      AND (NEW.result = 'FAILED' OR (
                          NEW.evaluated_at < intent.expires_at
                          AND reconciliation.result = 'CURRENT'
                          AND reconciliation.position_exposure = NEW.position_exposure
                          AND reconciliation.open_order_exposure = NEW.open_order_exposure
                          AND reconciliation.buying_power = NEW.buying_power
                          AND reconciliation.currency = NEW.currency
                          AND 4 = (SELECT count(*) FROM kill_switches
                              WHERE state = 'CLEAR' AND (
                                (scope_type = 'SYSTEM'
                                  AND scope_identifier = intent.environment)
                                OR (scope_type = 'CONNECTION'
                                  AND scope_identifier = intent.broker_connection_version->>'digest')
                                OR (scope_type = 'ACCOUNT'
                                  AND scope_identifier = intent.account_identifier)
                                OR (scope_type = 'GOAL'
                                  AND scope_identifier = intent.goal_version_id::text)))))
                    FOR SHARE OF intent, reservation, reconciliation) THEN
                    RAISE EXCEPTION 'Pre-submit evaluation authority is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE OR REPLACE FUNCTION protect_order() RETURNS trigger AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Order records cannot be removed.';
                END IF;
                IF TG_OP = 'INSERT' THEN
                    IF NEW.state <> 'SUBMITTING' OR NEW.filled_quantity <> 0 OR NOT EXISTS (
                        SELECT 1 FROM execution_intents AS intent
                        JOIN pre_submit_evaluations AS evaluation
                          ON evaluation.evaluation_id = NEW.evaluation_id
                        WHERE intent.intent_id = NEW.intent_id
                          AND ((intent.mode = 'PAPER_REVIEW'
                                AND intent.state = 'APPROVED'
                                AND evaluation.approval_id IS NOT NULL)
                            OR (intent.mode = 'PAPER_AUTO'
                                AND intent.state = 'RISK_ACCEPTED'
                                AND evaluation.approval_id IS NULL))
                          AND intent.content_digest = NEW.intent_digest
                          AND intent.environment = NEW.environment
                          AND intent.broker_connection_version = NEW.broker_connection_version
                          AND intent.endpoint_identity = NEW.endpoint_identity
                          AND intent.account_identifier = NEW.account_identifier
                          AND intent.instrument_identifier = NEW.instrument_identifier
                          AND intent.side = NEW.side AND intent.order_type = NEW.order_type
                          AND intent.quantity = NEW.quantity
                          AND intent.limit_price = NEW.limit_price
                          AND intent.time_in_force = NEW.time_in_force
                          AND evaluation.intent_id = intent.intent_id
                          AND evaluation.intent_digest = intent.content_digest
                          AND evaluation.result = 'PASSED'
                        FOR SHARE OF intent, evaluation) THEN
                        RAISE EXCEPTION 'Prepared Order authority is invalid.';
                    END IF;
                    RETURN NEW;
                END IF;
                IF NEW.order_id IS DISTINCT FROM OLD.order_id
                    OR NEW.intent_id IS DISTINCT FROM OLD.intent_id
                    OR NEW.intent_digest IS DISTINCT FROM OLD.intent_digest
                    OR NEW.evaluation_id IS DISTINCT FROM OLD.evaluation_id
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
                    OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                    OR NEW.broker_client_order_id IS DISTINCT FROM OLD.broker_client_order_id
                    OR NEW.first_submission_time IS DISTINCT FROM OLD.first_submission_time
                    OR NEW.filled_quantity < OLD.filled_quantity
                    OR (OLD.broker_order_id IS NOT NULL
                        AND NEW.broker_order_id IS DISTINCT FROM OLD.broker_order_id)
                    OR NOT ((OLD.state = 'SUBMITTING' AND NEW.state IN (
                            'ACKNOWLEDGED', 'REJECTED', 'SUBMISSION_UNKNOWN',
                            'PARTIALLY_FILLED', 'FILLED', 'CANCELED', 'EXPIRED'))
                        OR (OLD.state = 'SUBMISSION_UNKNOWN' AND NEW.state IN (
                            'ACKNOWLEDGED', 'RECONCILIATION_REQUIRED',
                            'PARTIALLY_FILLED', 'FILLED', 'CANCELED', 'REJECTED', 'EXPIRED'))
                        OR (OLD.state = 'ACKNOWLEDGED' AND NEW.state IN (
                            'CANCEL_PENDING', 'PARTIALLY_FILLED', 'FILLED',
                            'CANCELED', 'REJECTED', 'EXPIRED'))
                        OR (OLD.state = 'PARTIALLY_FILLED' AND NEW.state IN (
                            'CANCEL_PENDING', 'PARTIALLY_FILLED', 'FILLED',
                            'CANCELED', 'REJECTED', 'EXPIRED'))
                        OR (OLD.state = 'CANCEL_PENDING' AND NEW.state IN (
                            'PARTIALLY_FILLED', 'FILLED', 'CANCELED',
                            'REJECTED', 'EXPIRED'))) THEN
                    RAISE EXCEPTION 'Order transition is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE OR REPLACE FUNCTION protect_execution_intent() RETURNS trigger AS $$
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
                        OR NEW.content_digest IS DISTINCT FROM OLD.content_digest THEN
                        RAISE EXCEPTION 'Execution intent immutable content changed.';
                    END IF;
                    IF OLD.state = 'REVIEW_PENDING'
                        AND NEW.state IN ('APPROVED', 'REJECTED') THEN
                        SELECT result, intent_digest INTO review_result, review_digest
                        FROM review_approvals WHERE intent_id = NEW.intent_id FOR SHARE;
                        IF review_result IS DISTINCT FROM NEW.state
                            OR review_digest IS DISTINCT FROM NEW.content_digest THEN
                            RAISE EXCEPTION 'Execution intent state lacks review evidence.';
                        END IF;
                    ELSIF ((OLD.state = 'APPROVED' AND OLD.mode = 'PAPER_REVIEW')
                        OR (OLD.state = 'RISK_ACCEPTED' AND OLD.mode = 'PAPER_AUTO'))
                        AND NEW.state = 'SUBMISSION_PENDING' THEN
                        IF NOT EXISTS (
                            SELECT 1 FROM orders
                            WHERE intent_id = NEW.intent_id AND state = 'SUBMITTING'
                            FOR SHARE) THEN
                            RAISE EXCEPTION 'Execution intent state lacks prepared Order evidence.';
                        END IF;
                    ELSIF OLD.state IN ('SUBMISSION_PENDING', 'SUBMISSION_UNKNOWN')
                        AND ((NEW.state = 'SUBMITTED' AND EXISTS (
                                SELECT 1 FROM orders WHERE intent_id = NEW.intent_id
                                  AND state = 'ACKNOWLEDGED' FOR SHARE))
                          OR (OLD.state = 'SUBMISSION_PENDING'
                            AND NEW.state = 'SUBMISSION_UNKNOWN' AND EXISTS (
                                SELECT 1 FROM orders WHERE intent_id = NEW.intent_id
                                  AND state = 'SUBMISSION_UNKNOWN' FOR SHARE))
                          OR (NEW.state = 'FAILED' AND EXISTS (
                                SELECT 1 FROM orders WHERE intent_id = NEW.intent_id
                                  AND state IN ('REJECTED', 'RECONCILIATION_REQUIRED')
                                FOR SHARE))
                          OR (OLD.state = 'SUBMISSION_UNKNOWN'
                            AND NEW.state = 'CANCELED' AND EXISTS (
                                SELECT 1 FROM orders WHERE intent_id = NEW.intent_id
                                  AND state = 'CANCELED' FOR SHARE))) THEN
                        NULL;
                    ELSIF OLD.state = 'SUBMITTED' AND NEW.state = 'CANCELED'
                        AND EXISTS (SELECT 1 FROM orders WHERE intent_id = NEW.intent_id
                            AND state = 'CANCELED' FOR SHARE) THEN
                        NULL;
                    ELSE
                        RAISE EXCEPTION 'Execution intent transition is invalid.';
                    END IF;
                    RETURN NEW;
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM risk_evaluations AS risk
                    JOIN decisions AS decision ON decision.decision_id = NEW.decision_id
                    JOIN sessions AS stored_session
                      ON stored_session.session_id = NEW.session_id
                    JOIN goal_versions AS goal
                      ON goal.goal_version_id = NEW.goal_version_id
                    WHERE risk.risk_evaluation_id = NEW.risk_evaluation_id
                      AND risk.intent_id = NEW.intent_id
                      AND risk.decision_id = NEW.decision_id
                      AND risk.goal_version_id = NEW.goal_version_id
                      AND risk.session_id = NEW.session_id
                      AND decision.session_id = NEW.session_id
                      AND decision.goal_version_id = NEW.goal_version_id
                      AND stored_session.state = 'RUNNING'
                      AND stored_session.mode = NEW.mode
                      AND goal.mode = NEW.mode
                      AND NEW.mode IN ('PAPER_REVIEW', 'PAPER_AUTO')
                      AND NEW.environment = 'PAPER'
                      AND NEW.endpoint_identity =
                        goal.definition_json->'executionBinding'->>'endpointIdentity'
                      AND NEW.account_identifier =
                        goal.definition_json->'executionBinding'->>'expectedAccountIdentifier'
                      AND NEW.broker_connection_version =
                        goal.definition_json->'executionBinding'->'brokerConnectionVersion'
                      AND risk.requested_exposure = NEW.quantity * NEW.limit_price
                      AND ((risk.result = 'ACCEPTED'
                            AND ((NEW.mode = 'PAPER_REVIEW'
                                  AND NEW.state = 'REVIEW_PENDING')
                              OR (NEW.mode = 'PAPER_AUTO'
                                  AND NEW.state = 'RISK_ACCEPTED')))
                        OR (risk.result = 'REJECTED'
                            AND NEW.state = 'RISK_REJECTED'))
                    FOR SHARE OF risk, decision, stored_session, goal) THEN
                    RAISE EXCEPTION 'Execution intent binding is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE FUNCTION ensure_auto_intent_reservation() RETURNS trigger AS $$
            BEGIN
                IF NEW.mode = 'PAPER_AUTO' AND NEW.state NOT IN ('RISK_REJECTED', 'REJECTED')
                    AND NOT EXISTS (
                        SELECT 1 FROM exposure_reservations
                        WHERE intent_id = NEW.intent_id) THEN
                    RAISE EXCEPTION 'PAPER_AUTO intent lacks exposure reservation.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE CONSTRAINT TRIGGER ensure_auto_intent_reservation
                AFTER INSERT OR UPDATE OF state ON execution_intents
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION ensure_auto_intent_reservation();

            DO $migration$
            DECLARE function_body text;
            DECLARE updated_body text;
            BEGIN
                SELECT prosrc INTO function_body
                FROM pg_proc
                WHERE oid = 'protect_account_allocation_entry()'::regprocedure;
                updated_body := replace(
                    function_body,
                    'OR goal_definition->>''mode'' IS DISTINCT FROM ''PAPER_REVIEW''',
                    'OR goal_definition->>''mode'' NOT IN (''PAPER_REVIEW'', ''PAPER_AUTO'')');
                IF updated_body = function_body THEN
                    RAISE EXCEPTION 'Account allocation protection update did not match.';
                END IF;
                EXECUTE 'CREATE OR REPLACE FUNCTION protect_account_allocation_entry() '
                    || 'RETURNS trigger AS $function$' || updated_body
                    || '$function$ LANGUAGE plpgsql';
            END;
            $migration$;

            DO $migration$
            DECLARE function_body text;
            DECLARE updated_body text;
            BEGIN
                SELECT prosrc INTO function_body
                FROM pg_proc
                WHERE oid = 'protect_executable_session_preflight()'::regprocedure;
                updated_body := replace(
                    function_body,
                    'OR session_mode IS DISTINCT FROM ''PAPER_REVIEW''',
                    'OR session_mode NOT IN (''PAPER_REVIEW'', ''PAPER_AUTO'')');
                IF updated_body = function_body THEN
                    RAISE EXCEPTION 'Session preflight protection update did not match.';
                END IF;
                EXECUTE 'CREATE OR REPLACE FUNCTION protect_executable_session_preflight() '
                    || 'RETURNS trigger AS $function$' || updated_body
                    || '$function$ LANGUAGE plpgsql';
            END;
            $migration$;

            DO $migration$
            DECLARE function_body text;
            DECLARE updated_body text;
            BEGIN
                SELECT prosrc INTO function_body
                FROM pg_proc
                WHERE oid = 'protect_risk_evaluation()'::regprocedure;
                updated_body := replace(
                    function_body,
                    'AND stored_session.mode = ''PAPER_REVIEW''',
                    'AND stored_session.mode = goal.mode');
                updated_body := replace(
                    updated_body,
                    'AND goal.mode = ''PAPER_REVIEW''',
                    'AND goal.mode IN (''PAPER_REVIEW'', ''PAPER_AUTO'')');
                IF updated_body = function_body THEN
                    RAISE EXCEPTION 'Risk evaluation protection update did not match.';
                END IF;
                EXECUTE 'CREATE OR REPLACE FUNCTION protect_risk_evaluation() '
                    || 'RETURNS trigger AS $function$' || updated_body
                    || '$function$ LANGUAGE plpgsql';
            END;
            $migration$;

            DO $migration$
            DECLARE function_body text;
            DECLARE updated_body text;
            BEGIN
                SELECT prosrc INTO function_body
                FROM pg_proc
                WHERE oid = 'protect_reconciliation()'::regprocedure;
                updated_body := replace(
                    function_body,
                    'AND goal.mode = ''PAPER_REVIEW''',
                    'AND goal.mode IN (''PAPER_REVIEW'', ''PAPER_AUTO'')');
                IF updated_body = function_body THEN
                    RAISE EXCEPTION 'Reconciliation protection update did not match.';
                END IF;
                EXECUTE 'CREATE OR REPLACE FUNCTION protect_reconciliation() '
                    || 'RETURNS trigger AS $function$' || updated_body
                    || '$function$ LANGUAGE plpgsql';
            END;
            $migration$;

            DO $migration$
            DECLARE function_body text;
            DECLARE updated_body text;
            BEGIN
                SELECT prosrc INTO function_body
                FROM pg_proc
                WHERE oid = 'protect_exposure_reservation()'::regprocedure;
                updated_body := replace(
                    function_body,
                    'AND intent.state = ''REVIEW_PENDING''',
                    'AND ((intent.mode = ''PAPER_REVIEW'' '
                        || 'AND intent.state = ''REVIEW_PENDING'') '
                        || 'OR (intent.mode = ''PAPER_AUTO'' '
                        || 'AND intent.state = ''RISK_ACCEPTED''))');
                IF updated_body = function_body THEN
                    RAISE EXCEPTION 'Exposure reservation protection update did not match.';
                END IF;
                EXECUTE 'CREATE OR REPLACE FUNCTION protect_exposure_reservation() '
                    || 'RETURNS trigger AS $function$' || updated_body
                    || '$function$ LANGUAGE plpgsql';
            END;
            $migration$;
            """;

        private const string DropProtectionSql = """
            DROP TRIGGER IF EXISTS ensure_auto_intent_reservation ON execution_intents;
            DROP FUNCTION IF EXISTS ensure_auto_intent_reservation();
            DROP TRIGGER IF EXISTS ensure_cancel_pending_request ON orders;
            DROP FUNCTION IF EXISTS ensure_cancel_pending_request();
            DROP TRIGGER IF EXISTS protect_cancellation_request_write ON cancellation_requests;
            DROP TRIGGER IF EXISTS protect_cancellation_request_delete ON cancellation_requests;
            DROP FUNCTION IF EXISTS protect_cancellation_request();
            """;
    }
}
