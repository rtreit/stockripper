using Stockripper.Application.Data;
using Stockripper.Application.Decisions;
using Stockripper.Application.Goals;
using Stockripper.Application.Replay;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Replay;
using Stockripper.Domain.Sessions;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class ReplayServiceTests
{
    public static TheoryData<ExecutionMode> NonReplayModes => new()
    {
        ExecutionMode.RESEARCH,
        ExecutionMode.PAPER_REVIEW,
        ExecutionMode.PAPER_AUTO,
        ExecutionMode.LIVE_REVIEW,
        ExecutionMode.LIVE_AUTO
    };

    [Fact]
    public async Task Same_versioned_inputs_and_seed_produce_the_same_output()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);
        var activation = GoalTestData.CreateActivation(goal);
        var strategy = new DeterministicReplayStrategyRuntime();
        var replayPersistence = new MemoryReplayPersistence();
        var service = CreateService(goal, activation, snapshot, strategy, replayPersistence);

        var first = await service.RunAsync(goal.Id, activation.Id, snapshot.Id, 42, OperatorId.New());
        var second = await service.RunAsync(goal.Id, activation.Id, snapshot.Id, 42, OperatorId.New());

        Assert.Equal(first.OutputMetrics, second.OutputMetrics);
        Assert.Equal(first.Evidence, second.Evidence);
        Assert.Equal(first.OutputDigest, second.OutputDigest);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(2, strategy.EvaluationCount);
        Assert.Equal(2, replayPersistence.Results.Count);
    }

    [Theory]
    [MemberData(nameof(NonReplayModes))]
    public async Task Non_replay_modes_are_rejected_before_strategy_evaluation(ExecutionMode mode)
    {
        var goal = GoalTestData.CreateGoalVersion(mode);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);
        var activation = GoalTestData.CreateActivation(goal);
        var strategy = new DeterministicReplayStrategyRuntime();
        var service = CreateService(goal, activation, snapshot, strategy, new MemoryReplayPersistence());

        await Assert.ThrowsAsync<ReplayPreflightException>(() => service.RunAsync(
            goal.Id,
            activation.Id,
            snapshot.Id,
            42,
            OperatorId.New()).AsTask());

        Assert.Equal(0, strategy.EvaluationCount);
    }

    [Fact]
    public async Task Mismatched_data_policy_is_rejected_before_strategy_evaluation()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var snapshot = GoalTestData.CreateDataSnapshot(
            goal,
            dataPolicyVersion: new VersionReference("data-policy", "2", "digest-data-policy-2"));
        var strategy = new DeterministicReplayStrategyRuntime();
        var activation = GoalTestData.CreateActivation(goal);
        var service = CreateService(goal, activation, snapshot, strategy, new MemoryReplayPersistence());

        await Assert.ThrowsAsync<ReplayPreflightException>(() => service.RunAsync(
            goal.Id,
            activation.Id,
            snapshot.Id,
            42,
            OperatorId.New()).AsTask());

        Assert.Equal(0, strategy.EvaluationCount);
    }

    [Fact]
    public async Task Expired_activation_is_rejected_before_strategy_evaluation()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);
        var strategy = new DeterministicReplayStrategyRuntime();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime.AddHours(2));
        var activation = GoalTestData.CreateActivation(
            goal,
            GoalTestData.ReferenceTime.AddHours(1));
        var service = new ReplayService(
            new MemoryGoalPersistence(goal),
            new MemoryActivationPersistence(activation),
            new MemorySnapshotPersistence(snapshot),
            strategy,
            new MemoryDecisionPersistence(),
            new DecisionGuard(),
            new MemoryReplayPersistence(),
            new MemorySessionPersistence(),
            clock);

        await Assert.ThrowsAsync<DomainRuleViolationException>(() => service.RunAsync(
            goal.Id,
            activation.Id,
            snapshot.Id,
            42,
            OperatorId.New()).AsTask());

        Assert.Equal(0, strategy.EvaluationCount);
    }

    [Fact]
    public async Task Mismatched_strategy_response_version_is_rejected_without_persistence()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);
        var persistence = new MemoryReplayPersistence();
        var activation = GoalTestData.CreateActivation(goal);
        var service = CreateService(
            goal,
            activation,
            snapshot,
            new MismatchedStrategyRuntime(),
            persistence);

        await Assert.ThrowsAsync<ReplayPreflightException>(() => service.RunAsync(
            goal.Id,
            activation.Id,
            snapshot.Id,
            42,
            OperatorId.New()).AsTask());

        Assert.Empty(persistence.Results);
    }

    [Fact]
    public async Task Closed_decision_schedule_is_rejected_before_strategy_evaluation()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);
        var strategy = new DeterministicReplayStrategyRuntime();
        var activation = GoalTestData.CreateActivation(goal);
        var service = new ReplayService(
            new MemoryGoalPersistence(goal),
            new MemoryActivationPersistence(activation),
            new MemorySnapshotPersistence(snapshot),
            strategy,
            new MemoryDecisionPersistence(),
            new DecisionGuard(),
            new MemoryReplayPersistence(),
            new MemorySessionPersistence(),
            new ManualTimeProvider(GoalTestData.ReferenceTime.AddHours(14)));

        await Assert.ThrowsAsync<ReplayPreflightException>(() => service.RunAsync(
            goal.Id,
            activation.Id,
            snapshot.Id,
            42,
            OperatorId.New()).AsTask());

        Assert.Equal(0, strategy.EvaluationCount);
    }

    [Fact]
    public async Task Malformed_strategy_response_is_rejected_without_persistence()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);
        var persistence = new MemoryReplayPersistence();
        var activation = GoalTestData.CreateActivation(goal);
        var service = CreateService(
            goal,
            activation,
            snapshot,
            new MalformedStrategyRuntime(),
            persistence);

        await Assert.ThrowsAsync<ReplayPreflightException>(() => service.RunAsync(
            goal.Id,
            activation.Id,
            snapshot.Id,
            42,
            OperatorId.New()).AsTask());

        Assert.Empty(persistence.Results);
    }

    private static ReplayService CreateService(
        GoalVersion goal,
        GoalActivation activation,
        DataSnapshot snapshot,
        IStrategyRuntime strategy,
        IReplayPersistence replayPersistence) => new(
        new MemoryGoalPersistence(goal),
        new MemoryActivationPersistence(activation),
        new MemorySnapshotPersistence(snapshot),
        strategy,
        new MemoryDecisionPersistence(),
        new DecisionGuard(),
        replayPersistence,
        new MemorySessionPersistence(),
        new ManualTimeProvider(GoalTestData.ReferenceTime));

    private sealed class MemoryActivationPersistence(GoalActivation activation)
        : IGoalActivationPersistence
    {
        public ValueTask<GoalActivation?> FindAsync(
            ActivationId activationId,
            CancellationToken cancellationToken) => ValueTask.FromResult<GoalActivation?>(
            activationId == activation.Id ? activation : null);

        public ValueTask InsertAsync(
            GoalId goalId,
            GoalActivation value,
            OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask UpdateAsync(
            GoalActivation value,
            GoalActivationState expectedState,
            int expectedRevision,
            OperatorId actorId,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryGoalPersistence(GoalVersion goal) : IGoalPersistence
    {
        public ValueTask<GoalVersion?> FindGoalVersionAsync(
            GoalVersionId goalVersionId,
            CancellationToken cancellationToken) => ValueTask.FromResult<GoalVersion?>(
            goalVersionId == goal.Id ? goal : null);

        public ValueTask InsertDraftAsync(GoalDraft draft, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<GoalDraft?> FindDraftAsync(
            GoalDraftId draftId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask UpdateDraftAsync(
            GoalDraft revisedDraft,
            int expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GoalVersion> PublishDraftAsync(
            GoalDraftId draftId,
            int expectedRevision,
            OperatorId publisherId,
            DateTimeOffset publishedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemorySnapshotPersistence(DataSnapshot snapshot) : IDataSnapshotPersistence
    {
        public ValueTask InsertAsync(
            DataSnapshot value,
            OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DataSnapshot?> FindAsync(
            DataSnapshotId snapshotId,
            CancellationToken cancellationToken) => ValueTask.FromResult<DataSnapshot?>(
            snapshotId == snapshot.Id ? snapshot : null);
    }

    private sealed class MemoryReplayPersistence : IReplayPersistence
    {
        public List<ReplayResult> Results { get; } = [];

        public ValueTask InsertCompletedAsync(
            ReplayResult result,
            Session completedSession,
            int expectedSessionRevision,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            Results.Add(result);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReplayResult?> FindAsync(
            ReplayResultId replayResultId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            Results.SingleOrDefault(result => result.Id == replayResultId));
    }

    private sealed class MemoryDecisionPersistence : IDecisionPersistence
    {
        private readonly List<Decision> decisions = [];

        public ValueTask InsertAsync(
            Decision decision,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            decisions.Add(decision);
            return ValueTask.CompletedTask;
        }

        public ValueTask<Decision?> FindAsync(
            DecisionId decisionId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            decisions.SingleOrDefault(decision => decision.Id == decisionId));

        public ValueTask<IReadOnlyList<Decision>> FindBySessionAsync(
            SessionId sessionId,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<Decision>>(
            decisions.Where(decision => decision.SessionId == sessionId).ToArray());
    }

    private sealed class MemorySessionPersistence : ISessionPersistence
    {
        private readonly Dictionary<SessionId, Session> sessions = [];

        public ValueTask InsertAsync(
            Session session,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            sessions.Add(session.Id, session);
            return ValueTask.CompletedTask;
        }

        public ValueTask<Session?> FindAsync(
            SessionId sessionId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            sessions.TryGetValue(sessionId, out var session) ? session : null);

        public ValueTask UpdateAsync(
            Session session,
            SessionState expectedState,
            int expectedRevision,
            OperatorId actorId,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken)
        {
            sessions[session.Id] = session;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<Session>> FindInterruptedReadOnlyAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<Session>>([]);
    }

    private sealed class MismatchedStrategyRuntime : IStrategyRuntime
    {
        public ValueTask<StrategyRuntimeOutput> EvaluateAsync(
            StrategyRuntimeRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(new StrategyRuntimeOutput(
                StrategyRuntimeContract.V1,
                new VersionReference("strategy", "wrong", "digest-strategy-wrong"),
                "digest-strategy-wrong",
                DecisionAction.NO_ACTION,
                null,
                null,
                null,
                "The mismatched Strategy produced no action.",
                null,
                new Dictionary<string, decimal> { ["decisionCount"] = 1m },
                "NO_ACTION"));
    }

    private sealed class MalformedStrategyRuntime : IStrategyRuntime
    {
        public ValueTask<StrategyRuntimeOutput> EvaluateAsync(
            StrategyRuntimeRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(new StrategyRuntimeOutput(
                StrategyRuntimeContract.V1,
                request.StrategyVersion,
                request.StrategyVersion.Digest,
                DecisionAction.NO_ACTION,
                null,
                null,
                null,
                string.Empty,
                null,
                null!,
                string.Empty));
    }
}
