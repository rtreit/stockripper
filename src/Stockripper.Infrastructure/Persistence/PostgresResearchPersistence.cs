using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Research;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Research;
using Stockripper.Domain.Sessions;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresResearchPersistence(NpgsqlDataSource dataSource) : IResearchPersistence
{
    public async ValueTask InsertCompletedAsync(
        ResearchResult result,
        Session completedSession,
        int expectedSessionRevision,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (completedSession.Id != result.SessionId
                || completedSession.State != SessionState.COMPLETED)
            {
                throw new InvalidDataException("The completed Session does not match the Research result.");
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
                    throw new SessionConcurrencyException();
                }
            }

            await using var command = new NpgsqlCommand(
                """
                INSERT INTO research_results (
                    research_result_id, session_id, activation_id, goal_version_id,
                    data_snapshot_id, data_snapshot_digest, strategy_version,
                    data_policy_version, source_observations, output_metrics,
                    evidence, started_at, completed_at, content_digest)
                VALUES (
                    @research_result_id, @session_id, @activation_id, @goal_version_id,
                    @data_snapshot_id, @data_snapshot_digest, @strategy_version,
                    @data_policy_version, @source_observations, @output_metrics,
                    @evidence, @started_at, @completed_at, @content_digest)
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("research_result_id", result.Id.Value);
            command.Parameters.AddWithValue("session_id", result.SessionId.Value);
            command.Parameters.AddWithValue("activation_id", result.ActivationId.Value);
            command.Parameters.AddWithValue("goal_version_id", result.GoalVersionId.Value);
            command.Parameters.AddWithValue("data_snapshot_id", result.DataSnapshotId.Value);
            command.Parameters.AddWithValue("data_snapshot_digest", result.DataSnapshotDigest);
            command.Parameters.Add("strategy_version", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeVersion(result.StrategyVersion);
            command.Parameters.Add("data_policy_version", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeVersion(result.DataPolicyVersion);
            command.Parameters.Add("source_observations", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeSources(result.SourceObservations);
            command.Parameters.Add("output_metrics", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeMetrics(result.OutputMetrics);
            command.Parameters.AddWithValue("evidence", result.Evidence);
            command.Parameters.AddWithValue("started_at", result.StartedAt);
            command.Parameters.AddWithValue("completed_at", result.CompletedAt);
            command.Parameters.AddWithValue("content_digest", result.ContentDigest);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _ = await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.RESEARCH_COMPLETED,
                result.CompletedAt,
                actorId,
                result.GoalVersionId,
                result.SessionId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["contentDigest"] = result.ContentDigest,
                    ["dataSnapshotId"] = result.DataSnapshotId.Value.ToString("D"),
                    ["researchResultId"] = result.Id.Value.ToString("D")
                },
                SessionState.RUNNING.ToString(),
                SessionState.COMPLETED.ToString(),
                "Research completed.",
                "research-service",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<ResearchResult?> FindAsync(
        ResearchResultId researchResultId,
        CancellationToken cancellationToken)
    {
        StoredResearch? stored;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = new NpgsqlCommand(
            """
            SELECT session_id, activation_id, goal_version_id, data_snapshot_id,
                   data_snapshot_digest, strategy_version::text,
                   data_policy_version::text, source_observations::text,
                   output_metrics::text, evidence, started_at, completed_at,
                   content_digest
            FROM research_results
            WHERE research_result_id = @research_result_id
            """,
            connection))
        {
            command.Parameters.AddWithValue("research_result_id", researchResultId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            stored = new StoredResearch(
                new SessionId(reader.GetGuid(0)),
                new ActivationId(reader.GetGuid(1)),
                new GoalVersionId(reader.GetGuid(2)),
                new DataSnapshotId(reader.GetGuid(3)),
                reader.GetString(4),
                PersistenceJson.DeserializeVersion(reader.GetString(5)),
                PersistenceJson.DeserializeVersion(reader.GetString(6)),
                PersistenceJson.DeserializeSources(reader.GetString(7)),
                PersistenceJson.DeserializeMetrics(reader.GetString(8)),
                reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.GetString(12));
        }

        var goal = await new PostgresGoalPersistence(dataSource)
            .FindGoalVersionAsync(stored.GoalVersionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The stored Research Goal version is missing.");
        var snapshot = await new PostgresDataSnapshotPersistence(dataSource)
            .FindAsync(stored.DataSnapshotId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The stored Research data snapshot is missing.");
        var result = ResearchResult.Create(
            researchResultId,
            stored.SessionId,
            stored.ActivationId,
            goal,
            snapshot,
            stored.OutputMetrics,
            stored.Evidence,
            stored.StartedAt,
            stored.CompletedAt);
        if (stored.StrategyVersion != result.StrategyVersion
            || stored.DataPolicyVersion != result.DataPolicyVersion
            || !stored.SourceObservations.SequenceEqual(result.SourceObservations)
            || !string.Equals(stored.DataSnapshotDigest, result.DataSnapshotDigest, StringComparison.Ordinal)
            || !string.Equals(stored.ContentDigest, result.ContentDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored Research result integrity check failed.");
        }

        return result;
    }

    private sealed record StoredResearch(
        SessionId SessionId,
        ActivationId ActivationId,
        GoalVersionId GoalVersionId,
        DataSnapshotId DataSnapshotId,
        string DataSnapshotDigest,
        Domain.Goals.VersionReference StrategyVersion,
        Domain.Goals.VersionReference DataPolicyVersion,
        IReadOnlyList<Domain.Data.DataSourceObservation> SourceObservations,
        IReadOnlyDictionary<string, decimal> OutputMetrics,
        string Evidence,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        string ContentDigest);
}
