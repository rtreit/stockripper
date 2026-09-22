using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Replay;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class ReplayResultTests
{
    [Fact]
    public void Same_inputs_seed_and_output_produce_the_same_output_digest()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);
        var first = Create(goal, snapshot.Id, snapshot.ContentDigest, 42);
        var second = Create(goal, snapshot.Id, snapshot.ContentDigest, 42);

        Assert.Equal(first.OutputDigest, second.OutputDigest);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Different_seed_produces_a_different_output_digest()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);

        var first = Create(goal, snapshot.Id, snapshot.ContentDigest, 42);
        var second = Create(goal, snapshot.Id, snapshot.ContentDigest, 43);

        Assert.NotEqual(first.OutputDigest, second.OutputDigest);
    }

    [Fact]
    public void Non_replay_goal_is_rejected()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.RESEARCH);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);

        Assert.Throws<DomainRuleViolationException>(() => Create(
            goal,
            snapshot.Id,
            snapshot.ContentDigest,
            42));
    }

    private static ReplayResult Create(
        Domain.Goals.GoalVersion goal,
        DataSnapshotId snapshotId,
        string snapshotDigest,
        long seed) => ReplayResult.Create(
        ReplayResultId.New(),
        SessionId.New(),
        ActivationId.New(),
        goal,
        snapshotId,
        snapshotDigest,
        GoalTestData.ReferenceTime.AddHours(-8),
        GoalTestData.ReferenceTime.AddHours(-1),
        seed,
        new Dictionary<string, decimal> { ["decisionCount"] = 1m },
        "NO_ACTION",
        GoalTestData.ReferenceTime,
        GoalTestData.ReferenceTime);
}
