using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Sessions;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresExecutablePreflightPersistence(NpgsqlDataSource dataSource)
    : IExecutablePreflightPersistence
{
    public async ValueTask CompleteAsync(
        Session preflightSession,
        Session completedSession,
        ExecutableSessionPreflight evidence,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        if (preflightSession.State != SessionState.PREFLIGHT
            || completedSession.Id != preflightSession.Id
            || completedSession.Revision != preflightSession.Revision + 1
            || completedSession.State is not (SessionState.READY or SessionState.FAILED)
            || evidence.SessionId != preflightSession.Id
            || evidence.GoalVersionId != preflightSession.GoalVersionId
            || evidence.ActivationId != preflightSession.ActivationId
            || !evidence.HasValidContentDigest()
            || (completedSession.State == SessionState.READY)
                != (evidence.Result == ExecutablePreflightResult.PASSED))
        {
            throw new InvalidDataException("The executable preflight completion is inconsistent.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (evidence.Result == ExecutablePreflightResult.PASSED)
            {
                await EnsureCurrentSafetyAsync(
                    connection, transaction, evidence, cancellationToken)
                    .ConfigureAwait(false);
            }
            await InsertEvidenceAsync(connection, transaction, evidence, cancellationToken)
                .ConfigureAwait(false);
            await UpdateSessionAsync(
                connection,
                transaction,
                preflightSession,
                completedSession,
                cancellationToken).ConfigureAwait(false);
            var related = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["activationId"] = evidence.ActivationId.Value.ToString("D"),
                ["preflightDigest"] = evidence.ContentDigest,
                ["preflightId"] = evidence.Id.Value.ToString("D"),
                ["reconciliationId"] = evidence.ReconciliationId?.Value.ToString("D") ?? "none"
            };
            _ = await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.SESSION_PREFLIGHT_COMPLETED,
                evidence.CompletedAt,
                actorId,
                evidence.GoalVersionId,
                evidence.SessionId,
                related,
                SessionState.PREFLIGHT.ToString(),
                evidence.Result.ToString(),
                "Executable Session preflight completed.",
                "paper-review-session-service",
                cancellationToken).ConfigureAwait(false);
            _ = await PostgresSessionPersistence.AppendAuditAsync(
                connection,
                transaction,
                AuditEventType.SESSION_STATE_CHANGED,
                completedSession,
                actorId,
                evidence.CompletedAt,
                preflightSession.State.ToString(),
                completedSession.FailureReason ?? "Executable Session became ready.",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<ExecutableSessionPreflight?> FindAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT preflight_id, goal_version_id, activation_id,
                   broker_connection_version::text, reconciliation_id, observed_environment,
                   observed_endpoint_identity, observed_account_identifier,
                   observed_capabilities::text, broker_observed_at, broker_time,
                   capital_observed_at, reconciliation_observed_at, currency,
                   position_exposure, open_order_exposure, buying_power,
                   checks::text, result, completed_at, content_digest
            FROM executable_session_preflights
            WHERE session_id = @session_id
            """,
            connection);
        command.Parameters.AddWithValue("session_id", sessionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ExecutableSessionPreflight.Restore(
            new SessionPreflightId(reader.GetGuid(0)),
            sessionId,
            new GoalVersionId(reader.GetGuid(1)),
            new ActivationId(reader.GetGuid(2)),
            PersistenceJson.DeserializeVersion(reader.GetString(3)),
            reader.IsDBNull(4) ? null : new ReconciliationId(reader.GetGuid(4)),
            reader.IsDBNull(5)
                ? null
                : Enum.Parse<BrokerEnvironment>(reader.GetString(5), ignoreCase: false),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            PersistenceJson.DeserializeStrings(reader.GetString(8)),
            ReadTime(reader, 9),
            ReadTime(reader, 10),
            ReadTime(reader, 11),
            ReadTime(reader, 12),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetDecimal(14),
            reader.IsDBNull(15) ? null : reader.GetDecimal(15),
            reader.IsDBNull(16) ? null : reader.GetDecimal(16),
            PersistenceJson.DeserializePreflightChecks(reader.GetString(17)),
            Enum.Parse<ExecutablePreflightResult>(reader.GetString(18), ignoreCase: false),
            reader.GetFieldValue<DateTimeOffset>(19),
            reader.GetString(20));
    }

    private static async ValueTask InsertEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExecutableSessionPreflight evidence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO executable_session_preflights (
                preflight_id, session_id, goal_version_id, activation_id,
                broker_connection_version, reconciliation_id, observed_environment,
                observed_endpoint_identity, observed_account_identifier,
                observed_capabilities, broker_observed_at, broker_time,
                capital_observed_at, reconciliation_observed_at, currency,
                position_exposure, open_order_exposure, buying_power,
                checks, result, completed_at, content_digest)
            VALUES (
                @preflight_id, @session_id, @goal_version_id, @activation_id,
                @broker_connection_version, @reconciliation_id, @observed_environment,
                @observed_endpoint_identity, @observed_account_identifier,
                @observed_capabilities, @broker_observed_at, @broker_time,
                @capital_observed_at, @reconciliation_observed_at, @currency,
                @position_exposure, @open_order_exposure, @buying_power,
                @checks, @result, @completed_at, @content_digest)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("preflight_id", evidence.Id.Value);
        command.Parameters.AddWithValue("session_id", evidence.SessionId.Value);
        command.Parameters.AddWithValue("goal_version_id", evidence.GoalVersionId.Value);
        command.Parameters.AddWithValue("activation_id", evidence.ActivationId.Value);
        command.Parameters.Add("broker_connection_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(evidence.BrokerConnectionVersion);
        AddNullable(command, "reconciliation_id", evidence.ReconciliationId?.Value);
        AddNullable(command, "observed_environment", evidence.ObservedEnvironment?.ToString());
        AddNullable(command, "observed_endpoint_identity", evidence.ObservedEndpointIdentity);
        AddNullable(command, "observed_account_identifier", evidence.ObservedAccountIdentifier);
        command.Parameters.Add("observed_capabilities", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeStrings(evidence.ObservedCapabilities);
        AddNullable(command, "broker_observed_at", evidence.BrokerObservedAt);
        AddNullable(command, "broker_time", evidence.BrokerTime);
        AddNullable(command, "capital_observed_at", evidence.CapitalObservedAt);
        AddNullable(command, "reconciliation_observed_at", evidence.ReconciliationObservedAt);
        command.Parameters.AddWithValue("currency", evidence.Currency);
        AddNullable(command, "position_exposure", evidence.PositionExposure);
        AddNullable(command, "open_order_exposure", evidence.OpenOrderExposure);
        AddNullable(command, "buying_power", evidence.BuyingPower);
        command.Parameters.Add("checks", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializePreflightChecks(evidence.Checks);
        command.Parameters.AddWithValue("result", evidence.Result.ToString());
        command.Parameters.AddWithValue("completed_at", evidence.CompletedAt);
        command.Parameters.AddWithValue("content_digest", evidence.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureCurrentSafetyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExecutableSessionPreflight evidence,
        CancellationToken cancellationToken)
    {
        if (evidence.ReconciliationId is null
            || evidence.ObservedEnvironment != BrokerEnvironment.PAPER
            || string.IsNullOrWhiteSpace(evidence.ObservedEndpointIdentity)
            || string.IsNullOrWhiteSpace(evidence.ObservedAccountIdentifier))
        {
            throw new InvalidOperationException(
                "Current durable preflight safety evidence is invalid.");
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM reconciliations AS reconciliation
                    WHERE reconciliation.reconciliation_id = @reconciliation_id
                      AND reconciliation.goal_version_id = @goal_version_id
                      AND reconciliation.broker_connection_version = @broker_connection_version
                      AND reconciliation.environment = @environment
                      AND reconciliation.endpoint_identity = @endpoint_identity
                      AND reconciliation.account_identifier = @account_identifier
                      AND reconciliation.result = 'CURRENT'
                      AND reconciliation.sequence = (
                          SELECT max(latest.sequence)
                          FROM reconciliations AS latest
                          WHERE latest.goal_version_id = @goal_version_id
                            AND latest.broker_connection_version = @broker_connection_version
                            AND latest.environment = @environment
                            AND latest.endpoint_identity = @endpoint_identity
                            AND latest.account_identifier = @account_identifier)),
                (
                    SELECT count(*)
                    FROM kill_switches
                    WHERE state = 'CLEAR'
                      AND ((scope_type = 'SYSTEM' AND scope_identifier = @environment)
                        OR (scope_type = 'CONNECTION' AND scope_identifier = @connection_digest)
                        OR (scope_type = 'ACCOUNT' AND scope_identifier = @account_identifier)
                        OR (scope_type = 'GOAL' AND scope_identifier = @goal_scope)))
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "reconciliation_id", evidence.ReconciliationId.Value.Value);
        command.Parameters.AddWithValue("goal_version_id", evidence.GoalVersionId.Value);
        command.Parameters.Add("broker_connection_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(evidence.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", evidence.ObservedEnvironment.ToString()!);
        command.Parameters.AddWithValue(
            "endpoint_identity", evidence.ObservedEndpointIdentity!);
        command.Parameters.AddWithValue(
            "account_identifier", evidence.ObservedAccountIdentifier!);
        command.Parameters.AddWithValue(
            "connection_digest", evidence.BrokerConnectionVersion.Digest);
        command.Parameters.AddWithValue("goal_scope", evidence.GoalVersionId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!reader.GetBoolean(0) || reader.GetInt64(1) != 4)
        {
            throw new InvalidOperationException(
                "Current durable preflight safety evidence is invalid.");
        }
    }

    private static async ValueTask UpdateSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Session expected,
        Session completed,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE sessions
            SET state = @state, end_time = @end_time,
                failure_reason = @failure_reason,
                observed_environment = @observed_environment,
                observed_endpoint_identity = @observed_endpoint_identity,
                observed_account_identifier = @observed_account_identifier,
                revision = @revision
            WHERE session_id = @session_id
              AND state = @expected_state
              AND revision = @expected_revision
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("state", completed.State.ToString());
        AddNullable(command, "end_time", completed.EndTime);
        AddNullable(command, "failure_reason", completed.FailureReason);
        AddNullable(command, "observed_environment", completed.ObservedEnvironment?.ToString());
        AddNullable(command, "observed_endpoint_identity", completed.ObservedEndpointIdentity);
        AddNullable(command, "observed_account_identifier", completed.ObservedAccountIdentifier);
        command.Parameters.AddWithValue("revision", completed.Revision);
        command.Parameters.AddWithValue("session_id", completed.Id.Value);
        command.Parameters.AddWithValue("expected_state", expected.State.ToString());
        command.Parameters.AddWithValue("expected_revision", expected.Revision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new SessionConcurrencyException();
        }
    }

    private static DateTimeOffset? ReadTime(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null
        : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void AddNullable(NpgsqlCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
