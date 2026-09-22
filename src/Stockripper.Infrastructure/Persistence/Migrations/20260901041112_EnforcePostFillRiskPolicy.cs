using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforcePostFillRiskPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_post_fill_evaluations_result_consistency",
                table: "post_fill_evaluations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_post_fill_evaluations_values",
                table: "post_fill_evaluations");

            migrationBuilder.AddColumn<decimal>(
                name: "largest_instrument_exposure",
                table: "post_fill_evaluations",
                type: "numeric(38,12)",
                precision: 38,
                scale: 12,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "maximum_concentration",
                table: "post_fill_evaluations",
                type: "numeric(18,12)",
                precision: 18,
                scale: 12,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "maximum_portfolio_exposure",
                table: "post_fill_evaluations",
                type: "numeric(38,12)",
                precision: 38,
                scale: 12,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "maximum_position_exposure",
                table: "post_fill_evaluations",
                type: "numeric(38,12)",
                precision: 38,
                scale: 12,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(
                """
                ALTER TABLE post_fill_evaluations
                    DISABLE TRIGGER protect_post_fill_evaluation_write;
                UPDATE post_fill_evaluations
                SET largest_instrument_exposure = position_exposure,
                    maximum_position_exposure = allocation_amount,
                    maximum_portfolio_exposure = allocation_amount,
                    maximum_concentration = 1;
                ALTER TABLE post_fill_evaluations
                    ENABLE TRIGGER protect_post_fill_evaluation_write;
                ALTER TABLE post_fill_evaluations
                    ALTER COLUMN largest_instrument_exposure DROP DEFAULT,
                    ALTER COLUMN maximum_position_exposure DROP DEFAULT,
                    ALTER COLUMN maximum_portfolio_exposure DROP DEFAULT,
                    ALTER COLUMN maximum_concentration DROP DEFAULT;
                """);

            InstallPostFillProtection(migrationBuilder, includePolicyEvidence: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_post_fill_evaluations_result_consistency",
                table: "post_fill_evaluations",
                sql: "(result = 'PASSED' AND position_evidence_current AND position_exposure + open_order_exposure <= allocation_amount AND largest_instrument_exposure <= maximum_position_exposure AND position_exposure + open_order_exposure <= maximum_portfolio_exposure AND (position_exposure + open_order_exposure = 0 OR largest_instrument_exposure / (position_exposure + open_order_exposure) <= maximum_concentration)) OR (result = 'VIOLATION_LOCKED' AND (NOT position_evidence_current OR position_exposure + open_order_exposure > allocation_amount OR largest_instrument_exposure > maximum_position_exposure OR position_exposure + open_order_exposure > maximum_portfolio_exposure OR (position_exposure + open_order_exposure > 0 AND largest_instrument_exposure / (position_exposure + open_order_exposure) > maximum_concentration)))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_post_fill_evaluations_values",
                table: "post_fill_evaluations",
                sql: "position_exposure >= 0 AND open_order_exposure >= 0 AND allocation_amount > 0 AND largest_instrument_exposure >= 0 AND largest_instrument_exposure <= position_exposure + open_order_exposure AND maximum_position_exposure > 0 AND maximum_portfolio_exposure >= maximum_position_exposure AND maximum_concentration > 0 AND maximum_concentration <= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            InstallPostFillProtection(migrationBuilder, includePolicyEvidence: false);

            migrationBuilder.DropCheckConstraint(
                name: "ck_post_fill_evaluations_result_consistency",
                table: "post_fill_evaluations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_post_fill_evaluations_values",
                table: "post_fill_evaluations");

            migrationBuilder.DropColumn(
                name: "largest_instrument_exposure",
                table: "post_fill_evaluations");

            migrationBuilder.DropColumn(
                name: "maximum_concentration",
                table: "post_fill_evaluations");

            migrationBuilder.DropColumn(
                name: "maximum_portfolio_exposure",
                table: "post_fill_evaluations");

            migrationBuilder.DropColumn(
                name: "maximum_position_exposure",
                table: "post_fill_evaluations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_post_fill_evaluations_result_consistency",
                table: "post_fill_evaluations",
                sql: "(result = 'PASSED' AND position_evidence_current AND position_exposure + open_order_exposure <= allocation_amount) OR (result = 'VIOLATION_LOCKED' AND (NOT position_evidence_current OR position_exposure + open_order_exposure > allocation_amount))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_post_fill_evaluations_values",
                table: "post_fill_evaluations",
                sql: "position_exposure >= 0 AND open_order_exposure >= 0 AND allocation_amount > 0");
        }

        private static void InstallPostFillProtection(
            MigrationBuilder migrationBuilder,
            bool includePolicyEvidence)
        {
            var largestExposureCheck = includePolicyEvidence
                ? """
                      AND NEW.largest_instrument_exposure = COALESCE((
                        SELECT max(instrument_exposure.exposure)
                        FROM (
                          SELECT exposure_input.instrument_identifier,
                                 sum(exposure_input.exposure) AS exposure
                          FROM (
                            SELECT position.instrument_identifier,
                                   position.market_value AS exposure
                            FROM position_snapshots AS position
                            WHERE position.batch_id = batch.batch_id
                            UNION ALL
                            SELECT open_intent.instrument_identifier,
                                   open_reservation.remaining_amount AS exposure
                            FROM exposure_reservations AS open_reservation
                            JOIN execution_intents AS open_intent
                              ON open_intent.intent_id = open_reservation.intent_id
                            WHERE open_reservation.allocation_entry_id = allocation.allocation_entry_id
                              AND open_reservation.state = 'RESERVED'
                          ) AS exposure_input
                          GROUP BY exposure_input.instrument_identifier
                        ) AS instrument_exposure), 0)
                  """
                : string.Empty;
            migrationBuilder.Sql(
                $$"""
                CREATE OR REPLACE FUNCTION protect_post_fill_evaluation() RETURNS trigger AS $function$
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
                {{largestExposureCheck}}
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
                $function$ LANGUAGE plpgsql;
                """);
        }
    }
}
