using System.Data;
using System.Text.Json;
using Npgsql;
using Stockripper.Application.Audit;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;

namespace Stockripper.Infrastructure.Audit;

public sealed class PostgresAuditIntegrityVerifier(NpgsqlDataSource dataSource)
    : IAuditIntegrityVerifier
{
    public async ValueTask<AuditIntegrityResult> VerifyAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        var head = await ReadHeadAsync(connection, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return AuditIntegrityResult.Invalid(0, "HEAD_MISSING");
        }

        var expectedSequence = 1L;
        var expectedPreviousHash = AuditChain.GenesisHash;
        await using var command = new NpgsqlCommand(
            """
            SELECT event_id, sequence, event_type, event_time, actor_id,
                   goal_version_id, session_id, related_record_identifiers::text,
                   previous_state, new_state, reason, source_reference,
                   canonical_payload, previous_hash, event_hash
            FROM audit_events
            ORDER BY sequence
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sequence = reader.GetInt64(1);
            if (sequence != expectedSequence)
            {
                return AuditIntegrityResult.Invalid(expectedSequence - 1, "SEQUENCE_GAP");
            }

            var previousHash = reader.GetString(13);
            if (!string.Equals(previousHash, expectedPreviousHash, StringComparison.Ordinal))
            {
                return AuditIntegrityResult.Invalid(expectedSequence - 1, "PREVIOUS_HASH_MISMATCH");
            }

            if (!TryReadEventData(reader, out var data))
            {
                return AuditIntegrityResult.Invalid(expectedSequence - 1, "EVENT_DATA_INVALID");
            }

            var storedPayload = reader.GetString(12);
            var expectedPayload = AuditChain.CreateCanonicalPayload(data!);
            if (!string.Equals(storedPayload, expectedPayload, StringComparison.Ordinal))
            {
                return AuditIntegrityResult.Invalid(expectedSequence - 1, "PAYLOAD_MISMATCH");
            }

            var storedHash = reader.GetString(14);
            var expectedHash = AuditChain.ComputeHash(previousHash, storedPayload);
            if (!string.Equals(storedHash, expectedHash, StringComparison.Ordinal))
            {
                return AuditIntegrityResult.Invalid(expectedSequence - 1, "HASH_MISMATCH");
            }

            expectedPreviousHash = storedHash;
            expectedSequence++;
        }

        var verifiedCount = expectedSequence - 1;
        if (head.Value.Sequence != verifiedCount
            || !string.Equals(head.Value.Hash, expectedPreviousHash, StringComparison.Ordinal))
        {
            return AuditIntegrityResult.Invalid(verifiedCount, "HEAD_MISMATCH");
        }

        return AuditIntegrityResult.Valid(verifiedCount);
    }

    private static async ValueTask<(long Sequence, string Hash)?> ReadHeadAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT last_sequence, last_hash FROM audit_chain_heads WHERE chain_id = @chain_id",
            connection);
        command.Parameters.AddWithValue("chain_id", AuditChain.ChainId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private static bool TryReadEventData(NpgsqlDataReader reader, out AuditEventData? data)
    {
        data = null;
        var typeText = reader.GetString(2);
        if (!Enum.TryParse<AuditEventType>(typeText, ignoreCase: false, out var eventType)
            || !string.Equals(typeText, eventType.ToString(), StringComparison.Ordinal))
        {
            return false;
        }

        var related = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(7));
        if (related is null)
        {
            return false;
        }

        data = new AuditEventData(
            reader.GetGuid(0),
            reader.GetInt64(1),
            eventType,
            reader.GetFieldValue<DateTimeOffset>(3),
            new OperatorId(reader.GetGuid(4)),
            reader.IsDBNull(5) ? null : new GoalVersionId(reader.GetGuid(5)),
            reader.IsDBNull(6) ? null : new SessionId(reader.GetGuid(6)),
            related,
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11));
        return true;
    }
}
