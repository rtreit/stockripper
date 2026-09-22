using Stockripper.Application.Execution;
using Stockripper.Application.Goals;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class PaperExecutableSessionLifecycleServiceTests
{
    [Fact]
    public async Task Exact_current_authority_starts_and_completes_after_final_reconciliation()
    {
        var values = CreateValues();
        var running = await values.Service.StartAsync(values.Session.Current.Id, values.Actor);

        values.Clock.Advance(TimeSpan.FromSeconds(1));
        var final = values.Safety.CreateReconciliation(values.Clock.GetUtcNow(), current: true);
        values.Runner.Next = final;
        var completed = await values.Service.CompleteAsync(running.Id, values.Actor);

        Assert.Equal(SessionState.COMPLETED, completed.Session.State);
        Assert.Equal(final.Id, completed.Reconciliation.Id);
        Assert.Equal(
            [SessionState.RUNNING, SessionState.STOPPING,
                SessionState.RECONCILING, SessionState.COMPLETED],
            values.Session.Transitions);
    }

    [Fact]
    public async Task Paper_auto_starts_and_completes_with_exact_authority()
    {
        var values = CreateValues(ExecutionMode.PAPER_AUTO);

        var running = await values.Service.StartAsync(values.Session.Current.Id, values.Actor);
        values.Clock.Advance(TimeSpan.FromSeconds(1));
        values.Runner.Next = values.Safety.CreateReconciliation(
            values.Clock.GetUtcNow(), current: true);
        var completed = await values.Service.CompleteAsync(running.Id, values.Actor);

        Assert.Equal(ExecutionMode.PAPER_AUTO, completed.Session.Mode);
        Assert.Equal(SessionState.COMPLETED, completed.Session.State);
    }

    [Theory]
    [InlineData(ExecutionMode.PAPER_REVIEW)]
    [InlineData(ExecutionMode.PAPER_AUTO)]
    public async Task Expired_activation_blocks_session_start(ExecutionMode mode)
    {
        var values = CreateValues(mode);
        values.Clock.Advance(TimeSpan.FromHours(2));

        await Assert.ThrowsAsync<PaperExecutableSessionLifecycleException>(async () =>
            await values.Service.StartAsync(values.Session.Current.Id, values.Actor));

        Assert.DoesNotContain(SessionState.RUNNING, values.Session.Transitions);
    }

    [Fact]
    public async Task Final_reconciliation_difference_locks_instead_of_completing()
    {
        var values = CreateValues();
        var running = await values.Service.StartAsync(values.Session.Current.Id, values.Actor);
        values.Clock.Advance(TimeSpan.FromSeconds(1));
        values.Runner.Next = values.Safety.CreateReconciliation(
            values.Clock.GetUtcNow(), current: false);

        var completed = await values.Service.CompleteAsync(running.Id, values.Actor);

        Assert.Equal(SessionState.LOCKED, completed.Session.State);
        Assert.Equal(ReconciliationResult.DIFFERENCE_LOCKED,
            completed.Reconciliation.Result);
        Assert.DoesNotContain(SessionState.COMPLETED, values.Session.Transitions);
    }

    [Fact]
    public async Task Unavailable_final_reconciliation_leaves_session_reconciling()
    {
        var values = CreateValues();
        var running = await values.Service.StartAsync(values.Session.Current.Id, values.Actor);
        values.Runner.Unavailable = true;

        await Assert.ThrowsAsync<PaperBrokerReconciliationUnavailableException>(
            async () => await values.Service.CompleteAsync(running.Id, values.Actor));

        Assert.Equal(SessionState.RECONCILING, values.Session.Current.State);
    }

    private static Values CreateValues(
        ExecutionMode mode = ExecutionMode.PAPER_REVIEW)
    {
        var goal = GoalTestData.CreateGoalVersion(mode);
        var activation = GoalTestData.CreateActivation(goal,
            GoalTestData.ReferenceTime.AddHours(1));
        var actor = new OperatorId(Guid.Parse("30000000-0000-0000-0000-000000000001"));
        var created = Session.Create(SessionId.New(), goal, activation,
            GoalTestData.ReferenceTime, DataSnapshotId.New());
        var preflight = created.TransitionTo(
            SessionState.PREFLIGHT, GoalTestData.ReferenceTime);
        var ready = preflight.CompleteExecutablePreflight(
            BrokerEnvironment.PAPER,
            goal.ExecutionBinding!.EndpointIdentity,
            goal.ExecutionBinding.ExpectedAccountIdentifier,
            GoalTestData.ReferenceTime);
        var sessions = new MemorySessionPersistence(ready);
        var safety = new MemorySafetyPersistence(goal);
        safety.Add(safety.CreateReconciliation(GoalTestData.ReferenceTime, current: true));
        var runner = new StubReconciliationRunner(safety);
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var service = new PaperExecutableSessionLifecycleService(
            sessions,
            new MemoryGoalPersistence(goal),
            new MemoryActivationPersistence(activation),
            safety,
            runner,
            clock);
        return new(service, sessions, safety, runner, clock, actor);
    }

    private sealed record Values(
        PaperExecutableSessionLifecycleService Service,
        MemorySessionPersistence Session,
        MemorySafetyPersistence Safety,
        StubReconciliationRunner Runner,
        ManualTimeProvider Clock,
        OperatorId Actor);

    private sealed class MemorySessionPersistence(Session current) : ISessionPersistence
    {
        public Session Current { get; private set; } = current;
        public List<SessionState> Transitions { get; } = [];

        public ValueTask<Session?> FindAsync(
            SessionId sessionId,
            CancellationToken cancellationToken) => ValueTask.FromResult<Session?>(
            Current.Id == sessionId ? Current : null);

        public ValueTask UpdateAsync(
            Session session,
            SessionState expectedState,
            int expectedRevision,
            OperatorId actorId,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken)
        {
            Assert.Equal(expectedState, Current.State);
            Assert.Equal(expectedRevision, Current.Revision);
            Current = session;
            Transitions.Add(session.State);
            return ValueTask.CompletedTask;
        }

        public ValueTask InsertAsync(
            Session session,
            OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<Session>> FindInterruptedReadOnlyAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemorySafetyPersistence : IExecutionSafetyPersistence
    {
        private readonly GoalVersion goal;
        private readonly List<KillSwitch> switches;
        private readonly List<ReconciliationRecord> reconciliations = [];

        public MemorySafetyPersistence(GoalVersion goal)
        {
            this.goal = goal;
            var binding = ExecutionSafetyService.ToAccountBinding(goal);
            switches = PaperExecutionSafetyScopes.Required(goal.Id, binding)
                .Select(scope => KillSwitch.Configure(
                    KillSwitchId.New(), scope.ScopeType, scope.ScopeIdentifier,
                    GoalTestData.ReferenceTime))
                .ToList();
        }

        public void Add(ReconciliationRecord value) => reconciliations.Add(value);

        public ReconciliationRecord CreateReconciliation(
            DateTimeOffset at,
            bool current)
        {
            var binding = ExecutionSafetyService.ToAccountBinding(goal);
            return ReconciliationRecord.Create(
                ReconciliationId.New(), goal.Id, binding,
                goal.ExecutionBinding!.ReconciliationPolicyVersion,
                at, at, at, 0m, 0m, 25_000m, "USD",
                "orders:sha256:0000000000000000000000000000000000000000000000000000000000000000",
                "fills:sha256:0000000000000000000000000000000000000000000000000000000000000000",
                "positions:sha256:0000000000000000000000000000000000000000000000000000000000000000",
                current ? [] : ["Broker truth differs."], [],
                current ? null : "Review the Broker difference.", true);
        }

        public ValueTask<IReadOnlyList<KillSwitch>> FindKillSwitchesAsync(
            IReadOnlyList<KillSwitchScopeReference> scopes,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<KillSwitch>>(switches.ToArray());

        public ValueTask<ReconciliationRecord?> FindLatestCurrentReconciliationAsync(
            GoalVersionId goalVersionId,
            IntentAccountBinding accountBinding,
            CancellationToken cancellationToken)
        {
            var latest = reconciliations.LastOrDefault(value =>
                value.GoalVersionId == goalVersionId
                && value.AccountBinding == accountBinding);
            return ValueTask.FromResult<ReconciliationRecord?>(
                latest?.Result == ReconciliationResult.CURRENT ? latest : null);
        }

        public ValueTask InsertReconciliationAsync(
            ReconciliationRecord reconciliation,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            Add(reconciliation);
            return ValueTask.CompletedTask;
        }

        public ValueTask ConfigureKillSwitchesAsync(
            IReadOnlyList<KillSwitch> killSwitches,
            OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<KillSwitch?> FindKillSwitchAsync(
            KillSwitchId killSwitchId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask UpdateKillSwitchAsync(
            KillSwitch killSwitch,
            KillSwitchState expectedState,
            int expectedRevision,
            OperatorId actorId,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ReconciliationRecord?> FindReconciliationAsync(
            ReconciliationId reconciliationId,
            CancellationToken cancellationToken) => ValueTask.FromResult<ReconciliationRecord?>(
            reconciliations.SingleOrDefault(value => value.Id == reconciliationId));

        public ValueTask<ExposureReservation?> FindReservationAsync(
            ExecutionIntentId intentId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ReleaseReservationAsync(
            ExposureReservation reservation,
            int expectedRevision,
            OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubReconciliationRunner(MemorySafetyPersistence safety)
        : IPaperReconciliationRunner
    {
        public ReconciliationRecord? Next { get; set; }
        public bool Unavailable { get; set; }

        public async ValueTask<ReconciliationRecord> ReconcileAsync(
            GoalVersionId goalVersionId,
            OperatorId actorId,
            CancellationToken cancellationToken = default)
        {
            if (Unavailable)
            {
                throw new PaperBrokerReconciliationUnavailableException(
                    "The fake snapshot is unavailable.");
            }

            var next = Next ?? throw new InvalidOperationException(
                "The final reconciliation is not configured.");
            await safety.InsertReconciliationAsync(next, actorId, cancellationToken);
            return next;
        }
    }

    private sealed class MemoryGoalPersistence(GoalVersion goal) : IGoalPersistence
    {
        public ValueTask<GoalVersion?> FindGoalVersionAsync(
            GoalVersionId goalVersionId,
            CancellationToken cancellationToken) => ValueTask.FromResult<GoalVersion?>(
            goal.Id == goalVersionId ? goal : null);

        public ValueTask InsertDraftAsync(
            GoalDraft draft,
            CancellationToken cancellationToken) => throw new NotSupportedException();

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
}
