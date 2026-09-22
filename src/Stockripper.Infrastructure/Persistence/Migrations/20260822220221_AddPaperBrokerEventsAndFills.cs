using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperBrokerEventsAndFills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_exposure_reservations_amount",
                table: "exposure_reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_exposure_reservations_lifecycle",
                table: "exposure_reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.AddColumn<decimal>(
                name: "filled_quantity",
                table: "orders",
                type: "numeric(38,12)",
                precision: 38,
                scale: 12,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "remaining_amount",
                table: "exposure_reservations",
                type: "numeric(38,12)",
                precision: 38,
                scale: 12,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql("UPDATE exposure_reservations SET remaining_amount = amount WHERE state = 'RESERVED';");

            migrationBuilder.CreateTable(
                name: "broker_execution_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_connection_version = table.Column<string>(type: "jsonb", nullable: false),
                    environment = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    endpoint_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    broker_client_order_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    broker_order_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    event_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    cumulative_filled_quantity = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    broker_event_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_payload_reference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broker_execution_events", x => x.event_id);
                    table.CheckConstraint("ck_broker_execution_events_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_broker_execution_events_environment", "environment = 'PAPER'");
                    table.CheckConstraint("ck_broker_execution_events_kind", "kind IN ('ACKNOWLEDGED', 'PARTIALLY_FILLED', 'FILLED', 'CANCELED', 'REJECTED', 'EXPIRED')");
                    table.CheckConstraint("ck_broker_execution_events_quantity", "cumulative_filled_quantity >= 0");
                    table.ForeignKey(
                        name: "FK_broker_execution_events_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fills",
                columns: table => new
                {
                    fill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_fill_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_order_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    instrument_identifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    price = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    fee_amount = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: true),
                    fee_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    broker_execution_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_payload_reference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fills", x => x.fill_id);
                    table.CheckConstraint("ck_fills_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_fills_fee", "(fee_amount IS NULL AND fee_currency IS NULL) OR (fee_amount IS NOT NULL AND fee_currency ~ '^[A-Z]{3}$')");
                    table.CheckConstraint("ck_fills_values", "quantity > 0 AND quantity = trunc(quantity) AND price > 0 AND (fee_amount IS NULL OR fee_amount >= 0)");
                    table.ForeignKey(
                        name: "FK_fills_broker_execution_events_event_id",
                        column: x => x.event_id,
                        principalTable: "broker_execution_events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_fills_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_filled_quantity",
                table: "orders",
                sql: "filled_quantity >= 0 AND filled_quantity <= quantity");

            migrationBuilder.AddCheckConstraint(
                name: "ck_exposure_reservations_amount",
                table: "exposure_reservations",
                sql: "amount > 0 AND remaining_amount >= 0 AND remaining_amount <= amount");

            migrationBuilder.AddCheckConstraint(
                name: "ck_exposure_reservations_lifecycle",
                table: "exposure_reservations",
                sql: "(state = 'RESERVED' AND remaining_amount > 0 AND released_at IS NULL AND release_reason IS NULL) OR (state = 'RELEASED' AND remaining_amount = 0 AND revision > 0 AND released_at IS NOT NULL AND release_reason IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'SESSION_PREFLIGHT_COMPLETED', 'DECISION_RECORDED', 'RISK_EVALUATED', 'EXECUTION_INTENT_CREATED', 'EXECUTION_INTENT_STATE_CHANGED', 'REVIEW_RECORDED', 'KILL_SWITCH_CONFIGURED', 'KILL_SWITCH_STATE_CHANGED', 'RECONCILIATION_COMPLETED', 'EXPOSURE_RESERVED', 'EXPOSURE_RELEASED', 'EXPOSURE_ADJUSTED', 'PRE_SUBMIT_EVALUATED', 'ORDER_PREPARED', 'ORDER_STATE_CHANGED', 'BROKER_ORDER_EVENT_RECORDED', 'BROKER_EXECUTION_EVENT_RECORDED', 'FILL_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");

            migrationBuilder.CreateIndex(
                name: "IX_broker_execution_events_event_reference",
                table: "broker_execution_events",
                column: "event_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_broker_execution_events_order_id",
                table: "broker_execution_events",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_fills_broker_fill_id",
                table: "fills",
                column: "broker_fill_id",
                unique: true,
                filter: "broker_fill_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_fills_event_id",
                table: "fills",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fills_order_id",
                table: "fills",
                column: "order_id");

            migrationBuilder.Sql(UpSafetySql);
        }

        private const string UpSafetySql = """
            CREATE OR REPLACE FUNCTION protect_exposure_reservation() RETURNS trigger AS $$
            DECLARE authorized_amount numeric;
            DECLARE current_exposure numeric;
            DECLARE reserved_exposure numeric;
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Exposure reservations cannot be removed.';
                END IF;
                IF TG_OP = 'UPDATE' THEN
                    IF NEW.reservation_id IS DISTINCT FROM OLD.reservation_id
                        OR NEW.intent_id IS DISTINCT FROM OLD.intent_id
                        OR NEW.goal_version_id IS DISTINCT FROM OLD.goal_version_id
                        OR NEW.allocation_entry_id IS DISTINCT FROM OLD.allocation_entry_id
                        OR NEW.broker_connection_version IS DISTINCT FROM OLD.broker_connection_version
                        OR NEW.environment IS DISTINCT FROM OLD.environment
                        OR NEW.endpoint_identity IS DISTINCT FROM OLD.endpoint_identity
                        OR NEW.account_identifier IS DISTINCT FROM OLD.account_identifier
                        OR NEW.amount IS DISTINCT FROM OLD.amount
                        OR NEW.currency IS DISTINCT FROM OLD.currency
                        OR NEW.created_at IS DISTINCT FROM OLD.created_at
                        OR NEW.content_digest IS DISTINCT FROM OLD.content_digest
                        OR OLD.state <> 'RESERVED'
                        OR NEW.remaining_amount >= OLD.remaining_amount
                        OR NEW.revision <> OLD.revision + 1
                        OR NOT ((NEW.state = 'RESERVED' AND NEW.remaining_amount > 0
                                AND NEW.released_at IS NULL AND NEW.release_reason IS NULL)
                            OR (NEW.state = 'RELEASED' AND NEW.remaining_amount = 0
                                AND NEW.released_at IS NOT NULL AND NEW.release_reason IS NOT NULL)) THEN
                        RAISE EXCEPTION 'Exposure reservation transition is invalid.';
                    END IF;
                    RETURN NEW;
                END IF;
                IF NEW.remaining_amount <> NEW.amount OR NOT EXISTS (
                    SELECT 1 FROM execution_intents AS intent
                    WHERE intent.intent_id = NEW.intent_id
                      AND intent.goal_version_id = NEW.goal_version_id
                      AND intent.state = 'REVIEW_PENDING'
                      AND intent.environment = 'PAPER'
                      AND intent.broker_connection_version = NEW.broker_connection_version
                      AND intent.endpoint_identity = NEW.endpoint_identity
                      AND intent.account_identifier = NEW.account_identifier
                      AND intent.quantity * intent.limit_price = NEW.amount
                    FOR SHARE) THEN
                    RAISE EXCEPTION 'Exposure reservation intent binding is invalid.';
                END IF;
                SELECT allocation_amount INTO authorized_amount FROM account_allocation_entries
                    WHERE allocation_entry_id = NEW.allocation_entry_id
                      AND goal_version_id = NEW.goal_version_id AND state = 'ACTIVE'
                      AND environment = NEW.environment AND account_identifier = NEW.account_identifier
                    FOR UPDATE;
                SELECT position_exposure + open_order_exposure INTO current_exposure
                    FROM reconciliations WHERE goal_version_id = NEW.goal_version_id
                      AND environment = NEW.environment AND account_identifier = NEW.account_identifier
                      AND result = 'CURRENT' AND completed_at <= NEW.created_at
                    ORDER BY completed_at DESC LIMIT 1 FOR SHARE;
                SELECT COALESCE(sum(remaining_amount), 0) INTO reserved_exposure FROM exposure_reservations
                    WHERE allocation_entry_id = NEW.allocation_entry_id AND state = 'RESERVED';
                IF authorized_amount IS NULL OR current_exposure IS NULL
                    OR current_exposure + reserved_exposure + NEW.remaining_amount > authorized_amount
                    OR 4 <> (SELECT count(*) FROM kill_switches WHERE state = 'CLEAR' AND (
                        (scope_type = 'SYSTEM' AND scope_identifier = NEW.environment)
                        OR (scope_type = 'CONNECTION' AND scope_identifier = NEW.broker_connection_version->>'digest')
                        OR (scope_type = 'ACCOUNT' AND scope_identifier = NEW.account_identifier)
                        OR (scope_type = 'GOAL' AND scope_identifier = NEW.goal_version_id::text))) THEN
                    RAISE EXCEPTION 'Exposure reservation exceeds durable authority.';
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
                        JOIN pre_submit_evaluations AS evaluation ON evaluation.evaluation_id = NEW.evaluation_id
                        WHERE intent.intent_id = NEW.intent_id
                          AND intent.state = 'APPROVED' AND intent.content_digest = NEW.intent_digest
                          AND intent.environment = NEW.environment
                          AND intent.broker_connection_version = NEW.broker_connection_version
                          AND intent.endpoint_identity = NEW.endpoint_identity
                          AND intent.account_identifier = NEW.account_identifier
                          AND intent.instrument_identifier = NEW.instrument_identifier
                          AND intent.side = NEW.side AND intent.order_type = NEW.order_type
                          AND intent.quantity = NEW.quantity AND intent.limit_price = NEW.limit_price
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
                    OR NEW.side IS DISTINCT FROM OLD.side OR NEW.order_type IS DISTINCT FROM OLD.order_type
                    OR NEW.quantity IS DISTINCT FROM OLD.quantity OR NEW.limit_price IS DISTINCT FROM OLD.limit_price
                    OR NEW.time_in_force IS DISTINCT FROM OLD.time_in_force
                    OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                    OR NEW.broker_client_order_id IS DISTINCT FROM OLD.broker_client_order_id
                    OR NEW.first_submission_time IS DISTINCT FROM OLD.first_submission_time
                    OR NEW.filled_quantity < OLD.filled_quantity
                    OR (OLD.broker_order_id IS NOT NULL AND NEW.broker_order_id IS DISTINCT FROM OLD.broker_order_id)
                    OR NOT ((OLD.state = 'SUBMITTING' AND NEW.state IN ('ACKNOWLEDGED', 'REJECTED', 'SUBMISSION_UNKNOWN', 'PARTIALLY_FILLED', 'FILLED', 'CANCELED', 'EXPIRED'))
                        OR (OLD.state = 'SUBMISSION_UNKNOWN' AND NEW.state IN ('ACKNOWLEDGED', 'RECONCILIATION_REQUIRED', 'PARTIALLY_FILLED', 'FILLED', 'CANCELED', 'REJECTED', 'EXPIRED'))
                        OR (OLD.state = 'ACKNOWLEDGED' AND NEW.state IN ('PARTIALLY_FILLED', 'FILLED', 'CANCELED', 'REJECTED', 'EXPIRED'))
                        OR (OLD.state = 'PARTIALLY_FILLED' AND NEW.state IN ('PARTIALLY_FILLED', 'FILLED', 'CANCELED', 'REJECTED', 'EXPIRED'))
                        OR (OLD.state = 'CANCEL_PENDING' AND NEW.state IN ('PARTIALLY_FILLED', 'FILLED', 'CANCELED', 'REJECTED', 'EXPIRED'))) THEN
                    RAISE EXCEPTION 'Order transition is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE FUNCTION protect_broker_execution_event() RETURNS trigger AS $$
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Broker execution events are append-only.';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM orders
                    WHERE order_id = NEW.order_id
                      AND environment = NEW.environment
                      AND broker_connection_version = NEW.broker_connection_version
                      AND endpoint_identity = NEW.endpoint_identity
                      AND account_identifier = NEW.account_identifier
                      AND broker_client_order_id = NEW.broker_client_order_id
                      AND (broker_order_id IS NULL OR broker_order_id = NEW.broker_order_id)
                    FOR SHARE) THEN
                    RAISE EXCEPTION 'Broker execution event binding is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_broker_execution_event_write BEFORE INSERT OR UPDATE ON broker_execution_events
                FOR EACH ROW EXECUTE FUNCTION protect_broker_execution_event();
            CREATE TRIGGER protect_broker_execution_event_delete BEFORE DELETE ON broker_execution_events
                FOR EACH ROW EXECUTE FUNCTION protect_broker_execution_event();

            CREATE FUNCTION protect_fill() RETURNS trigger AS $$
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Fill records are append-only.';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM broker_execution_events AS event
                    JOIN orders AS order_record ON order_record.order_id = event.order_id
                    WHERE event.event_id = NEW.event_id AND event.order_id = NEW.order_id
                      AND event.kind IN ('PARTIALLY_FILLED', 'FILLED')
                      AND event.broker_order_id = NEW.broker_order_id
                      AND event.source_payload_reference = NEW.source_payload_reference
                      AND order_record.instrument_identifier = NEW.instrument_identifier
                    FOR SHARE OF event, order_record) THEN
                    RAISE EXCEPTION 'Fill binding is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_fill_write BEFORE INSERT OR UPDATE ON fills
                FOR EACH ROW EXECUTE FUNCTION protect_fill();
            CREATE TRIGGER protect_fill_delete BEFORE DELETE ON fills
                FOR EACH ROW EXECUTE FUNCTION protect_fill();
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS protect_fill_write ON fills;
                DROP TRIGGER IF EXISTS protect_fill_delete ON fills;
                DROP FUNCTION IF EXISTS protect_fill();
                DROP TRIGGER IF EXISTS protect_broker_execution_event_write ON broker_execution_events;
                DROP TRIGGER IF EXISTS protect_broker_execution_event_delete ON broker_execution_events;
                DROP FUNCTION IF EXISTS protect_broker_execution_event();
                """);
            migrationBuilder.Sql(AddDurableExecutionSafetyAndPaperOrderPreparation.TeardownSql);

            migrationBuilder.DropTable(
                name: "fills");

            migrationBuilder.DropTable(
                name: "broker_execution_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_filled_quantity",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_exposure_reservations_amount",
                table: "exposure_reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_exposure_reservations_lifecycle",
                table: "exposure_reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "filled_quantity",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "remaining_amount",
                table: "exposure_reservations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_exposure_reservations_amount",
                table: "exposure_reservations",
                sql: "amount > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_exposure_reservations_lifecycle",
                table: "exposure_reservations",
                sql: "(state = 'RESERVED' AND revision = 0 AND released_at IS NULL AND release_reason IS NULL) OR (state = 'RELEASED' AND revision > 0 AND released_at IS NOT NULL AND release_reason IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'SESSION_PREFLIGHT_COMPLETED', 'DECISION_RECORDED', 'RISK_EVALUATED', 'EXECUTION_INTENT_CREATED', 'EXECUTION_INTENT_STATE_CHANGED', 'REVIEW_RECORDED', 'KILL_SWITCH_CONFIGURED', 'KILL_SWITCH_STATE_CHANGED', 'RECONCILIATION_COMPLETED', 'EXPOSURE_RESERVED', 'EXPOSURE_RELEASED', 'PRE_SUBMIT_EVALUATED', 'ORDER_PREPARED', 'ORDER_STATE_CHANGED', 'BROKER_ORDER_EVENT_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");

            migrationBuilder.Sql(AddDurableExecutionSafetyAndPaperOrderPreparation.ProtectionSql);
        }
    }
}
