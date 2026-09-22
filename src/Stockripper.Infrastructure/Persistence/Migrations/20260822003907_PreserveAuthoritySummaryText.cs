using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveAuthoritySummaryText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE goal_activations ALTER COLUMN authority_summary TYPE text USING authority_summary::text;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE goal_activations ALTER COLUMN authority_summary TYPE jsonb USING authority_summary::jsonb;");
        }
    }
}
