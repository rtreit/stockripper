using Stockripper.Application.Allocations;
using Stockripper.Application.Brokers;
using Stockripper.Application.Data;
using Stockripper.Application.Execution;
using Stockripper.Application.Goals;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class PaperReviewSessionServiceTests
{
    [Theory]
    [InlineData(ExecutionMode.PAPER_REVIEW)]
    [InlineData(ExecutionMode.PAPER_AUTO)]
    public async Task Exact_preflight_records_evidence_and_makes_session_ready(
        ExecutionMode mode)
    {
        var context = CreateContext(mode: mode);

        var outcome = await context.Service.StartAsync(
            context.Goal.Id,
            context.Activation.Id,
            context.Snapshot.Id,
            OperatorId.New());

        Assert.Equal(SessionState.READY, outcome.Session.State);
        Assert.Equal(ExecutablePreflightResult.PASSED, outcome.Preflight.Result);
        Assert.NotNull(outcome.Preflight.ReconciliationId);
        Assert.All(outcome.Preflight.Checks, static check => Assert.True(check.Passed));
        Assert.Equal(outcome.Session, context.Sessions.Current);
        Assert.Equal(outcome.Preflight, context.Preflights.Current);
    }

    [Fact]
    public async Task Wrong_account_records_failed_preflight_and_never_becomes_ready()
    {
        var context = CreateContext(static observation => new ExecutablePreflightObservation(
            new BrokerConnectionObservation(
                observation.Broker.Environment,
                observation.Broker.EndpointIdentity,
                "wrong-paper-account",
                observation.Broker.Capabilities,
                observation.Broker.ObservedAt),
            observation.BrokerTime,
            observation.BrokerClockUsable,
            observation.MarketSessionOpen,
            observation.DataSourcesAvailable,
            observation.AvailablePolicies,
            observation.KillSwitchClear,
            observation.ReconciliationCurrent,
            observation.ReconciliationObservedAt,
            observation.CapitalDataCurrent,
            observation.BuyingPowerDataCurrent,
            observation.CapitalObservedAt,
            observation.PositionExposure,
            observation.OpenOrderExposure,
            observation.BuyingPower,
            observation.Currency));

        var outcome = await context.Service.StartAsync(
            context.Goal.Id,
            context.Activation.Id,
            context.Snapshot.Id,
            OperatorId.New());

        Assert.Equal(SessionState.FAILED, outcome.Session.State);
        Assert.Equal(ExecutablePreflightResult.FAILED, outcome.Preflight.Result);
        Assert.False(outcome.Preflight.Checks.Single(
            check => check.Code == ExecutablePreflightCheckCode.ACCOUNT_IDENTITY).Passed);
        Assert.DoesNotContain("wrong-paper-account", outcome.Session.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_endpoint_records_failed_preflight_and_never_becomes_ready()
    {
        var context = CreateContext(static observation => WithBroker(
            observation,
            new BrokerConnectionObservation(
                observation.Broker.Environment,
                "wrong-paper-endpoint",
                observation.Broker.AccountIdentifier,
                observation.Broker.Capabilities,
                observation.Broker.ObservedAt)));

        var outcome = await context.Service.StartAsync(
            context.Goal.Id,
            context.Activation.Id,
            context.Snapshot.Id,
            OperatorId.New());

        Assert.Equal(SessionState.FAILED, outcome.Session.State);
        Assert.False(outcome.Preflight.Checks.Single(
            check => check.Code == ExecutablePreflightCheckCode.ENDPOINT_IDENTITY).Passed);
        Assert.DoesNotContain("wrong-paper-endpoint", outcome.Session.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_policy_and_triggered_kill_switch_fail_closed()
    {
        var context = CreateContext(static observation => new ExecutablePreflightObservation(
            observation.Broker,
            observation.BrokerTime,
            observation.BrokerClockUsable,
            observation.MarketSessionOpen,
            observation.DataSourcesAvailable,
            [observation.AvailablePolicies.First()],
            false,
            observation.ReconciliationCurrent,
            observation.ReconciliationObservedAt,
            observation.CapitalDataCurrent,
            observation.BuyingPowerDataCurrent,
            observation.CapitalObservedAt,
            observation.PositionExposure,
            observation.OpenOrderExposure,
            observation.BuyingPower,
            observation.Currency));

        var outcome = await context.Service.StartAsync(
            context.Goal.Id,
            context.Activation.Id,
            context.Snapshot.Id,
            OperatorId.New());

        Assert.Equal(SessionState.FAILED, outcome.Session.State);
        Assert.False(outcome.Preflight.Checks.Single(
            check => check.Code == ExecutablePreflightCheckCode.POLICY_BINDINGS).Passed);
        Assert.False(outcome.Preflight.Checks.Single(
            check => check.Code == ExecutablePreflightCheckCode.KILL_SWITCH).Passed);
    }

    [Fact]
    public async Task Detailed_reconciliation_difference_prevents_ready_session()
    {
        var context = CreateContext(static observation =>
            new ExecutablePreflightObservation(
                observation.Broker, observation.BrokerTime,
                observation.BrokerClockUsable, observation.MarketSessionOpen,
                observation.DataSourcesAvailable, observation.AvailablePolicies,
                observation.KillSwitchClear, false,
                observation.ReconciliationObservedAt,
                observation.CapitalDataCurrent,
                observation.BuyingPowerDataCurrent,
                observation.CapitalObservedAt,
                observation.PositionExposure,
                observation.OpenOrderExposure,
                observation.BuyingPower,
                observation.Currency));

        var outcome = await context.Service.StartAsync(
            context.Goal.Id, context.Activation.Id,
            context.Snapshot.Id, OperatorId.New());

        Assert.Equal(SessionState.FAILED, outcome.Session.State);
        Assert.False(outcome.Preflight.Checks.Single(check =>
            check.Code == ExecutablePreflightCheckCode.RECONCILIATION).Passed);
    }

    [Theory]
    [InlineData(ExecutionMode.REPLAY)]
    [InlineData(ExecutionMode.RESEARCH)]
    [InlineData(ExecutionMode.LIVE_REVIEW)]
    [InlineData(ExecutionMode.LIVE_AUTO)]
    public async Task Session_boundary_accepts_only_paper_modes(ExecutionMode mode)
    {
        var context = CreateContext(mode: mode);

        await Assert.ThrowsAsync<PaperReviewSessionPreflightException>(() => context.Service.StartAsync(
            context.Goal.Id,
            context.Activation.Id,
            context.Snapshot.Id,
            OperatorId.New()).AsTask());

        Assert.Null(context.Sessions.Current);
    }

    private static TestContext CreateContext(
        Func<ExecutablePreflightObservation, ExecutablePreflightObservation>? change = null,
        ExecutionMode mode = ExecutionMode.PAPER_REVIEW)
    {
        var goal = GoalTestData.CreateGoalVersion(mode);
        var activation = GoalTestData.CreateActivation(goal);
        var snapshot = GoalTestData.CreateDataSnapshot(goal);
        var paperMode = mode.RequiredBrokerEnvironment() == BrokerEnvironment.PAPER;
        var allocation = paperMode
            ? CreateAllocation(goal, activation)
            : null;
        var observation = CreateObservation(
            paperMode
                ? goal
                : GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW));
        observation = change?.Invoke(observation) ?? observation;
        var sessions = new MemorySessionPersistence();
        var preflights = new MemoryPreflightPersistence(sessions);
        var safety = new MemorySafetyPersistence(goal);
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var service = new PaperReviewSessionService(
            new MemoryGoalPersistence(goal),
            new MemoryActivationPersistence(activation),
            new MemorySnapshotPersistence(snapshot),
            new MemoryAllocationPersistence(allocation),
            sessions,
            preflights,
            new FakeExecutablePreflightProbe(observation),
            safety,
            new FakeReconciliationRunner(goal, observation, safety, clock),
            clock);
        return new TestContext(goal, activation, snapshot, service, sessions, preflights);
    }

    private static ExecutablePreflightObservation CreateObservation(GoalVersion goal)
    {
        var binding = goal.ExecutionBinding!;
        return new ExecutablePreflightObservation(
            GoalTestData.CreateObservation(goal),
            GoalTestData.ReferenceTime,
            true,
            true,
            true,
            [
                goal.DataPolicyVersion,
                goal.RiskPolicyVersion,
                binding.OrderPolicyVersion,
                binding.ReconciliationPolicyVersion
            ],
            true,
            true,
            GoalTestData.ReferenceTime,
            true,
            true,
            GoalTestData.ReferenceTime,
            1_000m,
            500m,
            25_000m,
            "USD");
    }

    private static ExecutablePreflightObservation WithBroker(
        ExecutablePreflightObservation observation,
        BrokerConnectionObservation broker) => new(
        broker,
        observation.BrokerTime,
        observation.BrokerClockUsable,
        observation.MarketSessionOpen,
        observation.DataSourcesAvailable,
        observation.AvailablePolicies,
        observation.KillSwitchClear,
        observation.ReconciliationCurrent,
        observation.ReconciliationObservedAt,
        observation.CapitalDataCurrent,
        observation.BuyingPowerDataCurrent,
        observation.CapitalObservedAt,
        observation.PositionExposure,
        observation.OpenOrderExposure,
        observation.BuyingPower,
        observation.Currency);

    private static AccountAllocationEntry CreateAllocation(
        GoalVersion goal,
        GoalActivation activation)
    {
        var binding = goal.ExecutionBinding!;
        var limit = AccountAllocationLimit.Create(
            new VersionReference("paper-account-limit", "1", "digest-paper-account-limit-1"),
            binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            new Money(25_000m, "USD"),
            OperatorId.New(),
            GoalTestData.ReferenceTime);
        return AccountAllocationEntry.Reserve(
            AccountAllocationEntryId.New(),
            activation.Id,
            goal,
            limit,
            BrokerEnvironment.PAPER,
            "LONG_LIMIT_WHOLE_SHARE_MAX_NOTIONAL",
            GoalTestData.ReferenceTime);
    }

    private sealed record TestContext(
        GoalVersion Goal,
        GoalActivation Activation,
        DataSnapshot Snapshot,
        PaperReviewSessionService Service,
        MemorySessionPersistence Sessions,
        MemoryPreflightPersistence Preflights);

    private sealed class FakeExecutablePreflightProbe(ExecutablePreflightObservation observation)
        : IExecutablePreflightProbe
    {
        public ValueTask<ExecutablePreflightObservation> ObserveAsync(
            VersionReference brokerConnectionVersion,
            CancellationToken cancellationToken) => ValueTask.FromResult(observation);
    }

    private sealed class MemorySessionPersistence : ISessionPersistence
    {
        public Session? Current { get; set; }

        public ValueTask InsertAsync(
            Session session,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            Current = session;
            return ValueTask.CompletedTask;
        }

        public ValueTask<Session?> FindAsync(
            SessionId sessionId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            Current?.Id == sessionId ? Current : null);

        public ValueTask UpdateAsync(
            Session session,
            SessionState expectedState,
            int expectedRevision,
            OperatorId actorId,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(Current);
            Assert.Equal(expectedState, Current.State);
            Assert.Equal(expectedRevision, Current.Revision);
            Current = session;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<Session>> FindInterruptedReadOnlyAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryPreflightPersistence(MemorySessionPersistence sessions)
        : IExecutablePreflightPersistence
    {
        public ExecutableSessionPreflight? Current { get; private set; }

        public ValueTask CompleteAsync(
            Session preflightSession,
            Session completedSession,
            ExecutableSessionPreflight evidence,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(preflightSession, sessions.Current);
            sessions.Current = completedSession;
            Current = evidence;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ExecutableSessionPreflight?> FindAsync(
            SessionId sessionId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            Current?.SessionId == sessionId ? Current : null);
    }

    private sealed class MemorySafetyPersistence : IExecutionSafetyPersistence
    {
        private readonly List<KillSwitch> killSwitches;
        private readonly List<ReconciliationRecord> reconciliations = [];

        public MemorySafetyPersistence(GoalVersion goal)
        {
            if (goal.ExecutionBinding is null)
            {
                killSwitches = [];
                return;
            }

            var binding = ExecutionSafetyService.ToAccountBinding(goal);
            killSwitches = PaperExecutionSafetyScopes.Required(goal.Id, binding)
                .Select(scope => KillSwitch.Configure(
                    KillSwitchId.New(),
                    scope.ScopeType,
                    scope.ScopeIdentifier,
                    GoalTestData.ReferenceTime))
                .ToList();
        }

        public ValueTask ConfigureKillSwitchesAsync(
            IReadOnlyList<KillSwitch> values,
            OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<KillSwitch>> FindKillSwitchesAsync(
            IReadOnlyList<KillSwitchScopeReference> scopes,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<KillSwitch>>(
            killSwitches.Where(value => scopes.Any(scope => scope.ScopeType == value.ScopeType
                && scope.ScopeIdentifier == value.ScopeIdentifier)).ToArray());

        public ValueTask<KillSwitch?> FindKillSwitchAsync(
            KillSwitchId killSwitchId,
            CancellationToken cancellationToken) => ValueTask.FromResult<KillSwitch?>(
            killSwitches.SingleOrDefault(value => value.Id == killSwitchId));

        public ValueTask UpdateKillSwitchAsync(
            KillSwitch killSwitch,
            KillSwitchState expectedState,
            int expectedRevision,
            OperatorId actorId,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask InsertReconciliationAsync(
            ReconciliationRecord reconciliation,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            reconciliations.Add(reconciliation);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReconciliationRecord?> FindReconciliationAsync(
            ReconciliationId reconciliationId,
            CancellationToken cancellationToken) => ValueTask.FromResult<ReconciliationRecord?>(
            reconciliations.SingleOrDefault(value => value.Id == reconciliationId));

        public ValueTask<ReconciliationRecord?> FindLatestCurrentReconciliationAsync(
            GoalVersionId goalVersionId,
            IntentAccountBinding accountBinding,
            CancellationToken cancellationToken) => ValueTask.FromResult<ReconciliationRecord?>(
            reconciliations.LastOrDefault(value => value.GoalVersionId == goalVersionId
                && value.AccountBinding == accountBinding
                && value.Result == ReconciliationResult.CURRENT));

        public ValueTask<ExposureReservation?> FindReservationAsync(
            ExecutionIntentId intentId,
            CancellationToken cancellationToken) => ValueTask.FromResult<ExposureReservation?>(null);

        public ValueTask ReleaseReservationAsync(
            ExposureReservation reservation,
            int expectedRevision,
            OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeReconciliationRunner(
        GoalVersion goal,
        ExecutablePreflightObservation observation,
        IExecutionSafetyPersistence safety,
        TimeProvider clock) : IPaperReconciliationRunner
    {
        public async ValueTask<ReconciliationRecord> ReconcileAsync(
            GoalVersionId goalVersionId,
            OperatorId actorId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(goal.Id, goalVersionId);
            var expected = ExecutionSafetyService.ToAccountBinding(goal);
            var observed = new IntentAccountBinding(
                expected.BrokerConnectionVersion,
                observation.Broker.Environment,
                observation.Broker.EndpointIdentity,
                observation.Broker.AccountIdentifier);
            var differences = new List<string>();
            if (observed != expected)
            {
                differences.Add("Observed Broker authority differs from the Goal binding.");
            }
            if (!observation.ReconciliationCurrent)
            {
                differences.Add("Detailed Broker reconciliation is not current.");
            }

            var at = clock.GetUtcNow();
            var record = ReconciliationRecord.Create(
                ReconciliationId.New(), goal.Id, observed,
                goal.ExecutionBinding!.ReconciliationPolicyVersion,
                at, at, observation.ReconciliationObservedAt,
                observation.PositionExposure,
                observation.OpenOrderExposure,
                observation.BuyingPower,
                observation.Currency,
                "orders:sha256:0000000000000000000000000000000000000000000000000000000000000000",
                "fills:sha256:0000000000000000000000000000000000000000000000000000000000000000",
                "positions:sha256:0000000000000000000000000000000000000000000000000000000000000000",
                differences, [],
                differences.Count == 0 ? null : "Review the Broker difference.",
                true);
            await safety.InsertReconciliationAsync(
                record, actorId, cancellationToken);
            return record;
        }
    }

    private sealed class MemoryGoalPersistence(GoalVersion goal) : IGoalPersistence
    {
        public ValueTask<GoalVersion?> FindGoalVersionAsync(
            GoalVersionId goalVersionId,
            CancellationToken cancellationToken) => ValueTask.FromResult<GoalVersion?>(
            goal.Id == goalVersionId ? goal : null);

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

    private sealed class MemoryActivationPersistence(GoalActivation activation)
        : IGoalActivationPersistence
    {
        public ValueTask<GoalActivation?> FindAsync(
            ActivationId activationId,
            CancellationToken cancellationToken) => ValueTask.FromResult<GoalActivation?>(
            activation.Id == activationId ? activation : null);

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

    private sealed class MemorySnapshotPersistence(DataSnapshot snapshot) : IDataSnapshotPersistence
    {
        public ValueTask<DataSnapshot?> FindAsync(
            DataSnapshotId snapshotId,
            CancellationToken cancellationToken) => ValueTask.FromResult<DataSnapshot?>(
            snapshot.Id == snapshotId ? snapshot : null);

        public ValueTask InsertAsync(
            DataSnapshot value,
            OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryAllocationPersistence(AccountAllocationEntry? allocation)
        : IAccountAllocationPersistence
    {
        public ValueTask<AccountAllocationEntry?> FindByActivationAsync(
            ActivationId activationId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            allocation?.ActivationId == activationId ? allocation : null);

        public ValueTask InsertLimitAsync(
            AccountAllocationLimit accountLimit,
            OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AccountAllocationLimit?> FindLimitAsync(
            VersionReference limitVersion,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask ReserveAndActivateAsync(
            GoalId goalId,
            GoalActivation value,
            AccountAllocationEntry entry,
            BrokerConnectionObservation observation,
            OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
