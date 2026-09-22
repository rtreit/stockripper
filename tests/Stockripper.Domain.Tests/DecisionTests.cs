using Stockripper.Domain.Common;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Execution;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class DecisionTests
{
    [Fact]
    public void Decision_digest_ignores_equivalent_decimal_storage_scale()
    {
        var id = DecisionId.New();
        var sessionId = SessionId.New();
        var goalId = GoalVersionId.New();
        var snapshotId = DataSnapshotId.New();
        var strategy = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY).StrategyVersion;
        var guard = new DecisionGuardResult("OUTPUT_SCHEMA", true, "The output schema is valid.");

        var first = Decision.Create(
            id,
            sessionId,
            goalId,
            strategy,
            snapshotId,
            GoalTestData.ReferenceTime,
            DecisionAction.BUY,
            "AAPL",
            10m,
            100m,
            "Buy with bounded exposure.",
            0.8m,
            [guard]);
        var restoredScale = Decision.Create(
            id,
            sessionId,
            goalId,
            strategy,
            snapshotId,
            GoalTestData.ReferenceTime,
            DecisionAction.BUY,
            "AAPL",
            10.000000000000m,
            100.000000000000m,
            "Buy with bounded exposure.",
            0.800000000000m,
            [guard]);

        Assert.Equal(first.ContentDigest, restoredScale.ContentDigest);
    }

    [Fact]
    public void Guard_failure_creates_guard_rejected_decision_with_stable_digest()
    {
        var id = DecisionId.New();
        var sessionId = SessionId.New();
        var snapshotId = DataSnapshotId.New();
        var strategy = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY).StrategyVersion;

        var first = Decision.Create(
            id,
            sessionId,
            GoalVersionId.New(),
            strategy,
            snapshotId,
            GoalTestData.ReferenceTime,
            DecisionAction.UNKNOWN,
            null,
            null,
            null,
            "Strategy output was malformed.",
            null,
            [new DecisionGuardResult("OUTPUT_SCHEMA", false, "The output schema is invalid.")]);
        var second = Decision.Create(
            id,
            sessionId,
            first.GoalVersionId,
            strategy,
            snapshotId,
            GoalTestData.ReferenceTime,
            DecisionAction.UNKNOWN,
            null,
            null,
            null,
            "Strategy output was malformed.",
            null,
            [new DecisionGuardResult("OUTPUT_SCHEMA", false, "The output schema is invalid.")]);

        Assert.Equal(DecisionState.GUARD_REJECTED, first.State);
        Assert.Equal(first.ContentDigest, second.ContentDigest);
        Assert.True(first.HasValidContentDigest());
    }
}
