using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations;

[DbContext(typeof(StockripperDbContext))]
[Migration("20260820183000_AddReplaySnapshots")]
public sealed class AddReplaySnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_audit_events_type",
            table: "audit_events");

        migrationBuilder.AddCheckConstraint(
            name: "ck_audit_events_type",
            table: "audit_events",
            sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'DATA_SNAPSHOT_CREATED', 'REPLAY_COMPLETED', 'INTEGRITY_VERIFICATION_FAILED')");

        migrationBuilder.CreateTable(
            name: "data_snapshots",
            columns: table => new
            {
                data_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                data_set_version = table.Column<string>(type: "jsonb", nullable: false),
                data_policy_version = table.Column<string>(type: "jsonb", nullable: false),
                schema_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                range_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                range_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                source_observations = table.Column<string>(type: "jsonb", nullable: false),
                payload = table.Column<byte[]>(type: "bytea", nullable: false),
                payload_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_data_snapshots", x => x.data_snapshot_id);
                table.CheckConstraint("ck_data_snapshots_capture", "captured_at >= range_end");
                table.CheckConstraint("ck_data_snapshots_content_digest", "content_digest ~ '^[0-9a-f]{64}$'");
                table.CheckConstraint("ck_data_snapshots_payload", "octet_length(payload) > 0");
                table.CheckConstraint("ck_data_snapshots_payload_digest", "payload_digest ~ '^[0-9a-f]{64}$'");
                table.CheckConstraint("ck_data_snapshots_range", "range_end >= range_start");
            });

        migrationBuilder.CreateTable(
            name: "replay_results",
            columns: table => new
            {
                replay_result_id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                activation_id = table.Column<Guid>(type: "uuid", nullable: false),
                goal_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                data_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                data_snapshot_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                range_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                range_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                strategy_version = table.Column<string>(type: "jsonb", nullable: false),
                data_policy_version = table.Column<string>(type: "jsonb", nullable: false),
                risk_policy_version = table.Column<string>(type: "jsonb", nullable: false),
                seed = table.Column<long>(type: "bigint", nullable: false),
                output_metrics = table.Column<string>(type: "jsonb", nullable: false),
                evidence = table.Column<string>(type: "text", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                output_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_replay_results", x => x.replay_result_id);
                table.CheckConstraint("ck_replay_results_output_digest", "output_digest ~ '^[0-9a-f]{64}$'");
                table.CheckConstraint("ck_replay_results_range", "range_end >= range_start");
                table.CheckConstraint("ck_replay_results_snapshot_digest", "data_snapshot_digest ~ '^[0-9a-f]{64}$'");
                table.CheckConstraint("ck_replay_results_time", "completed_at >= started_at");
                table.ForeignKey(
                    name: "FK_replay_results_data_snapshots_data_snapshot_id",
                    column: x => x.data_snapshot_id,
                    principalTable: "data_snapshots",
                    principalColumn: "data_snapshot_id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_replay_results_goal_versions_goal_version_id",
                    column: x => x.goal_version_id,
                    principalTable: "goal_versions",
                    principalColumn: "goal_version_id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_replay_results_data_snapshot_id",
            table: "replay_results",
            column: "data_snapshot_id");

        migrationBuilder.CreateIndex(
            name: "IX_replay_results_goal_version_id",
            table: "replay_results",
            column: "goal_version_id");

        migrationBuilder.CreateIndex(
            name: "IX_replay_results_session_id",
            table: "replay_results",
            column: "session_id",
            unique: true);

        migrationBuilder.Sql(
            """
            CREATE FUNCTION reject_replay_foundation_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                RAISE EXCEPTION 'Replay foundation records are immutable.';
            END;
            $function$;

            CREATE TRIGGER data_snapshots_no_update_delete
            BEFORE UPDATE OR DELETE ON data_snapshots
            FOR EACH STATEMENT
            EXECUTE FUNCTION reject_replay_foundation_mutation();

            CREATE TRIGGER data_snapshots_no_truncate
            BEFORE TRUNCATE ON data_snapshots
            FOR EACH STATEMENT
            EXECUTE FUNCTION reject_replay_foundation_mutation();

            CREATE TRIGGER replay_results_no_update_delete
            BEFORE UPDATE OR DELETE ON replay_results
            FOR EACH STATEMENT
            EXECUTE FUNCTION reject_replay_foundation_mutation();

            CREATE TRIGGER replay_results_no_truncate
            BEFORE TRUNCATE ON replay_results
            FOR EACH STATEMENT
            EXECUTE FUNCTION reject_replay_foundation_mutation();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "replay_results");
        migrationBuilder.DropTable(name: "data_snapshots");
        migrationBuilder.Sql("DROP FUNCTION reject_replay_foundation_mutation();");

        migrationBuilder.DropCheckConstraint(
            name: "ck_audit_events_type",
            table: "audit_events");
        migrationBuilder.AddCheckConstraint(
            name: "ck_audit_events_type",
            table: "audit_events",
            sql: "event_type IN ('GOAL_DRAFT_CREATED', 'GOAL_DRAFT_UPDATED', 'GOAL_VERSION_PUBLISHED', 'INTEGRITY_VERIFICATION_FAILED')");
    }
}
