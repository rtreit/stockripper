using Stockripper.Application.Replay;
using Stockripper.Domain.Decisions;

namespace Stockripper.Testing;

public sealed class DeterministicReplayStrategyRuntime : IStrategyRuntime
{
    public int EvaluationCount { get; private set; }

    public ValueTask<StrategyRuntimeOutput> EvaluateAsync(
        StrategyRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EvaluationCount++;
        return ValueTask.FromResult(new StrategyRuntimeOutput(
            StrategyRuntimeContract.V1,
            request.StrategyVersion,
            request.StrategyVersion.Digest,
            DecisionAction.NO_ACTION,
            null,
            null,
            null,
            "The deterministic fixture produced no action.",
            null,
            new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                ["decisionCount"] = 1m,
                ["noActionCount"] = 1m
            },
            $"NO_ACTION:{request.DataSnapshotDigest}:{request.Seed}"));
    }
}
