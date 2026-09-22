using Stockripper.Application.Execution;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class PaperPostFillSafetyServiceTests
{
    [Fact]
    public async Task Complete_position_observation_creates_post_fill_evaluation()
    {
        var values = CreateValues(10_000m, 500m);
        var outcome = await values.Service.EvaluateAsync(new PaperPositionObservation(
            values.FillId, values.Binding, "positions-1", values.Now,
            "payload/positions-1", [new("AAPL", 10m, 99m, 990m, "USD")]), values.Actor);

        Assert.Equal(PostFillEvaluationResult.PASSED, outcome.Evaluation.Result);
        Assert.Equal(1_490m, outcome.Evaluation.TotalExposure);
        Assert.Null(outcome.SafetyResponse);
    }

    [Fact]
    public async Task Allocation_violation_creates_durable_safety_response_contract()
    {
        var values = CreateValues(1_000m, 500m);
        var outcome = await values.Service.EvaluateAsync(new PaperPositionObservation(
            values.FillId, values.Binding, "positions-2", values.Now,
            "payload/positions-2", [new("AAPL", 10m, 99m, 990m, "USD")]), values.Actor);

        Assert.Equal(PostFillEvaluationResult.VIOLATION_LOCKED, outcome.Evaluation.Result);
        Assert.NotNull(outcome.SafetyResponse);
        Assert.Equal(OperatorNotificationState.PENDING, outcome.SafetyResponse.NotificationState);
        Assert.Equal(2, values.Persistence.CancellationCallCount);
    }

    [Fact]
    public async Task Duplicate_instrument_snapshot_is_rejected_before_persistence()
    {
        var values = CreateValues(10_000m, 0m);
        var observation = new PaperPositionObservation(values.FillId, values.Binding,
            "positions-3", values.Now, "payload/positions-3",
            [new("AAPL", 5m, 99m, 495m, "USD"), new("AAPL", 5m, 99m, 495m, "USD")]);

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await values.Service.EvaluateAsync(observation, values.Actor));
    }

    private static Values CreateValues(decimal allocation, decimal openOrders)
    {
        var now = GoalTestData.ReferenceTime;
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var binding = ExecutionSafetyService.ToAccountBinding(goal);
        var fillId = FillId.New();
        var persistence = new MemoryPersistence(goal, binding, fillId, allocation, openOrders, now);
        return new(now, OperatorId.New(), fillId, binding, persistence,
            new PaperPostFillSafetyService(persistence, new ManualTimeProvider(now),
                persistence, persistence));
    }

    private sealed record Values(DateTimeOffset Now, OperatorId Actor, FillId FillId,
        IntentAccountBinding Binding, MemoryPersistence Persistence,
        PaperPostFillSafetyService Service);

    private sealed class MemoryPersistence(GoalVersion goal, IntentAccountBinding binding,
        FillId expectedFillId, decimal allocation, decimal openOrders, DateTimeOffset now)
        : IPaperPostFillSafetyPersistence, IExecutionSafetyPersistence,
            ITriggeredCancellationCoordinator
    {
        public int CancellationCallCount { get; private set; }

        public ValueTask<PostFillSafetyOutcome> EvaluateAsync(FillId fillId,
            PositionSnapshotBatch positionBatch, IReadOnlyList<PositionSnapshot> positions,
            OperatorId actorId, CancellationToken cancellationToken)
        {
            Assert.Equal(expectedFillId, fillId);
            Assert.Equal(binding, positionBatch.AccountBinding);
            var evaluation = PostFillEvaluation.Create(PostFillEvaluationId.New(), fillId,
                OrderId.New(), goal.Id, SessionId.New(), goal.RiskPolicyVersion,
                AccountAllocationEntryId.New(), positionBatch, positions.Sum(item => item.MarketValue),
                openOrders, allocation, positions.Sum(item => item.MarketValue) + openOrders,
                20_000m, 20_000m, 1m, "USD", true, now);
            var response = evaluation.Result == PostFillEvaluationResult.PASSED
                ? null
                : PostFillSafetyResponse.Create(PostFillSafetyResponseId.New(), evaluation,
                    binding, "Observed exposure exceeds the Goal allocation.", now);
            return ValueTask.FromResult(new PostFillSafetyOutcome(positionBatch, positions,
                evaluation, response, false));
        }

        public ValueTask<IReadOnlyList<KillSwitch>> FindKillSwitchesAsync(
            IReadOnlyList<KillSwitchScopeReference> scopes,
            CancellationToken cancellationToken)
        {
            var switches = scopes.Select(scope => KillSwitch.Configure(KillSwitchId.New(),
                    scope.ScopeType, scope.ScopeIdentifier, now))
                .Select(value => value.ScopeType is KillSwitchScopeType.ACCOUNT
                        or KillSwitchScopeType.GOAL
                    ? value.Trigger(now, "Post-Fill safety policy locked execution.")
                    : value)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<KillSwitch>>(switches);
        }

        public ValueTask<IReadOnlyList<CancellationRequest>> CancelForTriggeredScopeAsync(
            KillSwitch killSwitch, OperatorId actorId, string reason,
            CancellationToken cancellationToken)
        {
            Assert.NotEqual(KillSwitchState.CLEAR, killSwitch.State);
            CancellationCallCount++;
            return ValueTask.FromResult<IReadOnlyList<CancellationRequest>>([]);
        }

        public ValueTask ConfigureKillSwitchesAsync(IReadOnlyList<KillSwitch> killSwitches,
            OperatorId actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<KillSwitch?> FindKillSwitchAsync(KillSwitchId killSwitchId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask UpdateKillSwitchAsync(KillSwitch killSwitch,
            KillSwitchState expectedState, int expectedRevision, OperatorId actorId,
            DateTimeOffset changedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask InsertReconciliationAsync(ReconciliationRecord reconciliation,
            OperatorId actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ReconciliationRecord?> FindReconciliationAsync(
            ReconciliationId reconciliationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ReconciliationRecord?> FindLatestCurrentReconciliationAsync(
            GoalVersionId goalVersionId, IntentAccountBinding accountBinding,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ExposureReservation?> FindReservationAsync(
            ExecutionIntentId intentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask ReleaseReservationAsync(ExposureReservation reservation,
            int expectedRevision, OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
