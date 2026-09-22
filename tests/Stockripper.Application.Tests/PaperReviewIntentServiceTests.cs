using Stockripper.Application.Allocations;
using Stockripper.Application.Brokers;
using Stockripper.Application.Decisions;
using Stockripper.Application.Execution;
using Stockripper.Application.Goals;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;
using Stockripper.Domain.Risk;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class PaperReviewIntentServiceTests
{
    [Fact]
    public async Task Paper_auto_reserves_accepted_intent_without_review()
    {
        var fixture = new Fixture(mode: ExecutionMode.PAPER_AUTO);

        var outcome = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id,
            10m,
            100m,
            fixture.Clock.GetUtcNow().AddMinutes(5),
            fixture.Actor);

        Assert.Equal(ExecutionIntentState.RISK_ACCEPTED, outcome.Intent.State);
        Assert.Null(fixture.Store.Review);
        Assert.NotNull(fixture.Store.Reservation);
        Assert.Equal(outcome.Intent.Id, fixture.Store.Reservation.IntentId);
        Assert.Equal(1_000m, fixture.Store.Reservation.Amount);
    }

    [Fact]
    public async Task Exact_current_risk_evidence_creates_review_pending_intent()
    {
        var fixture = new Fixture();

        var outcome = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id,
            10m,
            100m,
            fixture.Clock.GetUtcNow().AddMinutes(5),
            fixture.Actor);

        Assert.Equal(RiskEvaluationResult.ACCEPTED, outcome.RiskEvaluation.Result);
        Assert.Equal(ExecutionIntentState.REVIEW_PENDING, outcome.Intent.State);
        Assert.Equal(outcome.Intent, fixture.Store.Intent);
        Assert.All(outcome.RiskEvaluation.Checks, check => Assert.True(check.Passed));
    }

    [Fact]
    public async Task Replacement_terms_create_a_new_intent_and_risk_evaluation()
    {
        var fixture = new Fixture();
        var first = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id,
            10m,
            100m,
            fixture.Clock.GetUtcNow().AddMinutes(5),
            fixture.Actor);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var replacement = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id,
            9m,
            101m,
            fixture.Clock.GetUtcNow().AddMinutes(5),
            fixture.Actor);

        Assert.Equal(ExecutionIntentState.REVIEW_PENDING, replacement.Intent.State);
        Assert.NotEqual(first.Intent.Id, replacement.Intent.Id);
        Assert.NotEqual(first.Intent.ContentDigest, replacement.Intent.ContentDigest);
        Assert.NotEqual(first.RiskEvaluation.Id, replacement.RiskEvaluation.Id);
        Assert.Equal(9m, replacement.Intent.Quantity);
        Assert.Equal(101m, replacement.Intent.LimitPrice);
    }

    [Fact]
    public async Task Allocation_failure_records_rejected_risk_and_prevents_review()
    {
        var fixture = new Fixture();

        var outcome = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id,
            100m,
            100m,
            fixture.Clock.GetUtcNow().AddMinutes(5),
            fixture.Actor);

        Assert.Equal(RiskEvaluationResult.REJECTED, outcome.RiskEvaluation.Result);
        Assert.Equal(ExecutionIntentState.RISK_REJECTED, outcome.Intent.State);
        Assert.Contains(
            outcome.RiskEvaluation.Checks,
            check => check.Code == RiskCheckCode.CAPITAL_ALLOCATION && !check.Passed);
        await Assert.ThrowsAsync<PaperReviewIntentException>(async () => await fixture.ReviewService.ReviewAsync(
            outcome.Intent.Id,
            outcome.Intent.ContentDigest,
            ReviewResult.APPROVED,
            fixture.Actor));
    }

    [Fact]
    public async Task Maximum_order_policy_rejects_an_order_inside_goal_allocation()
    {
        var fixture = new Fixture();

        var outcome = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id,
            51m,
            100m,
            fixture.Clock.GetUtcNow().AddMinutes(5),
            fixture.Actor);

        Assert.Equal(RiskEvaluationResult.REJECTED, outcome.RiskEvaluation.Result);
        Assert.Contains(outcome.RiskEvaluation.Checks,
            check => check.Code == RiskCheckCode.PRICE_PROTECTION && !check.Passed);
    }

    [Fact]
    public async Task Maximum_order_boundary_is_accepted_and_reserved_exactly()
    {
        var fixture = new Fixture();

        var outcome = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id, 50m, 100m,
            fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Actor);

        Assert.Equal(RiskEvaluationResult.ACCEPTED, outcome.RiskEvaluation.Result);
        Assert.Equal(5_000m, outcome.Intent.MaximumExposure);
        _ = await fixture.ReviewService.ReviewAsync(
            outcome.Intent.Id, outcome.Intent.ContentDigest,
            ReviewResult.APPROVED, fixture.Actor);
        Assert.Equal(5_000m, fixture.Store.Reservation!.Amount);
        Assert.All(outcome.RiskEvaluation.Checks, check => Assert.True(check.Passed));
    }

    [Fact]
    public async Task Missing_exact_policy_blocks_before_risk_persistence()
    {
        var fixture = new Fixture(policyUnavailable: true);

        await Assert.ThrowsAsync<PaperReviewIntentException>(async () =>
            await fixture.IntentService.CreateAsync(
                fixture.Decision.Id,
                10m,
                100m,
                fixture.Clock.GetUtcNow().AddMinutes(5),
                fixture.Actor));

        Assert.Null(fixture.Store.RiskEvaluation);
        Assert.Null(fixture.Store.Intent);
    }

    [Fact]
    public async Task Kill_switch_blocks_new_intent_without_persisting_one()
    {
        var fixture = new Fixture(killSwitchClear: false);

        await Assert.ThrowsAsync<PaperReviewIntentException>(async () => await fixture.IntentService.CreateAsync(
            fixture.Decision.Id,
            10m,
            100m,
            fixture.Clock.GetUtcNow().AddMinutes(5),
            fixture.Actor));

        Assert.Null(fixture.Store.Intent);
        Assert.Null(fixture.Store.RiskEvaluation);
    }

    [Fact]
    public async Task Detailed_reconciliation_difference_blocks_intent_creation()
    {
        var fixture = new Fixture();
        fixture.Runner.Current = false;

        await Assert.ThrowsAsync<PaperReviewIntentException>(async () =>
            await fixture.IntentService.CreateAsync(
                fixture.Decision.Id, 10m, 100m,
                fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Actor));

        Assert.Null(fixture.Store.Intent);
        Assert.Null(fixture.Store.RiskEvaluation);
    }

    [Fact]
    public async Task Risk_observation_mismatch_with_reconciliation_blocks_intent()
    {
        var fixture = new Fixture();
        fixture.Runner.PositionExposureAdjustment = 1m;

        await Assert.ThrowsAsync<PaperReviewIntentException>(async () =>
            await fixture.IntentService.CreateAsync(
                fixture.Decision.Id, 10m, 100m,
                fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Actor));

        Assert.Null(fixture.Store.Intent);
        Assert.Null(fixture.Store.RiskEvaluation);
    }

    [Fact]
    public async Task Wrong_risk_probe_account_records_rejected_account_binding()
    {
        var fixture = new Fixture();
        var current = fixture.Probe.Observation;
        fixture.Probe.Observation = new IntentRiskObservation(
            new BrokerConnectionObservation(
                BrokerEnvironment.PAPER,
                current.Broker.EndpointIdentity,
                "other-paper-account",
                current.Broker.Capabilities,
                fixture.Clock.GetUtcNow()),
            current.KillSwitchClear,
            current.ReconciliationCurrent,
            current.CapitalDataCurrent,
            fixture.Clock.GetUtcNow(),
            current.PositionExposure,
            current.OpenOrderExposure,
            current.BuyingPower,
            current.Currency);

        var outcome = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id, 10m, 100m,
            fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Actor);

        Assert.Equal(RiskEvaluationResult.REJECTED, outcome.RiskEvaluation.Result);
        Assert.Contains(outcome.RiskEvaluation.Checks,
            check => check.Code == RiskCheckCode.ACCOUNT_BINDING && !check.Passed);
    }

    [Fact]
    public async Task Approval_requires_exact_digest_and_current_safety_evidence()
    {
        var fixture = new Fixture();
        var outcome = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id,
            10m,
            100m,
            fixture.Clock.GetUtcNow().AddMinutes(5),
            fixture.Actor);

        await Assert.ThrowsAsync<PaperReviewIntentException>(async () => await fixture.ReviewService.ReviewAsync(
            outcome.Intent.Id,
            new string('0', 64),
            ReviewResult.APPROVED,
            fixture.Actor));
        var approval = await fixture.ReviewService.ReviewAsync(
            outcome.Intent.Id,
            outcome.Intent.ContentDigest,
            ReviewResult.APPROVED,
            fixture.Actor,
            "Approve the bounded paper order.");

        Assert.Equal(ReviewResult.APPROVED, approval.Result);
        Assert.Equal(outcome.Intent.ContentDigest, approval.IntentDigest);
        Assert.Equal(ExecutionIntentState.APPROVED, fixture.Store.Intent!.State);
        Assert.NotNull(fixture.Store.Reservation);
        Assert.Equal(outcome.Intent.MaximumExposure, fixture.Store.Reservation!.Amount);
    }

    [Fact]
    public async Task Kill_switch_change_after_risk_evaluation_blocks_approval()
    {
        var fixture = new Fixture();
        var outcome = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id,
            10m,
            100m,
            fixture.Clock.GetUtcNow().AddMinutes(5),
            fixture.Actor);
        fixture.Probe.Observation = fixture.CreateObservation(killSwitchClear: false);

        await Assert.ThrowsAsync<PaperReviewIntentException>(async () => await fixture.ReviewService.ReviewAsync(
            outcome.Intent.Id,
            outcome.Intent.ContentDigest,
            ReviewResult.APPROVED,
            fixture.Actor));

        Assert.Equal(ExecutionIntentState.REVIEW_PENDING, fixture.Store.Intent!.State);
        Assert.Null(fixture.Store.Review);
    }

    [Fact]
    public async Task Durable_kill_switch_change_blocks_approval_when_probe_remains_clear()
    {
        var fixture = new Fixture();
        var outcome = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id,
            10m,
            100m,
            fixture.Clock.GetUtcNow().AddMinutes(5),
            fixture.Actor);
        fixture.Store.TriggerSystem(fixture.Clock.GetUtcNow());

        await Assert.ThrowsAsync<PaperReviewIntentException>(async () => await fixture.ReviewService.ReviewAsync(
            outcome.Intent.Id,
            outcome.Intent.ContentDigest,
            ReviewResult.APPROVED,
            fixture.Actor));

        Assert.Null(fixture.Store.Review);
        Assert.Null(fixture.Store.Reservation);
    }

    [Fact]
    public async Task Detailed_reconciliation_difference_blocks_approval()
    {
        var fixture = new Fixture();
        var outcome = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id, 10m, 100m,
            fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Actor);
        fixture.Runner.Current = false;

        await Assert.ThrowsAsync<PaperReviewIntentException>(async () =>
            await fixture.ReviewService.ReviewAsync(
                outcome.Intent.Id, outcome.Intent.ContentDigest,
                ReviewResult.APPROVED, fixture.Actor));

        Assert.Null(fixture.Store.Review);
        Assert.Null(fixture.Store.Reservation);
    }

    [Fact]
    public async Task Durable_kill_switch_blocks_actionable_decision_boundary()
    {
        var fixture = new Fixture();
        fixture.Store.TriggerSystem(fixture.Clock.GetUtcNow());
        var service = new ExecutionSafetyService(
            fixture.Store, fixture.Store, fixture.Clock);

        await Assert.ThrowsAsync<ExecutionSafetyException>(async () =>
            await service.RequirePaperExecutionClearAsync(fixture.Goal.Id));

        Assert.Null(fixture.Store.Intent);
        Assert.Null(fixture.Store.RiskEvaluation);
    }

    [Fact]
    public async Task Rejected_review_releases_authority_and_cannot_be_approved_later()
    {
        var fixture = new Fixture();
        var outcome = await fixture.IntentService.CreateAsync(
            fixture.Decision.Id, 10m, 100m,
            fixture.Clock.GetUtcNow().AddMinutes(5), fixture.Actor);

        var rejection = await fixture.ReviewService.ReviewAsync(
            outcome.Intent.Id, outcome.Intent.ContentDigest,
            ReviewResult.REJECTED, fixture.Actor,
            "Reject the bounded paper order.");

        Assert.Equal(ReviewResult.REJECTED, rejection.Result);
        Assert.Equal(ExecutionIntentState.REJECTED, fixture.Store.Intent!.State);
        Assert.Null(fixture.Store.Reservation);
        await Assert.ThrowsAsync<PaperReviewIntentException>(async () =>
            await fixture.ReviewService.ReviewAsync(
                outcome.Intent.Id, outcome.Intent.ContentDigest,
                ReviewResult.APPROVED, fixture.Actor));
    }

    private sealed class Fixture
    {
        public Fixture(bool killSwitchClear = true,
            ExecutionMode mode = ExecutionMode.PAPER_REVIEW,
            bool policyUnavailable = false)
        {
            Clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
            Goal = GoalTestData.CreateGoalVersion(mode);
            Activation = GoalTestData.CreateActivation(Goal, Clock.GetUtcNow().AddHours(1));
            Actor = OperatorId.New();
            var binding = Goal.ExecutionBinding!;
            var limit = AccountAllocationLimit.Create(
                new VersionReference("account-limit", "1", "digest-account-limit-1"),
                binding.BrokerConnectionVersion,
                BrokerEnvironment.PAPER,
                binding.EndpointIdentity,
                binding.ExpectedAccountIdentifier,
                new Money(100_000m, "USD"),
                Actor,
                Clock.GetUtcNow());
            Allocation = AccountAllocationEntry.Reserve(
                AccountAllocationEntryId.New(),
                Activation.Id,
                Goal,
                limit,
                BrokerEnvironment.PAPER,
                "equity-limit-price-v1",
                Clock.GetUtcNow());
            var created = Session.Create(
                SessionId.New(),
                Goal,
                Activation,
                Clock.GetUtcNow(),
                DataSnapshotId.New());
            var preflightSession = created.TransitionTo(SessionState.PREFLIGHT, Clock.GetUtcNow());
            Preflight = ExecutableSessionPreflight.Create(
                SessionPreflightId.New(),
                preflightSession,
                binding.BrokerConnectionVersion,
                ReconciliationId.New(),
                BrokerEnvironment.PAPER,
                binding.EndpointIdentity,
                binding.ExpectedAccountIdentifier,
                binding.RequiredCapabilities,
                Clock.GetUtcNow(),
                Clock.GetUtcNow(),
                Clock.GetUtcNow(),
                Clock.GetUtcNow(),
                "USD",
                1_000m,
                500m,
                25_000m,
                Enum.GetValues<ExecutablePreflightCheckCode>().Select(
                    code => new ExecutablePreflightCheck(code, true, "The preflight check passed.")),
                Clock.GetUtcNow());
            Session = preflightSession.CompleteExecutablePreflight(
                    BrokerEnvironment.PAPER,
                    binding.EndpointIdentity,
                    binding.ExpectedAccountIdentifier,
                    Clock.GetUtcNow())
                .TransitionTo(SessionState.RUNNING, Clock.GetUtcNow());
            Decision = Decision.Create(
                DecisionId.New(),
                Session.Id,
                Goal.Id,
                Goal.StrategyVersion,
                Session.InputDataSnapshotId!.Value,
                Clock.GetUtcNow(),
                DecisionAction.BUY,
                "AAPL",
                10m,
                100m,
                "Buy within bounded paper authority.",
                0.8m,
                [new DecisionGuardResult("EXECUTABLE_OUTPUT", true, "The executable output is valid.")]);
            Store = new Store(
                Goal,
                Activation,
                Allocation,
                Session,
                Preflight,
                Decision,
                killSwitchClear,
                Clock.GetUtcNow());
            Probe = new Probe(CreateObservation(killSwitchClear));
            Runner = new ReconciliationRunner(Store, Goal, Clock, Probe);
            IPaperRiskPolicyProvider policyProvider = policyUnavailable
                ? new MissingPaperRiskPolicyProvider()
                : new FixedPaperRiskPolicyProvider();
            IntentService = new PaperReviewIntentService(
                Store,
                Store,
                Store,
                Store,
                Store,
                Store,
                Probe,
                Store,
                Runner,
                Store,
                policyProvider,
                Clock);
            ReviewService = new PaperReviewService(
                Store,
                Store,
                Store,
                Store,
                Store,
                Store,
                Probe,
                Store,
                Runner,
                policyProvider,
                Clock,
                TimeSpan.FromMinutes(2));
        }

        public ManualTimeProvider Clock { get; }
        public GoalVersion Goal { get; }
        public GoalActivation Activation { get; }
        public AccountAllocationEntry Allocation { get; }
        public Session Session { get; }
        public ExecutableSessionPreflight Preflight { get; }
        public Decision Decision { get; }
        public OperatorId Actor { get; }
        public Store Store { get; }
        public Probe Probe { get; }
        public ReconciliationRunner Runner { get; }
        public PaperReviewIntentService IntentService { get; }
        public PaperReviewService ReviewService { get; }

        public IntentRiskObservation CreateObservation(bool killSwitchClear) => new(
            new BrokerConnectionObservation(
                BrokerEnvironment.PAPER,
                Goal.ExecutionBinding!.EndpointIdentity,
                Goal.ExecutionBinding.ExpectedAccountIdentifier,
                Goal.ExecutionBinding.RequiredCapabilities,
                Clock.GetUtcNow()),
            killSwitchClear,
            reconciliationCurrent: true,
            capitalDataCurrent: true,
            Clock.GetUtcNow(),
            1_000m,
            500m,
            25_000m,
            "USD");
    }

    private sealed class MissingPaperRiskPolicyProvider : IPaperRiskPolicyProvider
    {
        public ValueTask<PaperRiskPolicy?> FindAsync(
            VersionReference reference,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<PaperRiskPolicy?>(null);
    }

    private sealed class Probe(IntentRiskObservation observation) : IIntentRiskProbe
    {
        public IntentRiskObservation Observation { get; set; } = observation;

        public ValueTask<IntentRiskObservation> ObserveAsync(
            IntentAccountBinding accountBinding,
            CancellationToken cancellationToken) => ValueTask.FromResult(Observation);
    }

    private sealed class ReconciliationRunner(
        IExecutionSafetyPersistence persistence,
        GoalVersion goal,
        TimeProvider clock,
        Probe probe) : IPaperReconciliationRunner
    {
        public bool Current { get; set; } = true;
        public decimal PositionExposureAdjustment { get; set; }

        public async ValueTask<ReconciliationRecord> ReconcileAsync(
            GoalVersionId goalVersionId,
            OperatorId actorId,
            CancellationToken cancellationToken = default)
        {
            var at = clock.GetUtcNow();
            var record = ReconciliationRecord.Create(
                ReconciliationId.New(), goal.Id,
                ExecutionSafetyService.ToAccountBinding(goal),
                goal.ExecutionBinding!.ReconciliationPolicyVersion,
                at, at, at,
                probe.Observation.PositionExposure + PositionExposureAdjustment,
                probe.Observation.OpenOrderExposure,
                probe.Observation.BuyingPower, probe.Observation.Currency,
                "orders:sha256:0000000000000000000000000000000000000000000000000000000000000000",
                "fills:sha256:0000000000000000000000000000000000000000000000000000000000000000",
                "positions:sha256:0000000000000000000000000000000000000000000000000000000000000000",
                Current ? [] : ["Broker truth differs."], [],
                Current ? null : "Review the Broker difference.", true);
            await persistence.InsertReconciliationAsync(
                record, actorId, cancellationToken);
            return record;
        }
    }

    private sealed class Store(
        GoalVersion goal,
        GoalActivation activation,
        AccountAllocationEntry allocation,
        Session session,
        ExecutableSessionPreflight preflight,
        Decision decision,
        bool killSwitchClear,
        DateTimeOffset at) :
        IGoalPersistence,
        IGoalActivationPersistence,
        IAccountAllocationPersistence,
        ISessionPersistence,
        IExecutablePreflightPersistence,
        IDecisionPersistence,
        IPaperReviewIntentPersistence,
        IExecutionSafetyPersistence
    {
        private readonly List<KillSwitch> killSwitches = CreateKillSwitches(
            goal,
            killSwitchClear,
            at);
        private readonly List<ReconciliationRecord> reconciliations = [];

        public RiskEvaluation? RiskEvaluation { get; private set; }
        public ExecutionIntent? Intent { get; private set; }
        public ReviewApproval? Review { get; private set; }
        public ReconciliationId? ReviewReconciliationId { get; private set; }
        public ExposureReservation? Reservation { get; private set; }

        public void TriggerSystem(DateTimeOffset at) => killSwitches[0] = killSwitches[0]
            .Trigger(at, "Test durable stop.");

        public ValueTask<GoalVersion?> FindGoalVersionAsync(
            GoalVersionId goalVersionId,
            CancellationToken cancellationToken) => ValueTask.FromResult<GoalVersion?>(
            goalVersionId == goal.Id ? goal : null);

        public ValueTask<GoalActivation?> FindAsync(
            ActivationId activationId,
            CancellationToken cancellationToken) => ValueTask.FromResult<GoalActivation?>(
            activationId == activation.Id ? activation : null);

        public ValueTask<AccountAllocationEntry?> FindByActivationAsync(
            ActivationId activationId,
            CancellationToken cancellationToken) => ValueTask.FromResult<AccountAllocationEntry?>(
            activationId == allocation.ActivationId ? allocation : null);

        ValueTask<Session?> ISessionPersistence.FindAsync(
            SessionId sessionId,
            CancellationToken cancellationToken) => ValueTask.FromResult<Session?>(
            sessionId == session.Id ? session : null);

        ValueTask<ExecutableSessionPreflight?> IExecutablePreflightPersistence.FindAsync(
            SessionId sessionId,
            CancellationToken cancellationToken) => ValueTask.FromResult<ExecutableSessionPreflight?>(
            sessionId == preflight.SessionId ? preflight : null);

        public ValueTask<Decision?> FindAsync(
            DecisionId decisionId,
            CancellationToken cancellationToken) => ValueTask.FromResult<Decision?>(
            decisionId == decision.Id ? decision : null);

        public ValueTask CreateAsync(
            RiskEvaluation riskEvaluation,
            ExecutionIntent intent,
            ExposureReservation? reservation,
            ReconciliationId reconciliationId,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            RiskEvaluation = riskEvaluation;
            Intent = intent;
            Reservation = reservation;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ExecutionIntent?> FindIntentAsync(
            ExecutionIntentId intentId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            Intent?.Id == intentId ? Intent : null);

        public ValueTask<RiskEvaluation?> FindRiskEvaluationAsync(
            RiskEvaluationId riskEvaluationId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            RiskEvaluation?.Id == riskEvaluationId ? RiskEvaluation : null);

        public ValueTask RecordReviewAsync(
            ExecutionIntent reviewedIntent,
            ExecutionIntentState expectedState,
            ReviewApproval review,
            ExposureReservation? reservation,
            ReconciliationId? reconciliationId,
            CancellationToken cancellationToken)
        {
            if (Intent?.State != expectedState)
            {
                throw new PaperReviewIntentConcurrencyException();
            }

            Intent = reviewedIntent;
            Review = review;
            Reservation = reservation;
            ReviewReconciliationId = reconciliationId;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReviewApproval?> FindReviewAsync(
            ExecutionIntentId intentId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            Review?.IntentId == intentId ? Review : null);

        public ValueTask<ReconciliationId?> FindReviewReconciliationAsync(
            ExecutionIntentId intentId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            Review?.IntentId == intentId ? ReviewReconciliationId : null);

        public ValueTask ConfigureKillSwitchesAsync(
            IReadOnlyList<KillSwitch> values,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            foreach (var value in values)
            {
                if (!killSwitches.Any(item => item.ScopeType == value.ScopeType
                    && item.ScopeIdentifier == value.ScopeIdentifier))
                {
                    killSwitches.Add(value);
                }
            }

            return ValueTask.CompletedTask;
        }

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
            CancellationToken cancellationToken)
        {
            var index = killSwitches.FindIndex(value => value.Id == killSwitch.Id);
            if (index < 0
                || killSwitches[index].State != expectedState
                || killSwitches[index].Revision != expectedRevision)
            {
                throw new InvalidOperationException("The kill switch changed concurrently.");
            }

            killSwitches[index] = killSwitch;
            return ValueTask.CompletedTask;
        }

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
            CancellationToken cancellationToken) => ValueTask.FromResult(
            Reservation?.IntentId == intentId ? Reservation : null);

        public ValueTask ReleaseReservationAsync(
            ExposureReservation reservation,
            int expectedRevision,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            Reservation = reservation;
            return ValueTask.CompletedTask;
        }

        private static List<KillSwitch> CreateKillSwitches(
            GoalVersion goal,
            bool clear,
            DateTimeOffset at)
        {
            var binding = ExecutionSafetyService.ToAccountBinding(goal);
            var values = PaperExecutionSafetyScopes.Required(goal.Id, binding)
                .Select(scope => KillSwitch.Configure(
                    KillSwitchId.New(),
                    scope.ScopeType,
                    scope.ScopeIdentifier,
                    at))
                .ToList();
            if (!clear)
            {
                values[0] = values[0].Trigger(at, "Test kill switch.");
            }

            return values;
        }

        public ValueTask InsertDraftAsync(GoalDraft draft, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<GoalDraft?> FindDraftAsync(GoalDraftId draftId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask UpdateDraftAsync(GoalDraft revisedDraft, int expectedRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<GoalVersion> PublishDraftAsync(GoalDraftId draftId, int expectedRevision, OperatorId publisherId, DateTimeOffset publishedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask InsertAsync(GoalId goalId, GoalActivation value, OperatorId actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask UpdateAsync(GoalActivation value, GoalActivationState expectedState, int expectedRevision, OperatorId actorId, DateTimeOffset changedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask InsertLimitAsync(AccountAllocationLimit accountLimit, OperatorId actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<AccountAllocationLimit?> FindLimitAsync(VersionReference limitVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask ReserveAndActivateAsync(GoalId goalId, GoalActivation value, AccountAllocationEntry entry, BrokerConnectionObservation observation, OperatorId actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask InsertAsync(Session value, OperatorId actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask UpdateAsync(Session value, SessionState expectedState, int expectedRevision, OperatorId actorId, DateTimeOffset changedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<IReadOnlyList<Session>> FindInterruptedReadOnlyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask CompleteAsync(Session preflightSession, Session completedSession, ExecutableSessionPreflight evidence, OperatorId actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask InsertAsync(Decision value, OperatorId actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<IReadOnlyList<Decision>> FindBySessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
