using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Decisions;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Decisions;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresDecisionPersistence(NpgsqlDataSource dataSource) : IDecisionPersistence
{
    public async ValueTask InsertAsync(
        Decision decision,
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
                INSERT INTO decisions (
                    decision_id, session_id, goal_version_id, strategy_version,
                    input_snapshot_id, decision_time, proposed_action,
                    instrument_identifier, quantity, limit_price, explanation,
                    confidence, guard_results, state, content_digest)
                VALUES (
                    @decision_id, @session_id, @goal_version_id, @strategy_version,
                    @input_snapshot_id, @decision_time, @proposed_action,
                    @instrument_identifier, @quantity, @limit_price, @explanation,
                    @confidence, @guard_results, @state, @content_digest)
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("decision_id", decision.Id.Value);
            command.Parameters.AddWithValue("session_id", decision.SessionId.Value);
            command.Parameters.AddWithValue("goal_version_id", decision.GoalVersionId.Value);
            command.Parameters.Add("strategy_version", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeVersion(decision.StrategyVersion);
            command.Parameters.AddWithValue("input_snapshot_id", decision.InputSnapshotId.Value);
            command.Parameters.AddWithValue("decision_time", decision.DecisionTime);
            command.Parameters.AddWithValue("proposed_action", decision.ProposedAction.ToString());
            command.Parameters.Add("instrument_identifier", NpgsqlDbType.Text).Value =
                (object?)decision.InstrumentIdentifier ?? DBNull.Value;
            command.Parameters.Add("quantity", NpgsqlDbType.Numeric).Value =
                (object?)decision.Quantity ?? DBNull.Value;
            command.Parameters.Add("limit_price", NpgsqlDbType.Numeric).Value =
                (object?)decision.LimitPrice ?? DBNull.Value;
            command.Parameters.AddWithValue("explanation", decision.Explanation);
            command.Parameters.Add("confidence", NpgsqlDbType.Numeric).Value =
                (object?)decision.Confidence ?? DBNull.Value;
            command.Parameters.Add("guard_results", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeGuards(decision.GuardResults);
            command.Parameters.AddWithValue("state", decision.State.ToString());
            command.Parameters.AddWithValue("content_digest", decision.ContentDigest);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _ = await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.DECISION_RECORDED,
                decision.DecisionTime,
                actorId,
                decision.GoalVersionId,
                decision.SessionId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["contentDigest"] = decision.ContentDigest,
                    ["decisionId"] = decision.Id.Value.ToString("D"),
                    ["inputSnapshotId"] = decision.InputSnapshotId.Value.ToString("D")
                },
                null,
                decision.State.ToString(),
                "Decision and deterministic guard results recorded.",
                "decision-guard",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<Decision?> FindAsync(
        DecisionId decisionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            SelectSql + " WHERE decision_id = @decision_id",
            connection);
        command.Parameters.AddWithValue("decision_id", decisionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader)
            : null;
    }

    public async ValueTask<IReadOnlyList<Decision>> FindBySessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var values = new List<Decision>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            SelectSql + " WHERE session_id = @session_id ORDER BY decision_time, decision_id",
            connection);
        command.Parameters.AddWithValue("session_id", sessionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(Read(reader));
        }

        return values;
    }

    private static Decision Read(NpgsqlDataReader reader)
    {
        var decision = Decision.Create(
            new DecisionId(reader.GetGuid(0)),
            new SessionId(reader.GetGuid(1)),
            new GoalVersionId(reader.GetGuid(2)),
            PersistenceJson.DeserializeVersion(reader.GetString(3)),
            new DataSnapshotId(reader.GetGuid(4)),
            reader.GetFieldValue<DateTimeOffset>(5),
            Enum.Parse<DecisionAction>(reader.GetString(6), ignoreCase: false),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetDecimal(8),
            reader.IsDBNull(9) ? null : reader.GetDecimal(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetDecimal(11),
            PersistenceJson.DeserializeGuards(reader.GetString(12)));
        if (!string.Equals(decision.State.ToString(), reader.GetString(13), StringComparison.Ordinal)
            || !string.Equals(decision.ContentDigest, reader.GetString(14), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored Decision integrity check failed.");
        }

        return decision;
    }

    private const string SelectSql = """
        SELECT decision_id, session_id, goal_version_id, strategy_version::text,
               input_snapshot_id, decision_time, proposed_action,
               instrument_identifier, quantity, limit_price, explanation,
               confidence, guard_results::text, state, content_digest
        FROM decisions
        """;
}
