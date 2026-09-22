using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPostFillPositionSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.CreateTable(
                name: "position_snapshot_batches",
                columns: table => new
                {
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_connection_version = table.Column<string>(type: "jsonb", nullable: false),
                    environment = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    endpoint_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    observation_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    position_count = table.Column<int>(type: "integer", nullable: false),
                    broker_observation_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_payload_reference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_position_snapshot_batches", x => x.batch_id);
                    table.CheckConstraint("ck_position_snapshot_batches_count", "position_count >= 0");
                    table.CheckConstraint("ck_position_snapshot_batches_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_position_snapshot_batches_environment", "environment = 'PAPER'");
                });

            migrationBuilder.CreateTable(
                name: "position_snapshots",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_connection_version = table.Column<string>(type: "jsonb", nullable: false),
                    environment = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    endpoint_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    instrument_identifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    average_entry_price = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: true),
                    market_value = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_position_snapshots", x => x.snapshot_id);
                    table.CheckConstraint("ck_position_snapshots_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_position_snapshots_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_position_snapshots_environment", "environment = 'PAPER'");
                    table.CheckConstraint("ck_position_snapshots_values", "quantity >= 0 AND quantity = trunc(quantity) AND market_value >= 0 AND ((quantity = 0 AND average_entry_price IS NULL AND market_value = 0) OR (quantity > 0 AND average_entry_price > 0))");
                    table.ForeignKey(
                        name: "FK_position_snapshots_position_snapshot_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "position_snapshot_batches",
                        principalColumn: "batch_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "post_fill_evaluations",
                columns: table => new
                {
                    evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    risk_policy_version = table.Column<string>(type: "jsonb", nullable: false),
                    allocation_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position_exposure = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    open_order_exposure = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    allocation_amount = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    position_evidence_current = table.Column<bool>(type: "boolean", nullable: false),
                    result = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_post_fill_evaluations", x => x.evaluation_id);
                    table.CheckConstraint("ck_post_fill_evaluations_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_post_fill_evaluations_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_post_fill_evaluations_result", "result IN ('PASSED', 'VIOLATION_LOCKED')");
                    table.CheckConstraint("ck_post_fill_evaluations_result_consistency", "(result = 'PASSED' AND position_evidence_current AND position_exposure + open_order_exposure <= allocation_amount) OR (result = 'VIOLATION_LOCKED' AND (NOT position_evidence_current OR position_exposure + open_order_exposure > allocation_amount))");
                    table.CheckConstraint("ck_post_fill_evaluations_values", "position_exposure >= 0 AND open_order_exposure >= 0 AND allocation_amount > 0");
                    table.ForeignKey(
                        name: "FK_post_fill_evaluations_account_allocation_entries_allocation~",
                        column: x => x.allocation_entry_id,
                        principalTable: "account_allocation_entries",
                        principalColumn: "allocation_entry_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_post_fill_evaluations_fills_fill_id",
                        column: x => x.fill_id,
                        principalTable: "fills",
                        principalColumn: "fill_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_post_fill_evaluations_goal_versions_goal_version_id",
                        column: x => x.goal_version_id,
                        principalTable: "goal_versions",
                        principalColumn: "goal_version_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_post_fill_evaluations_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_post_fill_evaluations_position_snapshot_batches_position_ba~",
                        column: x => x.position_batch_id,
                        principalTable: "position_snapshot_batches",
                        principalColumn: "batch_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_post_fill_evaluations_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "post_fill_safety_responses",
                columns: table => new
                {
                    response_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_connection_version = table.Column<string>(type: "jsonb", nullable: false),
                    environment = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    endpoint_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    reconciliation_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    notification_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_post_fill_safety_responses", x => x.response_id);
                    table.CheckConstraint("ck_post_fill_safety_responses_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_post_fill_safety_responses_environment", "environment = 'PAPER'");
                    table.CheckConstraint("ck_post_fill_safety_responses_states", "reconciliation_state = 'REQUESTED' AND notification_state = 'PENDING'");
                    table.ForeignKey(
                        name: "FK_post_fill_safety_responses_goal_versions_goal_version_id",
                        column: x => x.goal_version_id,
                        principalTable: "goal_versions",
                        principalColumn: "goal_version_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_post_fill_safety_responses_post_fill_evaluations_evaluation~",
                        column: x => x.evaluation_id,
                        principalTable: "post_fill_evaluations",
                        principalColumn: "evaluation_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_post_fill_safety_responses_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'SESSION_PREFLIGHT_COMPLETED', 'DECISION_RECORDED', 'RISK_EVALUATED', 'EXECUTION_INTENT_CREATED', 'EXECUTION_INTENT_STATE_CHANGED', 'REVIEW_RECORDED', 'KILL_SWITCH_CONFIGURED', 'KILL_SWITCH_STATE_CHANGED', 'RECONCILIATION_COMPLETED', 'EXPOSURE_RESERVED', 'EXPOSURE_RELEASED', 'EXPOSURE_ADJUSTED', 'PRE_SUBMIT_EVALUATED', 'ORDER_PREPARED', 'ORDER_STATE_CHANGED', 'BROKER_ORDER_EVENT_RECORDED', 'BROKER_EXECUTION_EVENT_RECORDED', 'FILL_RECORDED', 'POSITION_SNAPSHOT_RECORDED', 'POST_FILL_EVALUATED', 'POST_FILL_SAFETY_RESPONSE_CREATED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");

            migrationBuilder.CreateIndex(
                name: "IX_position_snapshot_batches_observation_reference",
                table: "position_snapshot_batches",
                column: "observation_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_position_snapshots_batch_id_instrument_identifier",
                table: "position_snapshots",
                columns: new[] { "batch_id", "instrument_identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_post_fill_evaluations_allocation_entry_id",
                table: "post_fill_evaluations",
                column: "allocation_entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_post_fill_evaluations_fill_id",
                table: "post_fill_evaluations",
                column: "fill_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_post_fill_evaluations_goal_version_id",
                table: "post_fill_evaluations",
                column: "goal_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_post_fill_evaluations_order_id",
                table: "post_fill_evaluations",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_post_fill_evaluations_position_batch_id",
                table: "post_fill_evaluations",
                column: "position_batch_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_post_fill_evaluations_session_id",
                table: "post_fill_evaluations",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_post_fill_safety_responses_evaluation_id",
                table: "post_fill_safety_responses",
                column: "evaluation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_post_fill_safety_responses_goal_version_id",
                table: "post_fill_safety_responses",
                column: "goal_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_post_fill_safety_responses_session_id",
                table: "post_fill_safety_responses",
                column: "session_id");

            migrationBuilder.Sql(ProtectionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TeardownSql);

            migrationBuilder.DropTable(
                name: "position_snapshots");

            migrationBuilder.DropTable(
                name: "post_fill_safety_responses");

            migrationBuilder.DropTable(
                name: "post_fill_evaluations");

            migrationBuilder.DropTable(
                name: "position_snapshot_batches");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'SESSION_PREFLIGHT_COMPLETED', 'DECISION_RECORDED', 'RISK_EVALUATED', 'EXECUTION_INTENT_CREATED', 'EXECUTION_INTENT_STATE_CHANGED', 'REVIEW_RECORDED', 'KILL_SWITCH_CONFIGURED', 'KILL_SWITCH_STATE_CHANGED', 'RECONCILIATION_COMPLETED', 'EXPOSURE_RESERVED', 'EXPOSURE_RELEASED', 'EXPOSURE_ADJUSTED', 'PRE_SUBMIT_EVALUATED', 'ORDER_PREPARED', 'ORDER_STATE_CHANGED', 'BROKER_ORDER_EVENT_RECORDED', 'BROKER_EXECUTION_EVENT_RECORDED', 'FILL_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");
        }

        private const string ProtectionSql = """
            CREATE FUNCTION protect_position_snapshot_batch() RETURNS trigger AS $$
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Position snapshot batches are append-only.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_position_snapshot_batch_write
                BEFORE INSERT OR UPDATE ON position_snapshot_batches
                FOR EACH ROW EXECUTE FUNCTION protect_position_snapshot_batch();
            CREATE TRIGGER protect_position_snapshot_batch_delete
                BEFORE DELETE ON position_snapshot_batches
                FOR EACH ROW EXECUTE FUNCTION protect_position_snapshot_batch();

            CREATE FUNCTION protect_position_snapshot() RETURNS trigger AS $$
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Position snapshots are append-only.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM position_snapshot_batches AS batch
                    WHERE batch.batch_id = NEW.batch_id
                      AND batch.broker_connection_version = NEW.broker_connection_version
                      AND batch.environment = NEW.environment
                      AND batch.endpoint_identity = NEW.endpoint_identity
                      AND batch.account_identifier = NEW.account_identifier
                    FOR SHARE OF batch) THEN
                    RAISE EXCEPTION 'Position snapshot binding is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_position_snapshot_write
                BEFORE INSERT OR UPDATE ON position_snapshots
                FOR EACH ROW EXECUTE FUNCTION protect_position_snapshot();
            CREATE TRIGGER protect_position_snapshot_delete
                BEFORE DELETE ON position_snapshots
                FOR EACH ROW EXECUTE FUNCTION protect_position_snapshot();

            CREATE FUNCTION protect_post_fill_evaluation() RETURNS trigger AS $$
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Post-Fill evaluations are append-only.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM fills AS fill_record
                    JOIN orders AS order_record ON order_record.order_id = fill_record.order_id
                    JOIN execution_intents AS intent ON intent.intent_id = order_record.intent_id
                    JOIN risk_evaluations AS risk ON risk.risk_evaluation_id = intent.risk_evaluation_id
                    JOIN exposure_reservations AS reservation ON reservation.intent_id = intent.intent_id
                    JOIN account_allocation_entries AS allocation
                      ON allocation.allocation_entry_id = reservation.allocation_entry_id
                    JOIN position_snapshot_batches AS batch ON batch.batch_id = NEW.position_batch_id
                    WHERE fill_record.fill_id = NEW.fill_id
                      AND order_record.order_id = NEW.order_id
                      AND intent.goal_version_id = NEW.goal_version_id
                      AND intent.session_id = NEW.session_id
                      AND risk.risk_policy_version = NEW.risk_policy_version
                      AND allocation.allocation_entry_id = NEW.allocation_entry_id
                      AND allocation.allocation_amount = NEW.allocation_amount
                      AND allocation.currency = NEW.currency
                      AND batch.broker_connection_version = order_record.broker_connection_version
                      AND batch.environment = order_record.environment
                      AND batch.endpoint_identity = order_record.endpoint_identity
                      AND batch.account_identifier = order_record.account_identifier
                      AND batch.position_count = (
                        SELECT count(*) FROM position_snapshots AS position
                        WHERE position.batch_id = batch.batch_id)
                      AND NEW.position_exposure = COALESCE((
                        SELECT sum(position.market_value) FROM position_snapshots AS position
                        WHERE position.batch_id = batch.batch_id), 0)
                      AND NEW.open_order_exposure = COALESCE((
                        SELECT sum(open_reservation.remaining_amount)
                        FROM exposure_reservations AS open_reservation
                        WHERE open_reservation.allocation_entry_id = allocation.allocation_entry_id
                          AND open_reservation.state = 'RESERVED'), 0)
                      AND NEW.position_evidence_current = EXISTS (
                        SELECT 1 FROM position_snapshots AS position
                        WHERE position.batch_id = batch.batch_id
                          AND position.instrument_identifier = order_record.instrument_identifier
                          AND position.quantity >= order_record.filled_quantity)
                    FOR SHARE OF fill_record, order_record, intent, risk, reservation, allocation, batch) THEN
                    RAISE EXCEPTION 'Post-Fill evaluation authority is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_post_fill_evaluation_write
                BEFORE INSERT OR UPDATE ON post_fill_evaluations
                FOR EACH ROW EXECUTE FUNCTION protect_post_fill_evaluation();
            CREATE TRIGGER protect_post_fill_evaluation_delete
                BEFORE DELETE ON post_fill_evaluations
                FOR EACH ROW EXECUTE FUNCTION protect_post_fill_evaluation();

            CREATE FUNCTION protect_post_fill_safety_response() RETURNS trigger AS $$
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Post-Fill safety responses are append-only.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM post_fill_evaluations AS evaluation
                    JOIN orders AS order_record ON order_record.order_id = evaluation.order_id
                    WHERE evaluation.evaluation_id = NEW.evaluation_id
                      AND evaluation.result = 'VIOLATION_LOCKED'
                      AND evaluation.goal_version_id = NEW.goal_version_id
                      AND evaluation.session_id = NEW.session_id
                      AND order_record.broker_connection_version = NEW.broker_connection_version
                      AND order_record.environment = NEW.environment
                      AND order_record.endpoint_identity = NEW.endpoint_identity
                      AND order_record.account_identifier = NEW.account_identifier
                    FOR SHARE OF evaluation, order_record) THEN
                    RAISE EXCEPTION 'Post-Fill safety response authority is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_post_fill_safety_response_write
                BEFORE INSERT OR UPDATE ON post_fill_safety_responses
                FOR EACH ROW EXECUTE FUNCTION protect_post_fill_safety_response();
            CREATE TRIGGER protect_post_fill_safety_response_delete
                BEFORE DELETE ON post_fill_safety_responses
                FOR EACH ROW EXECUTE FUNCTION protect_post_fill_safety_response();

            CREATE FUNCTION ensure_position_snapshot_batch_evaluated() RETURNS trigger AS $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM post_fill_evaluations
                    WHERE position_batch_id = NEW.batch_id) THEN
                    RAISE EXCEPTION 'Position snapshot batch lacks a post-Fill evaluation.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE CONSTRAINT TRIGGER ensure_position_snapshot_batch_evaluated
                AFTER INSERT ON position_snapshot_batches
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION ensure_position_snapshot_batch_evaluated();

            CREATE OR REPLACE FUNCTION protect_pre_submit_evaluation() RETURNS trigger AS $$
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Pre-submit evaluations are append-only.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM execution_intents AS intent
                    JOIN review_approvals AS review ON review.approval_id = NEW.approval_id
                    JOIN exposure_reservations AS reservation ON reservation.reservation_id = NEW.reservation_id
                    JOIN reconciliations AS reconciliation ON reconciliation.reconciliation_id = NEW.reconciliation_id
                    WHERE intent.intent_id = NEW.intent_id
                      AND intent.state = 'APPROVED'
                      AND intent.content_digest = NEW.intent_digest
                      AND intent.environment = 'PAPER'
                      AND review.intent_id = intent.intent_id
                      AND review.result = 'APPROVED'
                      AND review.intent_digest = intent.content_digest
                      AND reservation.intent_id = intent.intent_id
                      AND reservation.state = 'RESERVED'
                      AND reservation.amount = intent.quantity * intent.limit_price
                      AND reconciliation.goal_version_id = intent.goal_version_id
                      AND (NEW.result = 'FAILED' OR (
                          NEW.evaluated_at < review.expires_at
                          AND NEW.evaluated_at < intent.expires_at
                          AND reconciliation.result = 'CURRENT'
                          AND reconciliation.position_exposure = NEW.position_exposure
                          AND reconciliation.open_order_exposure = NEW.open_order_exposure
                          AND reconciliation.buying_power = NEW.buying_power
                          AND reconciliation.currency = NEW.currency
                          AND NOT EXISTS (
                            SELECT 1 FROM fills AS pending_fill
                            JOIN orders AS filled_order ON filled_order.order_id = pending_fill.order_id
                            LEFT JOIN post_fill_evaluations AS post_fill
                              ON post_fill.fill_id = pending_fill.fill_id
                            WHERE filled_order.environment = intent.environment
                              AND filled_order.broker_connection_version = intent.broker_connection_version
                              AND filled_order.endpoint_identity = intent.endpoint_identity
                              AND filled_order.account_identifier = intent.account_identifier
                              AND post_fill.fill_id IS NULL)
                          AND 4 = (SELECT count(*) FROM kill_switches
                              WHERE state = 'CLEAR' AND (
                                (scope_type = 'SYSTEM' AND scope_identifier = intent.environment)
                                OR (scope_type = 'CONNECTION' AND scope_identifier = intent.broker_connection_version->>'digest')
                                OR (scope_type = 'ACCOUNT' AND scope_identifier = intent.account_identifier)
                                OR (scope_type = 'GOAL' AND scope_identifier = intent.goal_version_id::text)))))
                    FOR SHARE OF intent, review, reservation, reconciliation) THEN
                    RAISE EXCEPTION 'Pre-submit evaluation authority is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            """;

        private const string TeardownSql = """
            DROP TRIGGER IF EXISTS ensure_position_snapshot_batch_evaluated ON position_snapshot_batches;
            DROP FUNCTION IF EXISTS ensure_position_snapshot_batch_evaluated();
            DROP TRIGGER IF EXISTS protect_post_fill_safety_response_write ON post_fill_safety_responses;
            DROP TRIGGER IF EXISTS protect_post_fill_safety_response_delete ON post_fill_safety_responses;
            DROP FUNCTION IF EXISTS protect_post_fill_safety_response();
            DROP TRIGGER IF EXISTS protect_post_fill_evaluation_write ON post_fill_evaluations;
            DROP TRIGGER IF EXISTS protect_post_fill_evaluation_delete ON post_fill_evaluations;
            DROP FUNCTION IF EXISTS protect_post_fill_evaluation();
            DROP TRIGGER IF EXISTS protect_position_snapshot_write ON position_snapshots;
            DROP TRIGGER IF EXISTS protect_position_snapshot_delete ON position_snapshots;
            DROP FUNCTION IF EXISTS protect_position_snapshot();
            DROP TRIGGER IF EXISTS protect_position_snapshot_batch_write ON position_snapshot_batches;
            DROP TRIGGER IF EXISTS protect_position_snapshot_batch_delete ON position_snapshot_batches;
            DROP FUNCTION IF EXISTS protect_position_snapshot_batch();

            CREATE OR REPLACE FUNCTION protect_pre_submit_evaluation() RETURNS trigger AS $$
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Pre-submit evaluations are append-only.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM execution_intents AS intent
                    JOIN review_approvals AS review ON review.approval_id = NEW.approval_id
                    JOIN exposure_reservations AS reservation ON reservation.reservation_id = NEW.reservation_id
                    JOIN reconciliations AS reconciliation ON reconciliation.reconciliation_id = NEW.reconciliation_id
                    WHERE intent.intent_id = NEW.intent_id
                      AND intent.state = 'APPROVED'
                      AND intent.content_digest = NEW.intent_digest
                      AND intent.environment = 'PAPER'
                      AND review.intent_id = intent.intent_id
                      AND review.result = 'APPROVED'
                      AND review.intent_digest = intent.content_digest
                      AND reservation.intent_id = intent.intent_id
                      AND reservation.state = 'RESERVED'
                      AND reservation.amount = intent.quantity * intent.limit_price
                      AND reconciliation.goal_version_id = intent.goal_version_id
                      AND (NEW.result = 'FAILED' OR (
                          NEW.evaluated_at < review.expires_at
                          AND NEW.evaluated_at < intent.expires_at
                          AND reconciliation.result = 'CURRENT'
                          AND reconciliation.position_exposure = NEW.position_exposure
                          AND reconciliation.open_order_exposure = NEW.open_order_exposure
                          AND reconciliation.buying_power = NEW.buying_power
                          AND reconciliation.currency = NEW.currency
                          AND 4 = (SELECT count(*) FROM kill_switches
                              WHERE state = 'CLEAR' AND (
                                (scope_type = 'SYSTEM' AND scope_identifier = intent.environment)
                                OR (scope_type = 'CONNECTION' AND scope_identifier = intent.broker_connection_version->>'digest')
                                OR (scope_type = 'ACCOUNT' AND scope_identifier = intent.account_identifier)
                                OR (scope_type = 'GOAL' AND scope_identifier = intent.goal_version_id::text)))))
                    FOR SHARE OF intent, review, reservation, reconciliation) THEN
                    RAISE EXCEPTION 'Pre-submit evaluation authority is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            """;
    }
}
