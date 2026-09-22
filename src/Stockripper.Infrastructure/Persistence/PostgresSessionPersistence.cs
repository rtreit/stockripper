using System.Data;
using Npgsql;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Sessions;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresSessionPersistence(NpgsqlDataSource dataSource)
    : ISessionPersistence, IExecutableSessionRecoveryPersistence
{
    public async ValueTask InsertAsync(
        Session session,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO sessions (
                    session_id, goal_version_id, activation_id, mode, start_time,
                    state, end_time, failure_reason, input_data_snapshot_id,
                    replay_seed, observed_environment, observed_endpoint_identity,
                    observed_account_identifier, revision)
                VALUES (
                    @session_id, @goal_version_id, @activation_id, @mode, @start_time,
                    @state, NULL, NULL, @input_data_snapshot_id, @replay_seed,
                    NULL, NULL, NULL, @revision)
                """,
                connection,
                transaction);
            AddIdentityParameters(command, session);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _ = await AppendAuditAsync(
                connection,
                transaction,
                AuditEventType.SESSION_CREATED,
                session,
                actorId,
                session.StartTime,
                null,
                "Session created.",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<Session?> FindAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            SelectSql + " WHERE session_id = @session_id",
            connection);
        command.Parameters.AddWithValue("session_id", sessionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async ValueTask UpdateAsync(
        Session session,
        SessionState expectedState,
        int expectedRevision,
        OperatorId actorId,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
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
            command.Parameters.AddWithValue("state", session.State.ToString());
            command.Parameters.AddWithValue("end_time", session.EndTime is null ? DBNull.Value : session.EndTime.Value);
            command.Parameters.AddWithValue(
                "failure_reason",
                session.FailureReason is null ? DBNull.Value : session.FailureReason);
            command.Parameters.AddWithValue("revision", session.Revision);
            command.Parameters.AddWithValue(
                "observed_environment",
                session.ObservedEnvironment is null
                    ? DBNull.Value
                    : session.ObservedEnvironment.Value.ToString());
            command.Parameters.AddWithValue(
                "observed_endpoint_identity",
                session.ObservedEndpointIdentity is null
                    ? DBNull.Value
                    : session.ObservedEndpointIdentity);
            command.Parameters.AddWithValue(
                "observed_account_identifier",
                session.ObservedAccountIdentifier is null
                    ? DBNull.Value
                    : session.ObservedAccountIdentifier);
            command.Parameters.AddWithValue("session_id", session.Id.Value);
            command.Parameters.AddWithValue("expected_state", expectedState.ToString());
            command.Parameters.AddWithValue("expected_revision", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new SessionConcurrencyException();
            }

            _ = await AppendAuditAsync(
                connection,
                transaction,
                AuditEventType.SESSION_STATE_CHANGED,
                session,
                actorId,
                changedAt,
                expectedState.ToString(),
                session.FailureReason ?? "Session state changed.",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<Session>> FindInterruptedReadOnlyAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<Session>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            SelectSql + " WHERE mode IN ('REPLAY', 'RESEARCH') AND state NOT IN ('COMPLETED', 'FAILED') ORDER BY start_time, session_id",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public async ValueTask<IReadOnlyList<Session>> FindInterruptedExecutableAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<Session>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            SelectSql + " WHERE mode IN ('PAPER_REVIEW', 'PAPER_AUTO', 'LIVE_REVIEW', 'LIVE_AUTO') AND state NOT IN ('COMPLETED', 'FAILED', 'LOCKED') ORDER BY start_time, session_id",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    internal static async ValueTask<AuditEventRecord> AppendAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditEventType eventType,
        Session session,
        OperatorId actorId,
        DateTimeOffset changedAt,
        string? previousState,
        string reason,
        CancellationToken cancellationToken) => await PostgresAuditWriter.AppendAsync(
        connection,
        transaction,
        eventType,
        changedAt,
        actorId,
        session.GoalVersionId,
        session.Id,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["activationId"] = session.ActivationId.Value.ToString("D")
        },
        previousState,
        session.State.ToString(),
        reason,
        "session-service",
        cancellationToken).ConfigureAwait(false);

    private static void AddIdentityParameters(NpgsqlCommand command, Session session)
    {
        command.Parameters.AddWithValue("session_id", session.Id.Value);
        command.Parameters.AddWithValue("goal_version_id", session.GoalVersionId.Value);
        command.Parameters.AddWithValue("activation_id", session.ActivationId.Value);
        command.Parameters.AddWithValue("mode", session.Mode.ToWireValue());
        command.Parameters.AddWithValue("start_time", session.StartTime);
        command.Parameters.AddWithValue("state", session.State.ToString());
        command.Parameters.AddWithValue(
            "input_data_snapshot_id",
            session.InputDataSnapshotId is null ? DBNull.Value : session.InputDataSnapshotId.Value.Value);
        command.Parameters.AddWithValue("replay_seed", session.ReplaySeed is null ? DBNull.Value : session.ReplaySeed.Value);
        command.Parameters.AddWithValue("revision", session.Revision);
    }

    private static Session Read(NpgsqlDataReader reader) => Session.Restore(
        new SessionId(reader.GetGuid(0)),
        new GoalVersionId(reader.GetGuid(1)),
        new ActivationId(reader.GetGuid(2)),
        ExecutionModeRules.Parse(reader.GetString(3)),
        reader.GetFieldValue<DateTimeOffset>(4),
        Enum.Parse<SessionState>(reader.GetString(5), ignoreCase: false),
        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : new DataSnapshotId(reader.GetGuid(8)),
        reader.IsDBNull(9) ? null : reader.GetInt64(9),
        reader.GetInt32(10),
        reader.IsDBNull(11)
            ? null
            : Enum.Parse<BrokerEnvironment>(reader.GetString(11), ignoreCase: false),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13));

    private const string SelectSql = """
        SELECT session_id, goal_version_id, activation_id, mode, start_time,
               state, end_time, failure_reason, input_data_snapshot_id,
               replay_seed, revision, observed_environment,
               observed_endpoint_identity, observed_account_identifier
        FROM sessions
        """;
}
