using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stockripper.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnablePaperReviewExpiryTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownSql);
        }

        private const string UpSql = """
            DO $migration$
            DECLARE function_body text;
            DECLARE updated_body text;
            BEGIN
                SELECT prosrc INTO function_body
                FROM pg_proc
                WHERE oid = 'protect_execution_intent()'::regprocedure;

                updated_body := replace(
                    function_body,
                    $needle$ELSIF ((OLD.state = 'APPROVED' AND OLD.mode = 'PAPER_REVIEW')$needle$,
                    $replacement$ELSIF OLD.mode = 'PAPER_REVIEW'
                        AND OLD.state = 'REVIEW_PENDING'
                        AND NEW.state = 'EXPIRED'
                        AND OLD.expires_at <= CURRENT_TIMESTAMP THEN
                        NULL;
                    ELSIF OLD.mode = 'PAPER_REVIEW'
                        AND OLD.state = 'APPROVED'
                        AND NEW.state = 'EXPIRED'
                        AND EXISTS (
                            SELECT 1 FROM review_approvals
                            WHERE intent_id = NEW.intent_id
                              AND result = 'APPROVED'
                              AND intent_digest = NEW.content_digest
                              AND expires_at <= CURRENT_TIMESTAMP
                            FOR SHARE) THEN
                        NULL;
                    ELSIF ((OLD.state = 'APPROVED' AND OLD.mode = 'PAPER_REVIEW')$replacement$);

                IF updated_body = function_body THEN
                    RAISE EXCEPTION 'Execution intent expiry protection update did not match.';
                END IF;

                EXECUTE 'CREATE OR REPLACE FUNCTION protect_execution_intent() '
                    || 'RETURNS trigger AS $function$' || updated_body
                    || '$function$ LANGUAGE plpgsql';
            END;
            $migration$;
            """;

        private const string DownSql = """
            DO $migration$
            DECLARE function_body text;
            DECLARE updated_body text;
            BEGIN
                SELECT prosrc INTO function_body
                FROM pg_proc
                WHERE oid = 'protect_execution_intent()'::regprocedure;

                updated_body := replace(
                    function_body,
                    $needle$ELSIF OLD.mode = 'PAPER_REVIEW'
                        AND OLD.state = 'REVIEW_PENDING'
                        AND NEW.state = 'EXPIRED'
                        AND OLD.expires_at <= CURRENT_TIMESTAMP THEN
                        NULL;
                    ELSIF OLD.mode = 'PAPER_REVIEW'
                        AND OLD.state = 'APPROVED'
                        AND NEW.state = 'EXPIRED'
                        AND EXISTS (
                            SELECT 1 FROM review_approvals
                            WHERE intent_id = NEW.intent_id
                              AND result = 'APPROVED'
                              AND intent_digest = NEW.content_digest
                              AND expires_at <= CURRENT_TIMESTAMP
                            FOR SHARE) THEN
                        NULL;
                    ELSIF ((OLD.state = 'APPROVED' AND OLD.mode = 'PAPER_REVIEW')$needle$,
                    $replacement$ELSIF ((OLD.state = 'APPROVED' AND OLD.mode = 'PAPER_REVIEW')$replacement$);

                IF updated_body = function_body THEN
                    RAISE EXCEPTION 'Execution intent expiry protection rollback did not match.';
                END IF;

                EXECUTE 'CREATE OR REPLACE FUNCTION protect_execution_intent() '
                    || 'RETURNS trigger AS $function$' || updated_body
                    || '$function$ LANGUAGE plpgsql';
            END;
            $migration$;
            """;
    }
}
