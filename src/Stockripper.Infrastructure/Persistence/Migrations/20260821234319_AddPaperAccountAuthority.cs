using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaperAccountAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.CreateTable(
                name: "account_allocation_limits",
                columns: table => new
                {
                    reference_digest = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    reference = table.Column<string>(type: "jsonb", nullable: false),
                    broker_connection_version = table.Column<string>(type: "jsonb", nullable: false),
                    environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    endpoint_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    maximum_amount = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_allocation_limits", x => x.reference_digest);
                    table.CheckConstraint("ck_account_allocation_limits_amount", "maximum_amount > 0");
                    table.CheckConstraint("ck_account_allocation_limits_binding", "reference->>'digest' = reference_digest AND length(btrim(endpoint_identity)) > 0 AND length(btrim(account_identifier)) > 0");
                    table.CheckConstraint("ck_account_allocation_limits_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_account_allocation_limits_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_account_allocation_limits_environment", "environment = 'PAPER'");
                });

            migrationBuilder.CreateTable(
                name: "account_allocation_entries",
                columns: table => new
                {
                    allocation_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    activation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_limit_digest = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_limit_version = table.Column<string>(type: "jsonb", nullable: false),
                    broker_connection_digest = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    broker_connection_version = table.Column<string>(type: "jsonb", nullable: false),
                    environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    endpoint_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    allocation_amount = table.Column<decimal>(type: "numeric(38,12)", precision: 38, scale: 12, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    exposure_method_identifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    effective_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    observed_environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    observed_endpoint_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    observed_account_identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    observed_capabilities = table.Column<string>(type: "jsonb", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_allocation_entries", x => x.allocation_entry_id);
                    table.CheckConstraint("ck_account_allocation_entries_amount", "allocation_amount > 0");
                    table.CheckConstraint("ck_account_allocation_entries_binding", "account_limit_version->>'digest' = account_limit_digest AND broker_connection_version->>'digest' = broker_connection_digest AND length(btrim(exposure_method_identifier)) > 0");
                    table.CheckConstraint("ck_account_allocation_entries_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_account_allocation_entries_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_account_allocation_entries_end", "(state = 'ACTIVE' AND effective_end IS NULL) OR (state = 'RELEASED' AND effective_end IS NOT NULL AND effective_end >= effective_start)");
                    table.CheckConstraint("ck_account_allocation_entries_environment", "environment = 'PAPER'");
                    table.CheckConstraint("ck_account_allocation_entries_observation", "observed_environment = 'PAPER' AND observed_environment = environment AND observed_endpoint_identity = endpoint_identity AND observed_account_identifier = account_identifier");
                    table.CheckConstraint("ck_account_allocation_entries_state", "state IN ('ACTIVE', 'RELEASED')");
                    table.ForeignKey(
                        name: "FK_account_allocation_entries_account_allocation_limits_accoun~",
                        column: x => x.account_limit_digest,
                        principalTable: "account_allocation_limits",
                        principalColumn: "reference_digest",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_account_allocation_entries_goal_activations_activation_id",
                        column: x => x.activation_id,
                        principalTable: "goal_activations",
                        principalColumn: "activation_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_account_allocation_entries_goal_versions_goal_version_id",
                        column: x => x.goal_version_id,
                        principalTable: "goal_versions",
                        principalColumn: "goal_version_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'ACCOUNT_ALLOCATION_LIMIT_CONFIGURED', 'BROKER_CONNECTION_VERIFIED', 'ACCOUNT_ALLOCATION_RESERVED', 'ACCOUNT_ALLOCATION_RELEASED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'DECISION_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");

            migrationBuilder.CreateIndex(
                name: "IX_account_allocation_entries_account_limit_digest",
                table: "account_allocation_entries",
                column: "account_limit_digest");

            migrationBuilder.CreateIndex(
                name: "IX_account_allocation_entries_activation_id",
                table: "account_allocation_entries",
                column: "activation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_allocation_entries_broker_connection_digest_account~",
                table: "account_allocation_entries",
                columns: new[] { "broker_connection_digest", "account_identifier", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_account_allocation_entries_goal_version_id",
                table: "account_allocation_entries",
                column: "goal_version_id");

            migrationBuilder.Sql(ProtectionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_allocation_entries");

            migrationBuilder.DropTable(
                name: "account_allocation_limits");

            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS protect_account_allocation_entry(); DROP FUNCTION IF EXISTS protect_account_allocation_limit();");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_type",
                table: "audit_events",
                sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'DECISION_RECORDED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'RESEARCH_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");
        }

        private const string ProtectionSql = """
            CREATE FUNCTION protect_account_allocation_limit() RETURNS trigger AS $$
            BEGIN
                IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
                    RAISE EXCEPTION 'Account allocation limit records cannot be removed.';
                END IF;
                IF TG_OP = 'UPDATE' THEN
                    RAISE EXCEPTION 'Account allocation limit records are immutable.';
                END IF;
                IF jsonb_typeof(NEW.reference) IS DISTINCT FROM 'object'
                    OR jsonb_typeof(NEW.broker_connection_version) IS DISTINCT FROM 'object'
                    OR NEW.reference->>'digest' IS DISTINCT FROM NEW.reference_digest THEN
                    RAISE EXCEPTION 'Account allocation limit binding is invalid.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_account_allocation_limit_insert_or_update
                BEFORE INSERT OR UPDATE ON account_allocation_limits
                FOR EACH ROW EXECUTE FUNCTION protect_account_allocation_limit();
            CREATE TRIGGER protect_account_allocation_limit_delete
                BEFORE DELETE ON account_allocation_limits
                FOR EACH ROW EXECUTE FUNCTION protect_account_allocation_limit();
            CREATE TRIGGER protect_account_allocation_limit_truncate
                BEFORE TRUNCATE ON account_allocation_limits
                FOR EACH STATEMENT EXECUTE FUNCTION protect_account_allocation_limit();

            CREATE FUNCTION protect_account_allocation_entry() RETURNS trigger AS $$
            DECLARE activation_goal_version_id uuid;
            DECLARE activation_state text;
            DECLARE activation_time timestamptz;
            DECLARE goal_definition jsonb;
            DECLARE limit_reference jsonb;
            DECLARE limit_broker_connection jsonb;
            DECLARE limit_environment text;
            DECLARE limit_endpoint text;
            DECLARE limit_account text;
            DECLARE limit_maximum numeric;
            DECLARE limit_currency text;
            DECLARE current_allocation numeric;
            BEGIN
                IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
                    RAISE EXCEPTION 'Account allocation entry records cannot be removed.';
                END IF;
                IF TG_OP = 'UPDATE' THEN
                    IF NEW.allocation_entry_id IS DISTINCT FROM OLD.allocation_entry_id
                        OR NEW.activation_id IS DISTINCT FROM OLD.activation_id
                        OR NEW.goal_version_id IS DISTINCT FROM OLD.goal_version_id
                        OR NEW.account_limit_digest IS DISTINCT FROM OLD.account_limit_digest
                        OR NEW.account_limit_version IS DISTINCT FROM OLD.account_limit_version
                        OR NEW.broker_connection_digest IS DISTINCT FROM OLD.broker_connection_digest
                        OR NEW.broker_connection_version IS DISTINCT FROM OLD.broker_connection_version
                        OR NEW.environment IS DISTINCT FROM OLD.environment
                        OR NEW.endpoint_identity IS DISTINCT FROM OLD.endpoint_identity
                        OR NEW.account_identifier IS DISTINCT FROM OLD.account_identifier
                        OR NEW.allocation_amount IS DISTINCT FROM OLD.allocation_amount
                        OR NEW.currency IS DISTINCT FROM OLD.currency
                        OR NEW.exposure_method_identifier IS DISTINCT FROM OLD.exposure_method_identifier
                        OR NEW.effective_start IS DISTINCT FROM OLD.effective_start
                        OR NEW.observed_environment IS DISTINCT FROM OLD.observed_environment
                        OR NEW.observed_endpoint_identity IS DISTINCT FROM OLD.observed_endpoint_identity
                        OR NEW.observed_account_identifier IS DISTINCT FROM OLD.observed_account_identifier
                        OR NEW.observed_capabilities IS DISTINCT FROM OLD.observed_capabilities
                        OR NEW.observed_at IS DISTINCT FROM OLD.observed_at
                        OR NEW.content_digest IS DISTINCT FROM OLD.content_digest
                        OR OLD.state IS DISTINCT FROM 'ACTIVE'
                        OR NEW.state IS DISTINCT FROM 'RELEASED'
                        OR OLD.effective_end IS NOT NULL
                        OR NEW.effective_end IS NULL THEN
                        RAISE EXCEPTION 'Account allocation immutable authority changed.';
                    END IF;
                    SELECT state INTO activation_state
                        FROM goal_activations
                        WHERE activation_id = NEW.activation_id
                        FOR SHARE;
                    IF activation_state NOT IN ('REVOKED', 'EXPIRED') THEN
                        RAISE EXCEPTION 'Account allocation release needs terminal Goal authority.';
                    END IF;
                    RETURN NEW;
                END IF;

                SELECT activation.goal_version_id, activation.state,
                       activation.activation_time, goal.definition_json
                    INTO activation_goal_version_id, activation_state,
                         activation_time, goal_definition
                    FROM goal_activations AS activation
                    JOIN goal_versions AS goal
                      ON goal.goal_version_id = activation.goal_version_id
                    WHERE activation.activation_id = NEW.activation_id
                    FOR SHARE OF activation, goal;
                IF activation_goal_version_id IS DISTINCT FROM NEW.goal_version_id
                    OR activation_state IS DISTINCT FROM 'ACTIVE'
                    OR activation_time IS DISTINCT FROM NEW.effective_start
                    OR goal_definition->>'mode' IS DISTINCT FROM 'PAPER_REVIEW'
                    OR (goal_definition->>'capitalAllocationAmount')::numeric
                        IS DISTINCT FROM NEW.allocation_amount
                    OR goal_definition->>'capitalAllocationCurrency' IS DISTINCT FROM NEW.currency
                    OR goal_definition->'executionBinding'->>'expectedEnvironment'
                        IS DISTINCT FROM NEW.environment
                    OR goal_definition->'executionBinding'->>'endpointIdentity'
                        IS DISTINCT FROM NEW.endpoint_identity
                    OR goal_definition->'executionBinding'->>'expectedAccountIdentifier'
                        IS DISTINCT FROM NEW.account_identifier
                    OR goal_definition->'executionBinding'->'brokerConnectionVersion'
                        IS DISTINCT FROM NEW.broker_connection_version THEN
                    RAISE EXCEPTION 'Account allocation Goal authority binding is invalid.';
                END IF;
                IF jsonb_typeof(NEW.observed_capabilities) IS DISTINCT FROM 'array'
                    OR NOT (NEW.observed_capabilities @>
                        (goal_definition->'executionBinding'->'requiredCapabilities'))
                    OR NEW.observed_at > NEW.effective_start THEN
                    RAISE EXCEPTION 'Account allocation Broker observation is invalid.';
                END IF;

                PERFORM pg_advisory_xact_lock(hashtextextended(
                    NEW.broker_connection_digest || ':' || NEW.account_identifier,
                    0));
                SELECT reference, broker_connection_version, environment,
                       endpoint_identity, account_identifier, maximum_amount, currency
                    INTO limit_reference, limit_broker_connection, limit_environment,
                         limit_endpoint, limit_account, limit_maximum, limit_currency
                    FROM account_allocation_limits
                    WHERE reference_digest = NEW.account_limit_digest
                    FOR SHARE;
                IF limit_reference IS DISTINCT FROM NEW.account_limit_version
                    OR limit_broker_connection IS DISTINCT FROM NEW.broker_connection_version
                    OR limit_environment IS DISTINCT FROM NEW.environment
                    OR limit_endpoint IS DISTINCT FROM NEW.endpoint_identity
                    OR limit_account IS DISTINCT FROM NEW.account_identifier
                    OR limit_currency IS DISTINCT FROM NEW.currency THEN
                    RAISE EXCEPTION 'Account allocation limit binding is invalid.';
                END IF;
                SELECT COALESCE(SUM(allocation_amount), 0)
                    INTO current_allocation
                    FROM account_allocation_entries
                    WHERE broker_connection_digest = NEW.broker_connection_digest
                      AND account_identifier = NEW.account_identifier
                      AND currency = NEW.currency
                      AND state = 'ACTIVE';
                IF current_allocation + NEW.allocation_amount > limit_maximum THEN
                    RAISE EXCEPTION 'Account allocation limit exceeded.';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER protect_account_allocation_entry_insert_or_update
                BEFORE INSERT OR UPDATE ON account_allocation_entries
                FOR EACH ROW EXECUTE FUNCTION protect_account_allocation_entry();
            CREATE TRIGGER protect_account_allocation_entry_delete
                BEFORE DELETE ON account_allocation_entries
                FOR EACH ROW EXECUTE FUNCTION protect_account_allocation_entry();
            CREATE TRIGGER protect_account_allocation_entry_truncate
                BEFORE TRUNCATE ON account_allocation_entries
                FOR EACH STATEMENT EXECUTE FUNCTION protect_account_allocation_entry();
            """;
    }
}
