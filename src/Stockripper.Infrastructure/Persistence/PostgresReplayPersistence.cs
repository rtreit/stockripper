using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Replay;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Replay;
using Stockripper.Domain.Sessions;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresReplayPersistence(NpgsqlDataSource dataSource) : IReplayPersistence
{
    public async ValueTask InsertCompletedAsync(
        ReplayResult result,
        Session completedSession,
        int expectedSessionRevision,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (completedSession.Id != result.SessionId
                || completedSession.State != SessionState.COMPLETED)
            {
                throw new InvalidDataException("The completed Session does not match the Replay result.");
            }

            await using (var sessionCommand = new NpgsqlCommand(
                """
                UPDATE sessions
                SET state = 'COMPLETED', end_time = @end_time,
                    failure_reason = NULL, revision = @revision
                WHERE session_id = @session_id
                  AND state = 'RUNNING'
                  AND revision = @expected_revision
                """,
                connection,
                transaction))
            {
                sessionCommand.Parameters.AddWithValue("end_time", completedSession.EndTime!.Value);
                sessionCommand.Parameters.AddWithValue("revision", completedSession.Revision);
                sessionCommand.Parameters.AddWithValue("session_id", completedSession.Id.Value);
                sessionCommand.Parameters.AddWithValue("expected_revision", expectedSessionRevision);
                if (await sessionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new Application.Sessions.SessionConcurrencyException();
                }
            }

            const string sql = """
                INSERT INTO replay_results (
                    replay_result_id, session_id, activation_id, goal_version_id,
                    data_snapshot_id, data_snapshot_digest, range_start, range_end,
                    strategy_version, data_policy_version, risk_policy_version,
                    seed, output_metrics, evidence, started_at, completed_at, output_digest)
                VALUES (
                    @replay_result_id, @session_id, @activation_id, @goal_version_id,
                    @data_snapshot_id, @data_snapshot_digest, @range_start, @range_end,
                    @strategy_version, @data_policy_version, @risk_policy_version,
                    @seed, @output_metrics, @evidence, @started_at, @completed_at, @output_digest)
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("replay_result_id", result.Id.Value);
            command.Parameters.AddWithValue("session_id", result.SessionId.Value);
            command.Parameters.AddWithValue("activation_id", result.ActivationId.Value);
            command.Parameters.AddWithValue("goal_version_id", result.GoalVersionId.Value);
            command.Parameters.AddWithValue("data_snapshot_id", result.DataSnapshotId.Value);
            command.Parameters.AddWithValue("data_snapshot_digest", result.DataSnapshotDigest);
            command.Parameters.AddWithValue("range_start", result.RangeStart);
            command.Parameters.AddWithValue("range_end", result.RangeEnd);
            command.Parameters.Add("strategy_version", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeVersion(result.StrategyVersion);
            command.Parameters.Add("data_policy_version", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeVersion(result.DataPolicyVersion);
            command.Parameters.Add("risk_policy_version", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeVersion(result.RiskPolicyVersion);
            command.Parameters.AddWithValue("seed", result.Seed);
            command.Parameters.Add("output_metrics", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeMetrics(result.OutputMetrics);
            command.Parameters.AddWithValue("evidence", result.Evidence);
            command.Parameters.AddWithValue("started_at", result.StartedAt);
            command.Parameters.AddWithValue("completed_at", result.CompletedAt);
            command.Parameters.AddWithValue("output_digest", result.OutputDigest);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _ = await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.REPLAY_COMPLETED,
                result.CompletedAt,
                actorId,
                result.GoalVersionId,
                result.SessionId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["dataSnapshotId"] = result.DataSnapshotId.Value.ToString("D"),
                    ["outputDigest"] = result.OutputDigest,
                    ["replayResultId"] = result.Id.Value.ToString("D")
                },
                "RUNNING",
                "COMPLETED",
                "Replay completed.",
                "replay-service",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<ReplayResult?> FindAsync(
        ReplayResultId replayResultId,
        CancellationToken cancellationToken)
    {
        ReplayRowValue? row;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = new NpgsqlCommand(
            """
            SELECT session_id, activation_id, goal_version_id, data_snapshot_id,
                   data_snapshot_digest, range_start, range_end,
                   strategy_version::text, data_policy_version::text,
                   risk_policy_version::text, seed, output_metrics::text,
                   evidence, started_at, completed_at, output_digest
            FROM replay_results
            WHERE replay_result_id = @replay_result_id
            """,
            connection))
        {
            command.Parameters.AddWithValue("replay_result_id", replayResultId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            row = new ReplayRowValue(
                new SessionId(reader.GetGuid(0)),
                new ActivationId(reader.GetGuid(1)),
                new GoalVersionId(reader.GetGuid(2)),
                new DataSnapshotId(reader.GetGuid(3)),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                PersistenceJson.DeserializeVersion(reader.GetString(7)),
                PersistenceJson.DeserializeVersion(reader.GetString(8)),
                PersistenceJson.DeserializeVersion(reader.GetString(9)),
                reader.GetInt64(10),
                PersistenceJson.DeserializeMetrics(reader.GetString(11)),
                reader.GetString(12),
                reader.GetFieldValue<DateTimeOffset>(13),
                reader.GetFieldValue<DateTimeOffset>(14),
                reader.GetString(15));
        }

        var goalVersion = await new PostgresGoalPersistence(dataSource)
            .FindGoalVersionAsync(row.GoalVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The stored Replay Goal version is missing.");
        if (row.StrategyVersion != goalVersion.StrategyVersion
            || row.DataPolicyVersion != goalVersion.DataPolicyVersion
            || row.RiskPolicyVersion != goalVersion.RiskPolicyVersion)
        {
            throw new InvalidDataException("The stored Replay configuration versions do not match the Goal version.");
        }

        var result = ReplayResult.Create(
            replayResultId,
            row.SessionId,
            row.ActivationId,
            goalVersion,
            row.DataSnapshotId,
            row.DataSnapshotDigest,
            row.RangeStart,
            row.RangeEnd,
            row.Seed,
            row.OutputMetrics,
            row.Evidence,
            row.StartedAt,
            row.CompletedAt);
        if (!string.Equals(result.OutputDigest, row.OutputDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored Replay output integrity check failed.");
        }

        return result;
    }

    private sealed record ReplayRowValue(
        SessionId SessionId,
        ActivationId ActivationId,
        GoalVersionId GoalVersionId,
        DataSnapshotId DataSnapshotId,
        string DataSnapshotDigest,
        DateTimeOffset RangeStart,
        DateTimeOffset RangeEnd,
        Domain.Goals.VersionReference StrategyVersion,
        Domain.Goals.VersionReference DataPolicyVersion,
        Domain.Goals.VersionReference RiskPolicyVersion,
        long Seed,
        IReadOnlyDictionary<string, decimal> OutputMetrics,
        string Evidence,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        string OutputDigest);
}
