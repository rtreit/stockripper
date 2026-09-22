using Stockripper.Application.Decisions;
using Stockripper.Application.Replay;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class DecisionGuardTests
{
    [Theory]
    [InlineData(ExecutionMode.REPLAY)]
    [InlineData(ExecutionMode.RESEARCH)]
    public void Valid_no_action_output_passes_read_only_guards(ExecutionMode mode)
    {
        var goal = GoalTestData.CreateGoalVersion(mode);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);
        var session = Session.Create(
                SessionId.New(),
                goal,
                GoalTestData.CreateActivation(goal),
                GoalTestData.ReferenceTime,
                snapshot.Id,
                mode == ExecutionMode.REPLAY ? 42 : null)
            .TransitionTo(SessionState.PREFLIGHT, GoalTestData.ReferenceTime)
            .TransitionTo(SessionState.READY, GoalTestData.ReferenceTime)
            .TransitionTo(SessionState.RUNNING, GoalTestData.ReferenceTime);

        var decision = new DecisionGuard().Evaluate(
            goal,
            session,
            snapshot,
            Output(goal),
            GoalTestData.ReferenceTime);

        Assert.Equal(DecisionState.NO_ACTION, decision.State);
        Assert.All(decision.GuardResults, result => Assert.True(result.Passed, result.Code));
    }

    [Theory]
    [InlineData(ExecutionMode.PAPER_REVIEW)]
    [InlineData(ExecutionMode.PAPER_AUTO)]
    [InlineData(ExecutionMode.LIVE_REVIEW)]
    [InlineData(ExecutionMode.LIVE_AUTO)]
    public void Phase_zero_guard_rejects_executable_modes(ExecutionMode mode)
    {
        var goal = GoalTestData.CreateGoalVersion(mode);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);
        var session = Session.Create(
                SessionId.New(),
                goal,
                GoalTestData.CreateActivation(goal),
                GoalTestData.ReferenceTime,
                snapshot.Id)
            .TransitionTo(SessionState.PREFLIGHT, GoalTestData.ReferenceTime)
            .CompleteExecutablePreflight(
                mode.RequiredBrokerEnvironment()!.Value,
                GoalTestData.Endpoint(mode.RequiredBrokerEnvironment()!.Value),
                GoalTestData.Account(mode.RequiredBrokerEnvironment()!.Value),
                GoalTestData.ReferenceTime)
            .TransitionTo(SessionState.RUNNING, GoalTestData.ReferenceTime);

        var decision = new DecisionGuard().Evaluate(
            goal,
            session,
            snapshot,
            Output(goal),
            GoalTestData.ReferenceTime);

        Assert.Equal(DecisionState.GUARD_REJECTED, decision.State);
        Assert.False(decision.GuardResults.Single(result => result.Code == "READ_ONLY_MODE").Passed);
    }

    [Theory]
    [InlineData(ExecutionMode.PAPER_REVIEW)]
    [InlineData(ExecutionMode.PAPER_AUTO)]
    public void Valid_actionable_output_passes_paper_guards(ExecutionMode mode)
    {
        var goal = GoalTestData.CreateGoalVersion(mode);
        var snapshot = PaperSnapshot(goal);
        var session = PaperSession(goal, snapshot);

        var decision = new DecisionGuard().EvaluatePaper(
            goal, session, snapshot, PaperOutput(goal),
            GoalTestData.DefaultPaperRiskPolicy, GoalTestData.ReferenceTime);

        Assert.Equal(DecisionState.PROPOSED, decision.State);
        Assert.All(decision.GuardResults, result => Assert.True(result.Passed, result.Code));
    }

    [Fact]
    public void Stale_paper_snapshot_is_guard_rejected()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);
        var session = PaperSession(goal, snapshot);

        var decision = new DecisionGuard().EvaluatePaper(
            goal, session, snapshot, PaperOutput(goal),
            GoalTestData.DefaultPaperRiskPolicy, GoalTestData.ReferenceTime);

        Assert.Equal(DecisionState.GUARD_REJECTED, decision.State);
        Assert.False(decision.GuardResults.Single(
            result => result.Code == "DATA_FRESHNESS").Passed);
    }

    [Fact]
    public void Fractional_paper_quantity_is_guard_rejected()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_AUTO);
        var snapshot = PaperSnapshot(goal);
        var session = PaperSession(goal, snapshot);
        var output = PaperOutput(goal) with { Quantity = "10.5" };

        var decision = new DecisionGuard().EvaluatePaper(
            goal, session, snapshot, output,
            GoalTestData.DefaultPaperRiskPolicy, GoalTestData.ReferenceTime);

        Assert.Equal(DecisionState.GUARD_REJECTED, decision.State);
        Assert.False(decision.GuardResults.Single(
            result => result.Code == "BOUNDED_ORDER").Passed);
    }

    private static StrategyRuntimeOutput Output(GoalVersion goal) => new(
        StrategyRuntimeContract.V1,
        goal.StrategyVersion,
        goal.StrategyVersion.Digest,
        DecisionAction.NO_ACTION,
        null,
        null,
        null,
        "No action was proposed.",
        null,
        new Dictionary<string, decimal> { ["decisionCount"] = 1m },
        "NO_ACTION");

    private static StrategyRuntimeOutput PaperOutput(GoalVersion goal) => new(
        StrategyRuntimeContract.V1,
        goal.StrategyVersion,
        goal.StrategyVersion.Digest,
        DecisionAction.BUY,
        "AAPL",
        "10",
        "100",
        "Buy inside the bounded paper policy.",
        "0.8",
        new Dictionary<string, decimal> { ["maximumExposure"] = 1_000m },
        "PAPER_PROPOSAL");

    private static DataSnapshot PaperSnapshot(GoalVersion goal) => DataSnapshot.Create(
        DataSnapshotId.New(),
        new VersionReference("paper-data", "1", "digest-paper-data-1"),
        goal.DataPolicyVersion, "paper-quote-v1",
        GoalTestData.ReferenceTime.AddMinutes(-1), GoalTestData.ReferenceTime,
        GoalTestData.ReferenceTime,
        [new DataSourceObservation("paper-data", "1", GoalTestData.ReferenceTime)],
        [1, 2, 3]);

    private static Session PaperSession(GoalVersion goal, DataSnapshot snapshot) =>
        Session.Create(SessionId.New(), goal, GoalTestData.CreateActivation(goal),
                GoalTestData.ReferenceTime, snapshot.Id)
            .TransitionTo(SessionState.PREFLIGHT, GoalTestData.ReferenceTime)
            .CompleteExecutablePreflight(BrokerEnvironment.PAPER,
                goal.ExecutionBinding!.EndpointIdentity,
                goal.ExecutionBinding.ExpectedAccountIdentifier,
                GoalTestData.ReferenceTime)
            .TransitionTo(SessionState.RUNNING, GoalTestData.ReferenceTime);
}
