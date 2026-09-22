using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Data;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresDataSnapshotPersistence(NpgsqlDataSource dataSource)
    : IDataSnapshotPersistence
{
    public async ValueTask InsertAsync(
        DataSnapshot snapshot,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            const string sql = """
                INSERT INTO data_snapshots (
                    data_snapshot_id, data_set_version, data_policy_version,
                    schema_version, range_start, range_end, captured_at,
                    source_observations, payload, payload_digest, content_digest)
                VALUES (
                    @data_snapshot_id, @data_set_version, @data_policy_version,
                    @schema_version, @range_start, @range_end, @captured_at,
                    @source_observations, @payload, @payload_digest, @content_digest)
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("data_snapshot_id", snapshot.Id.Value);
            command.Parameters.Add("data_set_version", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeVersion(snapshot.DataSetVersion);
            command.Parameters.Add("data_policy_version", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeVersion(snapshot.DataPolicyVersion);
            command.Parameters.AddWithValue("schema_version", snapshot.SchemaVersion);
            command.Parameters.AddWithValue("range_start", snapshot.RangeStart);
            command.Parameters.AddWithValue("range_end", snapshot.RangeEnd);
            command.Parameters.AddWithValue("captured_at", snapshot.CapturedAt);
            command.Parameters.Add("source_observations", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeSources(snapshot.SourceObservations);
            command.Parameters.AddWithValue("payload", snapshot.Payload.ToArray());
            command.Parameters.AddWithValue("payload_digest", snapshot.PayloadDigest);
            command.Parameters.AddWithValue("content_digest", snapshot.ContentDigest);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _ = await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.DATA_SNAPSHOT_CREATED,
                snapshot.CapturedAt,
                actorId,
                null,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["contentDigest"] = snapshot.ContentDigest,
                    ["dataSnapshotId"] = snapshot.Id.Value.ToString("D"),
                    ["dataSetVersion"] = snapshot.DataSetVersion.Version
                },
                null,
                "AVAILABLE",
                "Data snapshot created.",
                "data-snapshot-service",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<DataSnapshot?> FindAsync(
        DataSnapshotId snapshotId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT data_snapshot_id, data_set_version::text, data_policy_version::text,
                   schema_version, range_start, range_end, captured_at,
                   source_observations::text, payload, payload_digest, content_digest
            FROM data_snapshots
            WHERE data_snapshot_id = @data_snapshot_id
            """,
            connection);
        command.Parameters.AddWithValue("data_snapshot_id", snapshotId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var snapshot = DataSnapshot.Create(
            new DataSnapshotId(reader.GetGuid(0)),
            PersistenceJson.DeserializeVersion(reader.GetString(1)),
            PersistenceJson.DeserializeVersion(reader.GetString(2)),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            PersistenceJson.DeserializeSources(reader.GetString(7)),
            reader.GetFieldValue<byte[]>(8));
        if (!string.Equals(snapshot.PayloadDigest, reader.GetString(9), StringComparison.Ordinal)
            || !string.Equals(snapshot.ContentDigest, reader.GetString(10), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored data snapshot integrity check failed.");
        }

        return snapshot;
    }
}
