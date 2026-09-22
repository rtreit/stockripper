using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableExecutionSafetyAndPaperOrderPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.AddColumn<Guid>(
                name: "reconciliation_id",
                table: "risk_evaluations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "reconciliation_id",
                table: "review_approvals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reservation_id",
                table: "review_approvals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reconciliation_id",
                table: "executable_session_preflights",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "exposure_reservations",
                columns: table => new
                {
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocation_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_connection_version = table.Column<string>(type: "jsonb", nullable: false),
                    environment = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    endpoint_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    release_reason = table.Column<string>(type: "text", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exposure_reservations", x => x.reservation_id);
                    table.CheckConstraint("ck_exposure_reservations_amount", "amount > 0");
                    table.CheckConstraint("ck_exposure_reservations_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_exposure_reservations_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_exposure_reservations_environment", "environment = 'PAPER'");
                    table.CheckConstraint("ck_exposure_reservations_lifecycle", "(state = 'RESERVED' AND revision = 0 AND released_at IS NULL AND release_reason IS NULL) OR (state = 'RELEASED' AND revision > 0 AND released_at IS NOT NULL AND release_reason IS NOT NULL)");
                    table.CheckConstraint("ck_exposure_reservations_revision", "revision >= 0");
                    table.CheckConstraint("ck_exposure_reservations_state", "state IN ('RESERVED', 'RELEASED')");
                    table.ForeignKey(
                        name: "FK_exposure_reservations_account_allocation_entries_allocation~",
                        column: x => x.allocation_entry_id,
                        principalTable: "account_allocation_entries",
                        principalColumn: "allocation_entry_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_exposure_reservations_execution_intents_intent_id",
                        column: x => x.intent_id,
                        principalTable: "execution_intents",
                        principalColumn: "intent_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_exposure_reservations_goal_versions_goal_version_id",
                        column: x => x.goal_version_id,
                        principalTable: "goal_versions",
                        principalColumn: "goal_version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "kill_switches",
                columns: table => new
                {
                    kill_switch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    configured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    triggered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    trigger_reason = table.Column<string>(type: "text", nullable: true),
                    recovery_reconciliation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recovery_required_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kill_switches", x => x.kill_switch_id);
                    table.CheckConstraint("ck_kill_switches_lifecycle", "(state = 'CLEAR' AND ((revision = 0 AND triggered_at IS NULL AND trigger_reason IS NULL AND recovery_reconciliation_id IS NULL AND recovery_required_at IS NULL AND cleared_at IS NULL) OR (revision > 0 AND triggered_at IS NOT NULL AND trigger_reason IS NOT NULL AND recovery_reconciliation_id IS NOT NULL AND recovery_required_at IS NOT NULL AND cleared_at IS NOT NULL))) OR (state = 'TRIGGERED' AND triggered_at IS NOT NULL AND trigger_reason IS NOT NULL AND recovery_reconciliation_id IS NULL AND recovery_required_at IS NULL AND cleared_at IS NULL) OR (state = 'RECOVERY_REQUIRED' AND triggered_at IS NOT NULL AND trigger_reason IS NOT NULL AND recovery_reconciliation_id IS NOT NULL AND recovery_required_at IS NOT NULL AND cleared_at IS NULL)");
                    table.CheckConstraint("ck_kill_switches_revision", "revision >= 0");
                    table.CheckConstraint("ck_kill_switches_scope", "scope_type IN ('SYSTEM', 'CONNECTION', 'ACCOUNT', 'GOAL')");
                    table.CheckConstraint("ck_kill_switches_state", "state IN ('CLEAR', 'TRIGGERED', 'RECOVERY_REQUIRED')");
                });

            migrationBuilder.CreateTable(
                name: "reconciliations",
                columns: table => new
                {
                    reconciliation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_connection_version = table.Column<string>(type: "jsonb", nullable: false),
                    environment = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    endpoint_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    reconciliation_policy_version = table.Column<string>(type: "jsonb", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    position_exposure = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    open_order_exposure = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    buying_power = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    differences = table.Column<string>(type: "jsonb", nullable: false),
                    corrections = table.Column<string>(type: "jsonb", nullable: false),
                    result = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliations", x => x.reconciliation_id);
                    table.CheckConstraint("ck_reconciliations_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_reconciliations_details", "jsonb_typeof(differences) = 'array' AND jsonb_typeof(corrections) = 'array' AND ((result = 'CURRENT' AND jsonb_array_length(differences) = 0) OR (result = 'DIFFERENCE_LOCKED' AND jsonb_array_length(differences) > 0) OR result = 'FAILED')");
                    table.CheckConstraint("ck_reconciliations_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_reconciliations_environment", "environment = 'PAPER'");
                    table.CheckConstraint("ck_reconciliations_money", "position_exposure >= 0 AND open_order_exposure >= 0 AND buying_power >= 0");
                    table.CheckConstraint("ck_reconciliations_result", "result IN ('CURRENT', 'DIFFERENCE_LOCKED', 'FAILED')");
                    table.CheckConstraint("ck_reconciliations_time", "completed_at >= started_at AND observed_at <= completed_at");
                    table.ForeignKey(
                        name: "FK_reconciliations_goal_versions_goal_version_id",
                        column: x => x.goal_version_id,
                        principalTable: "goal_versions",
                        principalColumn: "goal_version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pre_submit_evaluations",
                columns: table => new
                {
                    evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reconciliation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position_exposure = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    open_order_exposure = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    buying_power = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    checks = table.Column<string>(type: "jsonb", nullable: false),
                    result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pre_submit_evaluations", x => x.evaluation_id);
                    table.CheckConstraint("ck_pre_submit_evaluations_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_pre_submit_evaluations_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_pre_submit_evaluations_money", "position_exposure >= 0 AND open_order_exposure >= 0 AND buying_power >= 0");
                    table.CheckConstraint("ck_pre_submit_evaluations_result", "result IN ('PASSED', 'FAILED')");
                    table.ForeignKey(
                        name: "FK_pre_submit_evaluations_execution_intents_intent_id",
                        column: x => x.intent_id,
                        principalTable: "execution_intents",
                        principalColumn: "intent_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pre_submit_evaluations_exposure_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalTable: "exposure_reservations",
                        principalColumn: "reservation_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pre_submit_evaluations_reconciliations_reconciliation_id",
                        column: x => x.reconciliation_id,
                        principalTable: "reconciliations",
                        principalColumn: "reconciliation_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_pre_submit_evaluations_review_approvals_approval_id",
                        column: x => x.approval_id,
                        principalTable: "review_approvals",
                        principalColumn: "approval_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    broker_client_order_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    broker_order_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    broker_event_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    first_submission_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    terminal_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.order_id);
                    table.CheckConstraint("ck_orders_environment", "environment = 'PAPER'");
                    table.CheckConstraint("ck_orders_request", "order_type = 'LIMIT' AND time_in_force = 'DAY' AND quantity > 0 AND quantity = trunc(quantity) AND limit_price > 0");
                    table.CheckConstraint("ck_orders_state", "state IN ('CREATED', 'SUBMITTING', 'ACKNOWLEDGED', 'PARTIALLY_FILLED', 'FILLED', 'CANCEL_PENDING', 'CANCELED', 'REJECTED', 'EXPIRED', 'SUBMISSION_UNKNOWN', 'RECONCILIATION_REQUIRED')");
                    table.CheckConstraint("ck_orders_terminal", "(state IN ('FILLED', 'CANCELED', 'REJECTED', 'EXPIRED', 'RECONCILIATION_REQUIRED') AND terminal_at IS NOT NULL) OR (state NOT IN ('FILLED', 'CANCELED', 'REJECTED', 'EXPIRED', 'RECONCILIATION_REQUIRED') AND terminal_at IS NULL)");
                    table.ForeignKey(
                        name: "FK_orders_execution_intents_intent_id",
                        column: x => x.intent_id,
                        principalTable: "execution_intents",
                        principalColumn: "intent_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_pre_submit_evaluations_evaluation_id",
                        column: x => x.evaluation_id,
                        principalTable: "pre_submit_evaluations",
                        principalColumn: "evaluation_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "broker_order_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_client_order_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    broker_order_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    event_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broker_order_events", x => x.event_id);
                    table.CheckConstraint("ck_broker_order_events_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_broker_order_events_result", "result IN ('ACKNOWLEDGED', 'REJECTED')");
                    table.ForeignKey(
                        name: "FK_broker_order_events_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_risk_evaluations_reconciliation_id",
                table: "risk_evaluations",
                column: "reconciliation_id");

            migrationBuilder.CreateIndex(
                name: "IX_review_approvals_reconciliation_id",
                table: "review_approvals",
                column: "reconciliation_id");

            migrationBuilder.CreateIndex(
                name: "IX_review_approvals_reservation_id",
                table: "review_approvals",
                column: "reservation_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_review_approvals_safety_evidence",
                table: "review_approvals",
                sql: "(result = 'APPROVED' AND reconciliation_id IS NOT NULL AND reservation_id IS NOT NULL) OR (result = 'REJECTED' AND reconciliation_id IS NULL AND reservation_id IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_executable_session_preflights_reconciliation_id",
                table: "executable_session_preflights",
                column: "reconciliation_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_executable_session_preflights_reconciliation",
                table: "executable_session_preflights",
                sql: "result <> 'PASSED' OR reconciliation_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'SESSION_PREFLIGHT_COMPLETED', 'DECISION_RECORDED', 'RISK_EVALUATED', 'EXECUTION_INTENT_CREATED', 'EXECUTION_INTENT_STATE_CHANGED', 'REVIEW_RECORDED', 'KILL_SWITCH_CONFIGURED', 'KILL_SWITCH_STATE_CHANGED', 'RECONCILIATION_COMPLETED', 'EXPOSURE_RESERVED', 'EXPOSURE_RELEASED', 'PRE_SUBMIT_EVALUATED', 'ORDER_PREPARED', 'ORDER_STATE_CHANGED', 'BROKER_ORDER_EVENT_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");

            migrationBuilder.CreateIndex(
                name: "IX_broker_order_events_event_reference",
                table: "broker_order_events",
                column: "event_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_broker_order_events_order_id",
                table: "broker_order_events",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_exposure_reservations_allocation_entry_id_state",
                table: "exposure_reservations",
                columns: new[] { "allocation_entry_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_exposure_reservations_goal_version_id",
                table: "exposure_reservations",
                column: "goal_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_exposure_reservations_intent_id",
                table: "exposure_reservations",
                column: "intent_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_kill_switches_scope_type_scope_identifier",
                table: "kill_switches",
                columns: new[] { "scope_type", "scope_identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_broker_client_order_id",
                table: "orders",
                column: "broker_client_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_evaluation_id",
                table: "orders",
                column: "evaluation_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_idempotency_key",
                table: "orders",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_intent_id",
                table: "orders",
                column: "intent_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pre_submit_evaluations_approval_id",
                table: "pre_submit_evaluations",
                column: "approval_id");

            migrationBuilder.CreateIndex(
                name: "IX_pre_submit_evaluations_intent_id",
                table: "pre_submit_evaluations",
                column: "intent_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pre_submit_evaluations_reconciliation_id",
                table: "pre_submit_evaluations",
                column: "reconciliation_id");

            migrationBuilder.CreateIndex(
                name: "IX_pre_submit_evaluations_reservation_id",
                table: "pre_submit_evaluations",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "IX_reconciliations_goal_version_id_completed_at",
                table: "reconciliations",
                columns: new[] { "goal_version_id", "completed_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_executable_session_preflights_reconciliations_reconciliatio~",
                table: "executable_session_preflights",
                column: "reconciliation_id",
                principalTable: "reconciliations",
                principalColumn: "reconciliation_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_review_approvals_exposure_reservations_reservation_id",
                table: "review_approvals",
                column: "reservation_id",
                principalTable: "exposure_reservations",
                principalColumn: "reservation_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_review_approvals_reconciliations_reconciliation_id",
                table: "review_approvals",
                column: "reconciliation_id",
                principalTable: "reconciliations",
                principalColumn: "reconciliation_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_risk_evaluations_reconciliations_reconciliation_id",
                table: "risk_evaluations",
                column: "reconciliation_id",
                principalTable: "reconciliations",
                principalColumn: "reconciliation_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(ProtectionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TeardownSql);

            migrationBuilder.DropForeignKey(
                name: "FK_executable_session_preflights_reconciliations_reconciliatio~",
                table: "executable_session_preflights");

            migrationBuilder.DropForeignKey(
                name: "FK_review_approvals_exposure_reservations_reservation_id",
                table: "review_approvals");

            migrationBuilder.DropForeignKey(
                name: "FK_review_approvals_reconciliations_reconciliation_id",
                table: "review_approvals");

            migrationBuilder.DropForeignKey(
                name: "FK_risk_evaluations_reconciliations_reconciliation_id",
                table: "risk_evaluations");

            migrationBuilder.DropTable(
                name: "broker_order_events");

            migrationBuilder.DropTable(
                name: "kill_switches");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "pre_submit_evaluations");

            migrationBuilder.DropTable(
                name: "exposure_reservations");

            migrationBuilder.DropTable(
                name: "reconciliations");

            migrationBuilder.DropIndex(
                name: "IX_risk_evaluations_reconciliation_id",
                table: "risk_evaluations");

            migrationBuilder.DropIndex(
                name: "IX_review_approvals_reconciliation_id",
                table: "review_approvals");

            migrationBuilder.DropIndex(
                name: "IX_review_approvals_reservation_id",
                table: "review_approvals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_review_approvals_safety_evidence",
                table: "review_approvals");

            migrationBuilder.DropIndex(
                name: "IX_executable_session_preflights_reconciliation_id",
                table: "executable_session_preflights");

            migrationBuilder.DropCheckConstraint(
                name: "ck_executable_session_preflights_reconciliation",
                table: "executable_session_preflights");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "reconciliation_id",
                table: "risk_evaluations");

            migrationBuilder.DropColumn(
                name: "reconciliation_id",
                table: "review_approvals");

            migrationBuilder.DropColumn(
                name: "reservation_id",
                table: "review_approvals");

            migrationBuilder.DropColumn(
                name: "reconciliation_id",
                table: "executable_session_preflights");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'SESSION_PREFLIGHT_COMPLETED', 'DECISION_RECORDED', 'RISK_EVALUATED', 'EXECUTION_INTENT_CREATED', 'EXECUTION_INTENT_STATE_CHANGED', 'REVIEW_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");
        }

        internal const string TeardownSql = """
            DROP TRIGGER IF EXISTS protect_broker_order_event_write ON broker_order_events;
            DROP TRIGGER IF EXISTS protect_broker_order_event_delete ON broker_order_events;
            DROP FUNCTION IF EXISTS protect_broker_order_event();
            DROP TRIGGER IF EXISTS protect_order_write ON orders;
            DROP TRIGGER IF EXISTS protect_order_delete ON orders;
            DROP FUNCTION IF EXISTS protect_order();
            DROP TRIGGER IF EXISTS protect_pre_submit_evaluation_write ON pre_submit_evaluations;
            DROP TRIGGER IF EXISTS protect_pre_submit_evaluation_delete ON pre_submit_evaluations;
            DROP FUNCTION IF EXISTS protect_pre_submit_evaluation();
            DROP TRIGGER IF EXISTS protect_exposure_reservation_write ON exposure_reservations;
            DROP TRIGGER IF EXISTS protect_exposure_reservation_delete ON exposure_reservations;
            DROP FUNCTION IF EXISTS protect_exposure_reservation();
            DROP TRIGGER IF EXISTS protect_kill_switch_write ON kill_switches;
            DROP TRIGGER IF EXISTS protect_kill_switch_delete ON kill_switches;
            DROP FUNCTION IF EXISTS protect_kill_switch();
            DROP TRIGGER IF EXISTS protect_reconciliation_write ON reconciliations;
            DROP TRIGGER IF EXISTS protect_reconciliation_delete ON reconciliations;
            DROP FUNCTION IF EXISTS protect_reconciliation();
            DROP TRIGGER IF EXISTS enforce_review_safety_write ON review_approvals;
            DROP TRIGGER IF EXISTS enforce_risk_safety_write ON risk_evaluations;
            DROP TRIGGER IF EXISTS enforce_preflight_safety_write ON executable_session_preflights;
            DROP FUNCTION IF EXISTS enforce_review_safety();
            DROP FUNCTION IF EXISTS enforce_risk_safety();
            DROP FUNCTION IF EXISTS enforce_preflight_safety();
            """;

        internal const string ProtectionSql = """
            CREATE FUNCTION protect_reconciliation() RETURNS trigger AS $$
            BEGIN
                IF TG_OP = 'DELETE' OR TG_OP = 'UPDATE' THEN
                    RAISE EXCEPTION 'Reconciliation records are append-only.';
                END IF;
                IF NEW.result = 'CURRENT' AND NOT EXISTS (
                    SELECT 1 FROM goal_versions AS goal
                    WHERE goal.goal_version_id = NEW.goal_version_id
                      AND goal.mode = 'PAPER_REVIEW'
                      AND goal.definition_json->'executionBinding'->'brokerConnectionVersion' = NEW.broker_connection_version
                      AND goal.definition_json->'executionBinding'->>'endpointIdentity' = NEW.endpoint_identity
                      AND goal.definition_json->'executionBinding'->>'expectedAccountIdentifier' = NEW.account_identifier
                      AND goal.definition_json->'executionBinding'->'reconciliationPolicyVersion' = NEW.reconciliation_policy_version
                    FOR SHARE) THEN
                    RAISE EXCEPTION 'Current reconciliation authority is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_reconciliation_write BEFORE INSERT OR UPDATE ON reconciliations
                FOR EACH ROW EXECUTE FUNCTION protect_reconciliation();
            CREATE TRIGGER protect_reconciliation_delete BEFORE DELETE ON reconciliations
                FOR EACH ROW EXECUTE FUNCTION protect_reconciliation();

            CREATE FUNCTION protect_kill_switch() RETURNS trigger AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Kill-switch state cannot be removed.';
                END IF;
                IF TG_OP = 'INSERT' THEN
                    IF NEW.state <> 'CLEAR' OR NEW.revision <> 0 THEN
                        RAISE EXCEPTION 'A kill switch must start clear.';
                    END IF;
                    RETURN NEW;
                END IF;
                IF NEW.kill_switch_id IS DISTINCT FROM OLD.kill_switch_id
                    OR NEW.scope_type IS DISTINCT FROM OLD.scope_type
                    OR NEW.scope_identifier IS DISTINCT FROM OLD.scope_identifier
                    OR NEW.configured_at IS DISTINCT FROM OLD.configured_at
                    OR NEW.revision <> OLD.revision + 1
                    OR NOT ((OLD.state IN ('CLEAR', 'RECOVERY_REQUIRED') AND NEW.state = 'TRIGGERED')
                        OR (OLD.state = 'TRIGGERED' AND NEW.state = 'RECOVERY_REQUIRED')
                        OR (OLD.state = 'RECOVERY_REQUIRED' AND NEW.state = 'CLEAR')) THEN
                    RAISE EXCEPTION 'Kill-switch transition is invalid.';
                END IF;
                IF NEW.state IN ('RECOVERY_REQUIRED', 'CLEAR') AND NOT EXISTS (
                    SELECT 1 FROM reconciliations
                    WHERE reconciliation_id = NEW.recovery_reconciliation_id
                      AND result = 'CURRENT'
                      AND completed_at >= OLD.triggered_at
                    FOR SHARE) THEN
                    RAISE EXCEPTION 'Kill-switch recovery evidence is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_kill_switch_write BEFORE INSERT OR UPDATE ON kill_switches
                FOR EACH ROW EXECUTE FUNCTION protect_kill_switch();
            CREATE TRIGGER protect_kill_switch_delete BEFORE DELETE ON kill_switches
                FOR EACH ROW EXECUTE FUNCTION protect_kill_switch();

            CREATE FUNCTION enforce_preflight_safety() RETURNS trigger AS $$
            BEGIN
                IF NEW.result = 'PASSED' AND NOT EXISTS (
                    SELECT 1 FROM reconciliations AS reconciliation
                    WHERE reconciliation.reconciliation_id = NEW.reconciliation_id
                      AND reconciliation.result = 'CURRENT'
                      AND reconciliation.completed_at <= NEW.completed_at
                      AND reconciliation.broker_connection_version = NEW.broker_connection_version
                      AND reconciliation.environment = NEW.observed_environment
                      AND reconciliation.endpoint_identity = NEW.observed_endpoint_identity
                      AND reconciliation.account_identifier = NEW.observed_account_identifier
                      AND 4 = (SELECT count(*) FROM kill_switches WHERE state = 'CLEAR' AND (
                        (scope_type = 'SYSTEM' AND scope_identifier = NEW.observed_environment)
                        OR (scope_type = 'CONNECTION' AND scope_identifier = NEW.broker_connection_version->>'digest')
                        OR (scope_type = 'ACCOUNT' AND scope_identifier = NEW.observed_account_identifier)
                        OR (scope_type = 'GOAL' AND scope_identifier = NEW.goal_version_id::text)))
                    FOR SHARE) THEN
                    RAISE EXCEPTION 'Passed preflight lacks durable safety evidence.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER enforce_preflight_safety_write BEFORE INSERT ON executable_session_preflights
                FOR EACH ROW EXECUTE FUNCTION enforce_preflight_safety();

            CREATE FUNCTION enforce_risk_safety() RETURNS trigger AS $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM reconciliations AS reconciliation
                    WHERE reconciliation.reconciliation_id = NEW.reconciliation_id
                      AND reconciliation.result = 'CURRENT'
                      AND reconciliation.completed_at <= NEW.evaluated_at
                      AND reconciliation.position_exposure = NEW.position_exposure
                      AND reconciliation.open_order_exposure = NEW.open_order_exposure
                      AND reconciliation.buying_power = NEW.buying_power
                      AND reconciliation.currency = NEW.currency
                      AND 4 = (SELECT count(*) FROM kill_switches WHERE state = 'CLEAR' AND (
                        (scope_type = 'SYSTEM' AND scope_identifier = reconciliation.environment)
                        OR (scope_type = 'CONNECTION' AND scope_identifier = reconciliation.broker_connection_version->>'digest')
                        OR (scope_type = 'ACCOUNT' AND scope_identifier = reconciliation.account_identifier)
                        OR (scope_type = 'GOAL' AND scope_identifier = NEW.goal_version_id::text)))
                    FOR SHARE) THEN
                    RAISE EXCEPTION 'Risk evaluation lacks durable safety evidence.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER enforce_risk_safety_write BEFORE INSERT ON risk_evaluations
                FOR EACH ROW EXECUTE FUNCTION enforce_risk_safety();

            CREATE FUNCTION protect_exposure_reservation() RETURNS trigger AS $$
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
                        OR OLD.state <> 'RESERVED' OR NEW.state <> 'RELEASED'
                        OR NEW.revision <> OLD.revision + 1 THEN
                        RAISE EXCEPTION 'Exposure reservation transition is invalid.';
                    END IF;
                    RETURN NEW;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM execution_intents AS intent
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
                SELECT COALESCE(sum(amount), 0) INTO reserved_exposure FROM exposure_reservations
                    WHERE allocation_entry_id = NEW.allocation_entry_id AND state = 'RESERVED';
                IF authorized_amount IS NULL OR current_exposure IS NULL
                    OR current_exposure + reserved_exposure + NEW.amount > authorized_amount
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
            CREATE TRIGGER protect_exposure_reservation_write BEFORE INSERT OR UPDATE ON exposure_reservations
                FOR EACH ROW EXECUTE FUNCTION protect_exposure_reservation();
            CREATE TRIGGER protect_exposure_reservation_delete BEFORE DELETE ON exposure_reservations
                FOR EACH ROW EXECUTE FUNCTION protect_exposure_reservation();

            CREATE FUNCTION enforce_review_safety() RETURNS trigger AS $$
            BEGIN
                IF NEW.result = 'APPROVED' AND NOT EXISTS (
                    SELECT 1 FROM execution_intents AS intent
                    JOIN exposure_reservations AS reservation ON reservation.reservation_id = NEW.reservation_id
                    JOIN reconciliations AS reconciliation ON reconciliation.reconciliation_id = NEW.reconciliation_id
                    WHERE intent.intent_id = NEW.intent_id AND intent.state = 'REVIEW_PENDING'
                      AND reservation.intent_id = intent.intent_id AND reservation.state = 'RESERVED'
                      AND reservation.amount = intent.quantity * intent.limit_price
                      AND reconciliation.goal_version_id = intent.goal_version_id
                      AND reconciliation.result = 'CURRENT'
                      AND reconciliation.completed_at <= NEW.action_time
                      AND 4 = (SELECT count(*) FROM kill_switches WHERE state = 'CLEAR' AND (
                        (scope_type = 'SYSTEM' AND scope_identifier = intent.environment)
                        OR (scope_type = 'CONNECTION' AND scope_identifier = intent.broker_connection_version->>'digest')
                        OR (scope_type = 'ACCOUNT' AND scope_identifier = intent.account_identifier)
                        OR (scope_type = 'GOAL' AND scope_identifier = intent.goal_version_id::text)))
                    FOR SHARE OF intent, reservation, reconciliation) THEN
                    RAISE EXCEPTION 'Review approval lacks durable safety evidence.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER enforce_review_safety_write BEFORE INSERT ON review_approvals
                FOR EACH ROW EXECUTE FUNCTION enforce_review_safety();

            CREATE FUNCTION protect_pre_submit_evaluation() RETURNS trigger AS $$
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
            CREATE TRIGGER protect_pre_submit_evaluation_write BEFORE INSERT OR UPDATE ON pre_submit_evaluations
                FOR EACH ROW EXECUTE FUNCTION protect_pre_submit_evaluation();
            CREATE TRIGGER protect_pre_submit_evaluation_delete BEFORE DELETE ON pre_submit_evaluations
                FOR EACH ROW EXECUTE FUNCTION protect_pre_submit_evaluation();

            CREATE FUNCTION protect_order() RETURNS trigger AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Order records cannot be removed.';
                END IF;
                IF TG_OP = 'INSERT' THEN
                    IF NEW.state <> 'SUBMITTING' OR NOT EXISTS (
                        SELECT 1 FROM execution_intents AS intent
                        JOIN pre_submit_evaluations AS evaluation ON evaluation.evaluation_id = NEW.evaluation_id
                        WHERE intent.intent_id = NEW.intent_id
                          AND intent.state = 'APPROVED'
                          AND intent.content_digest = NEW.intent_digest
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
                    OR NOT ((OLD.state = 'SUBMITTING' AND NEW.state IN ('ACKNOWLEDGED', 'REJECTED', 'SUBMISSION_UNKNOWN'))
                        OR (OLD.state = 'SUBMISSION_UNKNOWN' AND NEW.state IN ('ACKNOWLEDGED', 'RECONCILIATION_REQUIRED'))) THEN
                    RAISE EXCEPTION 'Order transition is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_order_write BEFORE INSERT OR UPDATE ON orders
                FOR EACH ROW EXECUTE FUNCTION protect_order();
            CREATE TRIGGER protect_order_delete BEFORE DELETE ON orders
                FOR EACH ROW EXECUTE FUNCTION protect_order();

            CREATE FUNCTION protect_broker_order_event() RETURNS trigger AS $$
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    RAISE EXCEPTION 'Broker Order events are append-only.';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM orders
                    WHERE order_id = NEW.order_id
                      AND broker_client_order_id = NEW.broker_client_order_id
                    FOR SHARE) THEN
                    RAISE EXCEPTION 'Broker Order event binding is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_broker_order_event_write BEFORE INSERT OR UPDATE ON broker_order_events
                FOR EACH ROW EXECUTE FUNCTION protect_broker_order_event();
            CREATE TRIGGER protect_broker_order_event_delete BEFORE DELETE ON broker_order_events
                FOR EACH ROW EXECUTE FUNCTION protect_broker_order_event();

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
                        OR NEW.side IS DISTINCT FROM OLD.side OR NEW.order_type IS DISTINCT FROM OLD.order_type
                        OR NEW.quantity IS DISTINCT FROM OLD.quantity OR NEW.limit_price IS DISTINCT FROM OLD.limit_price
                        OR NEW.time_in_force IS DISTINCT FROM OLD.time_in_force
                        OR NEW.expires_at IS DISTINCT FROM OLD.expires_at
                        OR NEW.risk_evaluation_id IS DISTINCT FROM OLD.risk_evaluation_id
                        OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
                        OR NEW.created_at IS DISTINCT FROM OLD.created_at
                        OR NEW.content_digest IS DISTINCT FROM OLD.content_digest THEN
                        RAISE EXCEPTION 'Execution intent immutable content changed.';
                    END IF;
                    IF OLD.state = 'REVIEW_PENDING' AND NEW.state IN ('APPROVED', 'REJECTED') THEN
                        SELECT result, intent_digest INTO review_result, review_digest
                        FROM review_approvals WHERE intent_id = NEW.intent_id FOR SHARE;
                        IF review_result IS DISTINCT FROM NEW.state OR review_digest IS DISTINCT FROM NEW.content_digest THEN
                            RAISE EXCEPTION 'Execution intent state lacks review evidence.';
                        END IF;
                    ELSIF OLD.state = 'APPROVED' AND NEW.state = 'SUBMISSION_PENDING' THEN
                        IF NOT EXISTS (SELECT 1 FROM orders WHERE intent_id = NEW.intent_id AND state = 'SUBMITTING' FOR SHARE) THEN
                            RAISE EXCEPTION 'Execution intent state lacks prepared Order evidence.';
                        END IF;
                    ELSIF OLD.state IN ('SUBMISSION_PENDING', 'SUBMISSION_UNKNOWN')
                        AND ((NEW.state = 'SUBMITTED' AND EXISTS (SELECT 1 FROM orders WHERE intent_id = NEW.intent_id AND state = 'ACKNOWLEDGED' FOR SHARE))
                          OR (OLD.state = 'SUBMISSION_PENDING' AND NEW.state = 'SUBMISSION_UNKNOWN' AND EXISTS (SELECT 1 FROM orders WHERE intent_id = NEW.intent_id AND state = 'SUBMISSION_UNKNOWN' FOR SHARE))
                          OR (NEW.state = 'FAILED' AND EXISTS (SELECT 1 FROM orders WHERE intent_id = NEW.intent_id AND state IN ('REJECTED', 'RECONCILIATION_REQUIRED') FOR SHARE))) THEN
                        NULL;
                    ELSE
                        RAISE EXCEPTION 'Execution intent transition is invalid.';
                    END IF;
                    RETURN NEW;
                END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM risk_evaluations AS risk
                    JOIN decisions AS decision ON decision.decision_id = NEW.decision_id
                    JOIN sessions AS stored_session ON stored_session.session_id = NEW.session_id
                    JOIN goal_versions AS goal ON goal.goal_version_id = NEW.goal_version_id
                    WHERE risk.risk_evaluation_id = NEW.risk_evaluation_id
                      AND risk.intent_id = NEW.intent_id AND risk.decision_id = NEW.decision_id
                      AND risk.goal_version_id = NEW.goal_version_id AND risk.session_id = NEW.session_id
                      AND decision.session_id = NEW.session_id AND decision.goal_version_id = NEW.goal_version_id
                      AND stored_session.state = 'RUNNING' AND stored_session.mode = 'PAPER_REVIEW'
                      AND NEW.environment = 'PAPER'
                      AND NEW.endpoint_identity = goal.definition_json->'executionBinding'->>'endpointIdentity'
                      AND NEW.account_identifier = goal.definition_json->'executionBinding'->>'expectedAccountIdentifier'
                      AND NEW.broker_connection_version = goal.definition_json->'executionBinding'->'brokerConnectionVersion'
                      AND risk.requested_exposure = NEW.quantity * NEW.limit_price
                      AND ((risk.result = 'ACCEPTED' AND NEW.state = 'REVIEW_PENDING')
                        OR (risk.result = 'REJECTED' AND NEW.state = 'RISK_REJECTED'))
                    FOR SHARE OF risk, decision, stored_session, goal) THEN
                    RAISE EXCEPTION 'Execution intent binding is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            """;
    }
}
