using Stockripper.Domain.Common;
using Stockripper.Domain.Data;

namespace Stockripper.Application.Data;

public interface IDataSnapshotPersistence
{
    ValueTask InsertAsync(
        DataSnapshot snapshot,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<DataSnapshot?> FindAsync(
        DataSnapshotId snapshotId,
        CancellationToken cancellationToken);
}
