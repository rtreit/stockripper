using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthoritySummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "authority_summary",
                table: "goal_activations",
                type: "jsonb",
                nullable: true);
            migrationBuilder.Sql(
                """
                UPDATE goal_activations
                SET authority_summary = jsonb_build_object(
                    'schema', 'stockripper-legacy-authority-summary-v0',
                    'activationEvidence', activation_evidence);
                """);
            migrationBuilder.AlterColumn<string>(
                name: "authority_summary",
                table: "goal_activations",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);
            migrationBuilder.Sql(
                """
                CREATE FUNCTION protect_goal_authority_summary() RETURNS trigger AS $$
                BEGIN
                    IF NEW.authority_summary IS DISTINCT FROM OLD.authority_summary THEN
                        RAISE EXCEPTION 'Goal authority summary is immutable.';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER protect_goal_authority_summary_update
                    BEFORE UPDATE ON goal_activations
                    FOR EACH ROW EXECUTE FUNCTION protect_goal_authority_summary();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS protect_goal_authority_summary_update ON goal_activations;
                DROP FUNCTION IF EXISTS protect_goal_authority_summary();
                """);
            migrationBuilder.DropColumn(
                name: "authority_summary",
                table: "goal_activations");
        }
    }
}
