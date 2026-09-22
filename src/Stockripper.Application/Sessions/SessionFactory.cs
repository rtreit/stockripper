using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Sessions;

public sealed class SessionFactory(TimeProvider timeProvider)
{
    public Session Create(
        GoalVersion goalVersion,
        GoalActivation activation,
        DataSnapshotId? inputDataSnapshotId = null,
        long? replaySeed = null) => Session.Create(
        SessionId.New(),
        goalVersion,
        activation,
        timeProvider.GetUtcNow(),
        inputDataSnapshotId,
        replaySeed);
}
