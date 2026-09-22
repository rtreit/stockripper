using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;

namespace Stockripper.Infrastructure.Audit;

internal static class PostgresAuditWriter
{
    public static async ValueTask<AuditEventRecord> AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditEventType eventType,
        DateTimeOffset eventTime,
        OperatorId actorId,
        GoalVersionId? goalVersionId,
        SessionId? sessionId,
        IReadOnlyDictionary<string, string> relatedRecordIdentifiers,
        string? previousState,
        string newState,
        string reason,
        string sourceReference,
        CancellationToken cancellationToken)
    {
        var (lastSequence, previousHash) = await LockHeadAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        var sequence = checked(lastSequence + 1);
        var eventId = Guid.NewGuid();
        var data = new AuditEventData(
            eventId,
            sequence,
            eventType,
            eventTime,
            actorId,
            goalVersionId,
            sessionId,
            relatedRecordIdentifiers,
            previousState,
            newState,
            reason,
            sourceReference);
        var canonicalPayload = AuditChain.CreateCanonicalPayload(data);
        var eventHash = AuditChain.ComputeHash(previousHash, canonicalPayload);
        var sortedRelatedRecordIdentifiers = new SortedDictionary<string, string>(
            (IDictionary<string, string>)relatedRecordIdentifiers,
            StringComparer.Ordinal);
        var relatedJson = JsonSerializer.Serialize(sortedRelatedRecordIdentifiers);

        await InsertEventAsync(
            connection,
            transaction,
            data,
            relatedJson,
            canonicalPayload,
            previousHash,
            eventHash,
            cancellationToken).ConfigureAwait(false);
        await UpdateHeadAsync(
            connection,
            transaction,
            sequence,
            eventHash,
            cancellationToken).ConfigureAwait(false);

        return new AuditEventRecord(
            eventId,
            sequence,
            eventType,
            eventTime,
            actorId,
            goalVersionId,
            sessionId,
            relatedRecordIdentifiers,
            previousState,
            newState,
            reason,
            sourceReference,
            canonicalPayload,
            previousHash,
            eventHash);
    }

    private static async ValueTask<(long Sequence, string Hash)> LockHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT last_sequence, last_hash FROM audit_chain_heads WHERE chain_id = @chain_id FOR UPDATE",
            connection,
            transaction);
        command.Parameters.AddWithValue("chain_id", AuditChain.ChainId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The Audit chain head is missing.");
        }

        return (reader.GetInt64(0), reader.GetString(1));
    }

    private static async ValueTask InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditEventData data,
        string relatedJson,
        string canonicalPayload,
        string previousHash,
        string eventHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO audit_events (
                event_id, sequence, event_type, event_time, actor_id,
                goal_version_id, session_id, related_record_identifiers,
                previous_state, new_state, reason, source_reference,
                canonical_payload, previous_hash, event_hash)
            VALUES (
                @event_id, @sequence, @event_type, @event_time, @actor_id,
                @goal_version_id, @session_id, @related_record_identifiers,
                @previous_state, @new_state, @reason, @source_reference,
                @canonical_payload, @previous_hash, @event_hash)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("event_id", data.EventId);
        command.Parameters.AddWithValue("sequence", data.Sequence);
        command.Parameters.AddWithValue("event_type", data.EventType.ToString());
        command.Parameters.AddWithValue("event_time", data.EventTime);
        command.Parameters.AddWithValue("actor_id", data.ActorId.Value);
        command.Parameters.Add("goal_version_id", NpgsqlDbType.Uuid).Value =
            (object?)data.GoalVersionId?.Value ?? DBNull.Value;
        command.Parameters.Add("session_id", NpgsqlDbType.Uuid).Value =
            (object?)data.SessionId?.Value ?? DBNull.Value;
        command.Parameters.AddWithValue(
            "related_record_identifiers",
            NpgsqlDbType.Jsonb,
            relatedJson);
        command.Parameters.Add("previous_state", NpgsqlDbType.Text).Value =
            (object?)data.PreviousState ?? DBNull.Value;
        command.Parameters.AddWithValue("new_state", data.NewState);
        command.Parameters.AddWithValue("reason", data.Reason);
        command.Parameters.AddWithValue("source_reference", data.SourceReference);
        command.Parameters.AddWithValue("canonical_payload", canonicalPayload);
        command.Parameters.AddWithValue("previous_hash", previousHash);
        command.Parameters.AddWithValue("event_hash", eventHash);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpdateHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long sequence,
        string eventHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE audit_chain_heads
            SET last_sequence = @sequence, last_hash = @event_hash
            WHERE chain_id = @chain_id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("event_hash", eventHash);
        command.Parameters.AddWithValue("chain_id", AuditChain.ChainId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidDataException("The Audit chain head update failed.");
        }
    }
}
