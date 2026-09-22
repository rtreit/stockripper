using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceReconciliationEvidenceFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_reconciliations_evidence",
                table: "reconciliations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reconciliations_evidence",
                table: "reconciliations",
                sql: "compared_order_range ~ '^orders:sha256:[0-9a-f]{64}$' AND compared_fill_range ~ '^fills:sha256:[0-9a-f]{64}$' AND compared_position_snapshot ~ '^positions:sha256:[0-9a-f]{64}$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_reconciliations_evidence",
                table: "reconciliations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reconciliations_evidence",
                table: "reconciliations",
                sql: "length(compared_order_range) > 0 AND length(compared_fill_range) > 0 AND length(compared_position_snapshot) > 0");
        }
    }
}
