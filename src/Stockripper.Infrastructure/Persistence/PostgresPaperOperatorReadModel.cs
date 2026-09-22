using Npgsql;
using Stockripper.Application.Execution;
using Stockripper.Domain.Sessions;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresPaperOperatorReadModel(NpgsqlDataSource dataSource)
    : IPaperOperatorReadModel
{
    public async ValueTask<PaperOperatorSnapshot?> ReadLatestAsync(
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT goal.goal_version_id,
                   goal.mode,
                   COALESCE(stored_session.observed_environment,
                       goal.definition_json->'executionBinding'->>'expectedEnvironment'),
                   goal.definition_json->'executionBinding'->>'expectedAccountIdentifier',
                   activation.state,
                   stored_session.state,
                   intent.state,
                   order_record.state,
                   reconciliation.result,
                   COALESCE(switches.summary, 'UNCONFIGURED'),
                   preflight.checks
            FROM goal_versions AS goal
            LEFT JOIN LATERAL (
                SELECT state
                FROM goal_activations
                WHERE goal_version_id = goal.goal_version_id
                ORDER BY activation_time DESC
                LIMIT 1
            ) AS activation ON true
            LEFT JOIN LATERAL (
                SELECT session_id, state, observed_environment
                FROM sessions
                WHERE goal_version_id = goal.goal_version_id
                ORDER BY start_time DESC
                LIMIT 1
            ) AS stored_session ON true
            LEFT JOIN LATERAL (
                SELECT intent_id, state
                FROM execution_intents
                WHERE goal_version_id = goal.goal_version_id
                ORDER BY created_at DESC
                LIMIT 1
            ) AS intent ON true
            LEFT JOIN LATERAL (
                SELECT state
                FROM orders
                WHERE intent_id = intent.intent_id
                ORDER BY first_submission_time DESC
                LIMIT 1
            ) AS order_record ON true
            LEFT JOIN LATERAL (
                SELECT result
                FROM reconciliations
                WHERE goal_version_id = goal.goal_version_id
                ORDER BY sequence DESC
                LIMIT 1
            ) AS reconciliation ON true
            LEFT JOIN LATERAL (
                SELECT string_agg(scope_type || ':' || state, ', '
                    ORDER BY scope_type, scope_identifier) AS summary
                FROM kill_switches
                WHERE (scope_type = 'GOAL'
                        AND scope_identifier = goal.goal_version_id::text)
                   OR (scope_type = 'SYSTEM' AND scope_identifier = 'PAPER')
                   OR (scope_type = 'CONNECTION'
                        AND scope_identifier = goal.definition_json
                            ->'executionBinding'->'brokerConnectionVersion'->>'digest')
                   OR (scope_type = 'ACCOUNT'
                        AND scope_identifier = goal.definition_json
                            ->'executionBinding'->>'expectedAccountIdentifier')
            ) AS switches ON true
            LEFT JOIN LATERAL (
                SELECT checks
                FROM executable_session_preflights
                WHERE session_id = stored_session.session_id
                LIMIT 1
            ) AS preflight ON true
            WHERE goal.mode IN ('PAPER_REVIEW', 'PAPER_AUTO')
            ORDER BY goal.created_at DESC
            LIMIT 1
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var activation = ReadNullable(reader, 4);
        var session = ReadNullable(reader, 5);
        var intent = ReadNullable(reader, 6);
        var order = ReadNullable(reader, 7);
        var reconciliation = ReadNullable(reader, 8);
        var switches = reader.GetString(9);
        var preflightChecks = reader.IsDBNull(10)
            ? []
            : PersistenceJson.DeserializePreflightChecks(reader.GetString(10));
        var alerts = new List<string>();
        if (switches.Contains("TRIGGERED", StringComparison.Ordinal)
            || switches.Contains("RECOVERY_REQUIRED", StringComparison.Ordinal))
        {
            alerts.Add("A kill switch blocks new exposure.");
        }
        if (reconciliation is not null and not "CURRENT")
        {
            alerts.Add("Broker reconciliation is not current.");
        }
        if (intent == "RISK_REJECTED")
        {
            alerts.Add("The latest Execution intent failed deterministic risk checks.");
        }
        if (order is "SUBMISSION_UNKNOWN" or "RECONCILIATION_REQUIRED")
        {
            alerts.Add("The latest Broker submission needs reconciliation.");
        }
        if (session == "LOCKED")
        {
            alerts.Add("The latest Session is locked.");
        }
        if (Failed(preflightChecks, ExecutablePreflightCheckCode.ACCOUNT_IDENTITY))
        {
            alerts.Add("The observed Broker account differs from the Goal binding.");
        }
        if (Failed(preflightChecks, ExecutablePreflightCheckCode.ENDPOINT_IDENTITY)
            || Failed(preflightChecks, ExecutablePreflightCheckCode.BROKER_ENVIRONMENT))
        {
            alerts.Add("The observed Broker endpoint or environment differs from the Goal binding.");
        }
        if (Failed(preflightChecks, ExecutablePreflightCheckCode.DATA_SOURCES)
            || Failed(preflightChecks, ExecutablePreflightCheckCode.CAPITAL_DATA)
            || Failed(preflightChecks, ExecutablePreflightCheckCode.BROKER_CLOCK))
        {
            alerts.Add("Market or capital data is stale or unavailable.");
        }
        if (session == "FAILED" && alerts.Count == 0)
        {
            alerts.Add("The latest Session failed executable preflight.");
        }

        return new PaperOperatorSnapshot(reader.GetGuid(0), reader.GetString(1),
            reader.GetString(2), Mask(reader.GetString(3)), activation, session,
            intent, order, reconciliation, switches, alerts);
    }

    private static string? ReadNullable(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static bool Failed(
        IReadOnlyList<ExecutablePreflightCheck> checks,
        ExecutablePreflightCheckCode code) => checks.Any(check =>
            check.Code == code && !check.Passed);

    private static string Mask(string value) => value.Length <= 4
        ? new string('*', value.Length)
        : $"{new string('*', value.Length - 4)}{value[^4..]}";
}
