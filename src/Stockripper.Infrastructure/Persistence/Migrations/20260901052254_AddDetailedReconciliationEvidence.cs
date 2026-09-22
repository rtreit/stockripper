using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDetailedReconciliationEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_reconciliations_details",
                table: "reconciliations");

            migrationBuilder.AddColumn<string>(
                name: "compared_fill_range",
                table: "reconciliations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "compared_order_range",
                table: "reconciliations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "compared_position_snapshot",
                table: "reconciliations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "operator_action",
                table: "reconciliations",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE reconciliations
                SET compared_order_range = 'orders:sha256:' || repeat('0', 64),
                    compared_fill_range = 'fills:sha256:' || repeat('0', 64),
                    compared_position_snapshot = 'positions:sha256:' || repeat('0', 64),
                    operator_action = CASE
                        WHEN result = 'CURRENT' THEN NULL
                        ELSE 'Review this legacy reconciliation before execution.'
                    END
                """);

            migrationBuilder.AlterColumn<string>(
                name: "compared_fill_range",
                table: "reconciliations",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "compared_order_range",
                table: "reconciliations",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "compared_position_snapshot",
                table: "reconciliations",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_reconciliations_details",
                table: "reconciliations",
                sql: "jsonb_typeof(differences) = 'array' AND jsonb_typeof(corrections) = 'array' AND ((result = 'CURRENT' AND jsonb_array_length(differences) = 0 AND operator_action IS NULL) OR (result = 'DIFFERENCE_LOCKED' AND jsonb_array_length(differences) > 0 AND operator_action IS NOT NULL) OR (result = 'FAILED' AND operator_action IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reconciliations_evidence",
                table: "reconciliations",
                sql: "length(compared_order_range) > 0 AND length(compared_fill_range) > 0 AND length(compared_position_snapshot) > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_reconciliations_details",
                table: "reconciliations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reconciliations_evidence",
                table: "reconciliations");

            migrationBuilder.DropColumn(
                name: "compared_fill_range",
                table: "reconciliations");

            migrationBuilder.DropColumn(
                name: "compared_order_range",
                table: "reconciliations");

            migrationBuilder.DropColumn(
                name: "compared_position_snapshot",
                table: "reconciliations");

            migrationBuilder.DropColumn(
                name: "operator_action",
                table: "reconciliations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reconciliations_details",
                table: "reconciliations",
                sql: "jsonb_typeof(differences) = 'array' AND jsonb_typeof(corrections) = 'array' AND ((result = 'CURRENT' AND jsonb_array_length(differences) = 0) OR (result = 'DIFFERENCE_LOCKED' AND jsonb_array_length(differences) > 0) OR result = 'FAILED')");
        }
    }
}
