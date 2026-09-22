using Stockripper.Domain.Common;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Replay;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Replay;

public sealed record StrategyRuntimeRequest(
    GoalVersionId GoalVersionId,
    VersionReference StrategyVersion,
    DataSnapshotId DataSnapshotId,
    string DataSnapshotDigest,
    long Seed);

public sealed record StrategyRuntimeOutput(
    string ContractVersion,
    VersionReference StrategyVersion,
    string StrategyArtifactDigest,
    DecisionAction ProposedAction,
    string? InstrumentIdentifier,
    string? Quantity,
    string? LimitPrice,
    string? Explanation,
    string? Confidence,
    IReadOnlyDictionary<string, decimal> OutputMetrics,
    string Evidence);

public static class StrategyRuntimeContract
{
    public const string V1 = "strategy.v1";
}

public interface IStrategyRuntime
{
    ValueTask<StrategyRuntimeOutput> EvaluateAsync(
        StrategyRuntimeRequest request,
        CancellationToken cancellationToken);
}

public interface IReplayPersistence
{
    ValueTask InsertCompletedAsync(
        ReplayResult result,
        Session completedSession,
        int expectedSessionRevision,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<ReplayResult?> FindAsync(
        ReplayResultId replayResultId,
        CancellationToken cancellationToken);
}

public sealed class ReplayGoalNotFoundException : InvalidOperationException
{
    public ReplayGoalNotFoundException()
        : base("The Replay Goal version does not exist.")
    {
    }
}

public sealed class ReplaySnapshotNotFoundException : InvalidOperationException
{
    public ReplaySnapshotNotFoundException()
        : base("The Replay data snapshot does not exist.")
    {
    }
}

public sealed class ReplayPreflightException(string message) : InvalidOperationException(message);

public sealed class StrategyRuntimeUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
