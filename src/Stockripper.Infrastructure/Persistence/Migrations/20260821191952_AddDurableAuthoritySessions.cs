using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations;

public partial class AddDurableAuthoritySessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_audit_events_type",
            table: "audit_events");

        migrationBuilder.CreateTable(
            name: "goal_activations",
            columns: table => new
            {
                activation_id = table.Column<Guid>(type: "uuid", nullable: false),
                goal_id = table.Column<Guid>(type: "uuid", nullable: false),
                goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                activation_evidence = table.Column<string>(type: "text", nullable: false),
                activation_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expiry_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                revocation_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                revocation_reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                revision = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_goal_activations", row => row.activation_id);
                table.CheckConstraint("ck_goal_activations_expiry", "expiry_time IS NULL OR expiry_time > activation_time");
                table.CheckConstraint("ck_goal_activations_revision", "revision > 0");
                table.CheckConstraint("ck_goal_activations_revocation", "(state = 'REVOKED' AND revocation_time IS NOT NULL AND revocation_reason IS NOT NULL) OR (state <> 'REVOKED' AND revocation_time IS NULL AND revocation_reason IS NULL)");
                table.CheckConstraint("ck_goal_activations_state", "state IN ('ACTIVE', 'PAUSED', 'REVOKED', 'EXPIRED')");
                table.ForeignKey(
                    name: "FK_goal_activations_goal_versions_goal_version_id",
                    column: row => row.goal_version_id,
                    principalTable: "goal_versions",
                    principalColumn: "goal_version_id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "sessions",
            columns: table => new
            {
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                activation_id = table.Column<Guid>(type: "uuid", nullable: false),
                mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                start_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                end_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                failure_reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                input_data_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                replay_seed = table.Column<long>(type: "bigint", nullable: true),
                revision = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_sessions", row => row.session_id);
                table.CheckConstraint("ck_sessions_failure", "(state = 'FAILED' AND failure_reason IS NOT NULL) OR (state <> 'FAILED' AND failure_reason IS NULL)");
                table.CheckConstraint("ck_sessions_mode", "mode IN ('REPLAY', 'RESEARCH', 'PAPER_REVIEW', 'PAPER_AUTO', 'LIVE_REVIEW', 'LIVE_AUTO')");
                table.CheckConstraint("ck_sessions_replay_input", "(mode = 'REPLAY' AND input_data_snapshot_id IS NOT NULL AND replay_seed IS NOT NULL) OR (mode <> 'REPLAY' AND input_data_snapshot_id IS NULL AND replay_seed IS NULL)");
                table.CheckConstraint("ck_sessions_revision", "revision > 0");
                table.CheckConstraint("ck_sessions_state", "state IN ('CREATED', 'PREFLIGHT', 'READY', 'RUNNING', 'STOPPING', 'RECONCILING', 'COMPLETED', 'FAILED', 'LOCKED')");
                table.CheckConstraint("ck_sessions_terminal", "(state IN ('COMPLETED', 'FAILED') AND end_time IS NOT NULL) OR (state NOT IN ('COMPLETED', 'FAILED') AND end_time IS NULL)");
                table.ForeignKey(
                    name: "FK_sessions_data_snapshots_input_data_snapshot_id",
                    column: row => row.input_data_snapshot_id,
                    principalTable: "data_snapshots",
                    principalColumn: "data_snapshot_id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_sessions_goal_activations_activation_id",
                    column: row => row.activation_id,
                    principalTable: "goal_activations",
                    principalColumn: "activation_id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_sessions_goal_versions_goal_version_id",
                    column: row => row.goal_version_id,
                    principalTable: "goal_versions",
                    principalColumn: "goal_version_id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.AddCheckConstraint(
            name: "ck_audit_events_type",
            table: "audit_events",
            sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'GOAL_ACTIVATED', 'GOAL_ACTIVATION_STATE_CHANGED', 'SESSION_CREATED', 'SESSION_STATE_CHANGED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");

        migrationBuilder.CreateIndex(
            name: "IX_goal_activations_goal_id",
            table: "goal_activations",
            column: "goal_id",
            unique: true,
            filter: "state IN ('ACTIVE', 'PAUSED')");
        migrationBuilder.CreateIndex(
            name: "IX_goal_activations_goal_version_id",
            table: "goal_activations",
            column: "goal_version_id");
        migrationBuilder.CreateIndex(
            name: "IX_sessions_activation_id",
            table: "sessions",
            column: "activation_id");
        migrationBuilder.CreateIndex(
            name: "IX_sessions_goal_version_id",
            table: "sessions",
            column: "goal_version_id");
        migrationBuilder.CreateIndex(
            name: "IX_sessions_input_data_snapshot_id",
            table: "sessions",
            column: "input_data_snapshot_id");

        migrationBuilder.AddForeignKey(
            name: "FK_audit_events_sessions_session_id",
            table: "audit_events",
            column: "session_id",
            principalTable: "sessions",
            principalColumn: "session_id",
            onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(
            name: "FK_replay_results_goal_activations_activation_id",
            table: "replay_results",
            column: "activation_id",
            principalTable: "goal_activations",
            principalColumn: "activation_id",
            onDelete: ReferentialAction.Restrict);
        migrationBuilder.AddForeignKey(
            name: "FK_replay_results_sessions_session_id",
            table: "replay_results",
            column: "session_id",
            principalTable: "sessions",
            principalColumn: "session_id",
            onDelete: ReferentialAction.Restrict);
        migrationBuilder.CreateIndex(
            name: "IX_replay_results_activation_id",
            table: "replay_results",
            column: "activation_id");

        migrationBuilder.Sql(ProtectionSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_audit_events_sessions_session_id", "audit_events");
        migrationBuilder.DropForeignKey("FK_replay_results_goal_activations_activation_id", "replay_results");
        migrationBuilder.DropForeignKey("FK_replay_results_sessions_session_id", "replay_results");
        migrationBuilder.DropIndex("IX_replay_results_activation_id", "replay_results");
        migrationBuilder.DropTable("sessions");
        migrationBuilder.DropTable("goal_activations");
        migrationBuilder.DropCheckConstraint("ck_audit_events_type", "audit_events");
        migrationBuilder.AddCheckConstraint(
            name: "ck_audit_events_type",
            table: "audit_events",
            sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS protect_session_authority(); DROP FUNCTION IF EXISTS protect_goal_activation_authority();");
    }

    private const string ProtectionSql = """
        CREATE FUNCTION protect_goal_activation_authority() RETURNS trigger AS $$
        DECLARE stored_goal_id uuid;
        BEGIN
            IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
                RAISE EXCEPTION 'Goal activation records cannot be removed.';
            END IF;
            SELECT goal_id INTO stored_goal_id FROM goal_versions WHERE goal_version_id = NEW.goal_version_id;
            IF stored_goal_id IS DISTINCT FROM NEW.goal_id THEN
                RAISE EXCEPTION 'Goal activation binding does not match the Goal version.';
            END IF;
            IF TG_OP = 'UPDATE' THEN
                IF NEW.activation_id IS DISTINCT FROM OLD.activation_id
                    OR NEW.goal_id IS DISTINCT FROM OLD.goal_id
                    OR NEW.goal_version_id IS DISTINCT FROM OLD.goal_version_id
                    OR NEW.operator_id IS DISTINCT FROM OLD.operator_id
                    OR NEW.activation_evidence IS DISTINCT FROM OLD.activation_evidence
                    OR NEW.authority_summary IS DISTINCT FROM OLD.authority_summary
                    OR NEW.activation_time IS DISTINCT FROM OLD.activation_time
                    OR NEW.expiry_time IS DISTINCT FROM OLD.expiry_time
                    OR NEW.revision <> OLD.revision + 1 THEN
                    RAISE EXCEPTION 'Goal activation immutable authority changed.';
                END IF;
                IF NOT ((OLD.state = 'ACTIVE' AND NEW.state IN ('PAUSED', 'REVOKED', 'EXPIRED'))
                    OR (OLD.state = 'PAUSED' AND NEW.state IN ('ACTIVE', 'REVOKED', 'EXPIRED'))) THEN
                    RAISE EXCEPTION 'Goal activation state transition is invalid.';
                END IF;
            END IF;
            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;
        CREATE TRIGGER protect_goal_activation_insert_or_update
            BEFORE INSERT OR UPDATE ON goal_activations
            FOR EACH ROW EXECUTE FUNCTION protect_goal_activation_authority();
        CREATE TRIGGER protect_goal_activation_delete
            BEFORE DELETE ON goal_activations
            FOR EACH ROW EXECUTE FUNCTION protect_goal_activation_authority();
        CREATE TRIGGER protect_goal_activation_truncate
            BEFORE TRUNCATE ON goal_activations
            FOR EACH STATEMENT EXECUTE FUNCTION protect_goal_activation_authority();

        CREATE FUNCTION protect_session_authority() RETURNS trigger AS $$
        DECLARE activation_goal_version_id uuid;
        DECLARE activation_state text;
        DECLARE activation_expiry timestamptz;
        DECLARE goal_mode text;
        BEGIN
            IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
                RAISE EXCEPTION 'Session records cannot be removed.';
            END IF;
            IF TG_OP = 'INSERT' THEN
                SELECT goal_version_id, state, expiry_time
                    INTO activation_goal_version_id, activation_state, activation_expiry
                    FROM goal_activations
                    WHERE activation_id = NEW.activation_id
                    FOR SHARE;
                SELECT mode INTO goal_mode
                    FROM goal_versions
                    WHERE goal_version_id = NEW.goal_version_id;
                IF activation_goal_version_id IS DISTINCT FROM NEW.goal_version_id
                    OR goal_mode IS DISTINCT FROM NEW.mode
                    OR activation_state IS DISTINCT FROM 'ACTIVE'
                    OR (activation_expiry IS NOT NULL AND NEW.start_time >= activation_expiry) THEN
                    RAISE EXCEPTION 'Session authority binding is invalid or inactive.';
                END IF;
            END IF;
            IF TG_OP = 'UPDATE' THEN
                IF NEW.session_id IS DISTINCT FROM OLD.session_id
                    OR NEW.goal_version_id IS DISTINCT FROM OLD.goal_version_id
                    OR NEW.activation_id IS DISTINCT FROM OLD.activation_id
                    OR NEW.mode IS DISTINCT FROM OLD.mode
                    OR NEW.start_time IS DISTINCT FROM OLD.start_time
                    OR NEW.input_data_snapshot_id IS DISTINCT FROM OLD.input_data_snapshot_id
                    OR NEW.replay_seed IS DISTINCT FROM OLD.replay_seed
                    OR NEW.revision <> OLD.revision + 1 THEN
                    RAISE EXCEPTION 'Session immutable authority changed.';
                END IF;
                IF NOT ((OLD.state = 'CREATED' AND NEW.state IN ('PREFLIGHT', 'FAILED', 'LOCKED'))
                    OR (OLD.state = 'PREFLIGHT' AND NEW.state IN ('READY', 'FAILED', 'LOCKED'))
                    OR (OLD.state = 'READY' AND NEW.state IN ('RUNNING', 'FAILED', 'LOCKED'))
                    OR (OLD.state = 'RUNNING' AND NEW.state IN ('STOPPING', 'FAILED', 'LOCKED'))
                    OR (OLD.state = 'RUNNING' AND NEW.state = 'COMPLETED' AND OLD.mode IN ('REPLAY', 'RESEARCH'))
                    OR (OLD.state = 'STOPPING' AND NEW.state IN ('RECONCILING', 'FAILED', 'LOCKED'))
                    OR (OLD.state = 'RECONCILING' AND NEW.state IN ('COMPLETED', 'FAILED', 'LOCKED'))
                    OR (OLD.state = 'LOCKED' AND NEW.state IN ('PREFLIGHT', 'FAILED'))) THEN
                    RAISE EXCEPTION 'Session state transition is invalid.';
                END IF;
            END IF;
            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;
        CREATE TRIGGER protect_session_insert_or_update
            BEFORE INSERT OR UPDATE ON sessions
            FOR EACH ROW EXECUTE FUNCTION protect_session_authority();
        CREATE TRIGGER protect_session_delete
            BEFORE DELETE ON sessions
            FOR EACH ROW EXECUTE FUNCTION protect_session_authority();
        CREATE TRIGGER protect_session_truncate
            BEFORE TRUNCATE ON sessions
            FOR EACH STATEMENT EXECUTE FUNCTION protect_session_authority();
        """;
}
