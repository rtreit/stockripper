using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Goals;

namespace Stockripper.Application.Data;

public sealed class DataSnapshotService(
    IDataSnapshotPersistence persistence,
    TimeProvider timeProvider)
{
    public async ValueTask<DataSnapshot> CreateAsync(
        VersionReference dataSetVersion,
        VersionReference dataPolicyVersion,
        string schemaVersion,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        IEnumerable<DataSourceObservation> sourceObservations,
        ReadOnlyMemory<byte> payload,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = DataSnapshot.Create(
            DataSnapshotId.New(),
            dataSetVersion,
            dataPolicyVersion,
            schemaVersion,
            rangeStart,
            rangeEnd,
            timeProvider.GetUtcNow(),
            sourceObservations,
            payload.Span);
        await persistence.InsertAsync(snapshot, actorId, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }
}
