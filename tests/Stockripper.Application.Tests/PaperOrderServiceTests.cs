using Stockripper.Application.Allocations;
using Stockripper.Application.Brokers;
using Stockripper.Application.Audit;
using Stockripper.Application.Execution;
using Stockripper.Application.Goals;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Risk;
using Stockripper.Domain.Sessions;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class PaperOrderServiceTests
{
    [Fact]
    public async Task Paper_auto_sends_without_review_approval()
    {
        var values = CreatePrepared(ExecutionMode.PAPER_AUTO);
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);

        var sent = await service.SendPreparedAsync(values.Order.Id, values.Actor);

        Assert.Equal(OrderState.ACKNOWLEDGED, sent.State);
        Assert.Null(values.Intents.Review);
        Assert.Equal(1, fake.SendCallCount);
        Assert.Equal(ExecutionIntentState.SUBMITTED, values.Intents.Intent.State);
    }

    [Fact]
    public async Task Timeout_after_acceptance_recovers_one_order_by_client_identifier()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        fake.Enqueue(PaperBrokerSendBehavior.TIMEOUT_AFTER_ACCEPTANCE);
        var service = CreateService(values, fake);

        var unknown = await service.SendPreparedAsync(values.Order.Id, values.Actor);
        var recovered = Assert.Single(await service.RecoverAsync(values.Actor));

        Assert.Equal(OrderState.SUBMISSION_UNKNOWN, unknown.State);
        Assert.Equal(OrderState.ACKNOWLEDGED, recovered.State);
        Assert.Equal(1, fake.SendCallCount);
        Assert.Equal(1, fake.ExternalOrderCount);
        Assert.Equal(ExecutionIntentState.SUBMITTED, values.Intents.Intent.State);
    }

    [Fact]
    public async Task Timeout_before_acceptance_requires_reconciliation_without_retry()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        fake.Enqueue(PaperBrokerSendBehavior.TIMEOUT_BEFORE_ACCEPTANCE);
        var service = CreateService(values, fake);

        _ = await service.SendPreparedAsync(values.Order.Id, values.Actor);
        var recovered = Assert.Single(await service.RecoverAsync(values.Actor));

        Assert.Equal(OrderState.RECONCILIATION_REQUIRED, recovered.State);
        Assert.Equal(1, fake.SendCallCount);
        Assert.Equal(0, fake.ExternalOrderCount);
        Assert.Equal(ExecutionIntentState.FAILED, values.Intents.Intent.State);
    }

    [Fact]
    public async Task Restart_after_preparation_without_broker_order_never_submits_blindly()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);

        var recovered = Assert.Single(await service.RecoverAsync(values.Actor));

        Assert.Equal(OrderState.RECONCILIATION_REQUIRED, recovered.State);
        Assert.Equal(0, fake.SendCallCount);
        Assert.Equal(0, fake.ExternalOrderCount);
        Assert.Equal(ExecutionIntentState.FAILED, values.Intents.Intent.State);
    }

    [Fact]
    public async Task Repeated_send_does_not_call_fake_after_acknowledgment()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);

        var first = await service.SendPreparedAsync(values.Order.Id, values.Actor);
        var second = await service.SendPreparedAsync(values.Order.Id, values.Actor);

        Assert.Equal(OrderState.ACKNOWLEDGED, first.State);
        Assert.Equal(first, second);
        Assert.Equal(1, fake.SendCallCount);
        Assert.Equal(1, fake.ExternalOrderCount);
    }

    [Fact]
    public async Task Kill_switch_change_after_preparation_blocks_broker_send()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);
        values.Authority.TriggerSystem(values.Clock.GetUtcNow());

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await service.SendPreparedAsync(values.Order.Id, values.Actor));

        Assert.Equal(0, fake.SendCallCount);
        Assert.Equal(OrderState.SUBMITTING, values.Orders.Current.State);
    }

    [Fact]
    public async Task Audit_integrity_failure_blocks_broker_send()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake,
            auditIntegrityGuard: new AuditIntegrityGuard(
                new InvalidAuditIntegrityVerifier()));

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await service.SendPreparedAsync(values.Order.Id, values.Actor));

        Assert.Equal(0, fake.SendCallCount);
        Assert.Equal(OrderState.SUBMITTING, values.Orders.Current.State);
    }

    [Fact]
    public async Task Reconciliation_difference_after_preparation_blocks_broker_send()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);
        values.Authority.RecordReconciliationDifference(values.Clock.GetUtcNow());

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await service.SendPreparedAsync(values.Order.Id, values.Actor));

        Assert.Equal(0, fake.SendCallCount);
        Assert.Equal(OrderState.SUBMITTING, values.Orders.Current.State);
    }

    [Fact]
    public async Task Kill_switch_trigger_cannot_overtake_an_inflight_submission()
    {
        var values = CreatePrepared();
        using var interlock = new PaperSubmissionInterlock();
        var fake = new BlockingPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake, submissionInterlock: interlock);
        var safety = new ExecutionSafetyService(
            values.Authority, values.Authority, values.Clock,
            submissionInterlock: interlock);

        var sendTask = service.SendPreparedAsync(
            values.Order.Id, values.Actor).AsTask();
        await fake.Entered;
        var triggerTask = safety.TriggerAsync(
            values.Authority.SystemSwitchId, values.Actor,
            "Operator stopped paper execution.").AsTask();

        Assert.False(triggerTask.IsCompleted);
        fake.Release();
        var sent = await sendTask;
        var triggered = await triggerTask;

        Assert.Equal(OrderState.ACKNOWLEDGED, sent.State);
        Assert.Equal(KillSwitchState.TRIGGERED, triggered.State);
        Assert.Equal(1, fake.SendCallCount);
    }

    [Fact]
    public async Task Reconciliation_difference_before_preparation_prevents_order()
    {
        var values = CreatePrepared(prepared: false);
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);
        values.Authority.RecordReconciliationDifference(values.Clock.GetUtcNow());

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await service.PrepareAsync(values.Intents.Intent.Id, values.Actor));

        Assert.Equal(0, fake.SendCallCount);
        Assert.False(values.Orders.HasOrder);
    }

    [Fact]
    public async Task Broker_binding_change_after_preparation_blocks_broker_send()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);
        values.Authority.ObserveWrongAccount();

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await service.SendPreparedAsync(values.Order.Id, values.Actor));

        Assert.Equal(0, fake.SendCallCount);
        Assert.Equal(OrderState.SUBMITTING, values.Orders.Current.State);
    }

    [Fact]
    public async Task Broker_endpoint_change_after_preparation_blocks_broker_send()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);
        values.Authority.ObserveWrongEndpoint();

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await service.SendPreparedAsync(values.Order.Id, values.Actor));

        Assert.Equal(0, fake.SendCallCount);
        Assert.Equal(OrderState.SUBMITTING, values.Orders.Current.State);
    }

    [Fact]
    public async Task Expired_review_after_preparation_blocks_broker_send()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);
        values.Clock.Advance(TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await service.SendPreparedAsync(values.Order.Id, values.Actor));

        Assert.Equal(0, fake.SendCallCount);
        Assert.Equal(OrderState.SUBMITTING, values.Orders.Current.State);
    }

    [Fact]
    public async Task Closed_schedule_after_preparation_blocks_paper_auto_broker_send()
    {
        var values = CreatePrepared(ExecutionMode.PAPER_AUTO,
            TimeSpan.FromHours(5), TimeSpan.FromHours(5));
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);
        values.Clock.Advance(TimeSpan.FromHours(4));

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await service.SendPreparedAsync(values.Order.Id, values.Actor));

        Assert.Equal(0, fake.SendCallCount);
        Assert.Equal(OrderState.SUBMITTING, values.Orders.Current.State);
    }

    [Fact]
    public async Task Expired_activation_after_preparation_blocks_paper_auto_broker_send()
    {
        var values = CreatePrepared(ExecutionMode.PAPER_AUTO,
            TimeSpan.FromHours(2), TimeSpan.FromMinutes(1));
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);
        values.Clock.Advance(TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await service.SendPreparedAsync(values.Order.Id, values.Actor));

        Assert.Equal(0, fake.SendCallCount);
        Assert.Equal(OrderState.SUBMITTING, values.Orders.Current.State);
    }

    [Fact]
    public async Task Allocation_release_after_preparation_blocks_broker_send()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);
        values.Authority.ReleaseAllocation(values.Clock.GetUtcNow());

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await service.SendPreparedAsync(values.Order.Id, values.Actor));

        Assert.Equal(0, fake.SendCallCount);
        Assert.Equal(OrderState.SUBMITTING, values.Orders.Current.State);
    }

    [Fact]
    public async Task Exposure_change_after_preparation_blocks_broker_send()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);
        values.Authority.SetExposure(9_500m, 500m);

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await service.SendPreparedAsync(values.Order.Id, values.Actor));

        Assert.Equal(0, fake.SendCallCount);
        Assert.Equal(OrderState.SUBMITTING, values.Orders.Current.State);
    }

    [Fact]
    public async Task Missing_policy_after_preparation_blocks_broker_send()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake, new MissingPaperRiskPolicyProvider());

        await Assert.ThrowsAsync<PaperOrderException>(async () =>
            await service.SendPreparedAsync(values.Order.Id, values.Actor));

        Assert.Equal(0, fake.SendCallCount);
        Assert.Equal(OrderState.SUBMITTING, values.Orders.Current.State);
    }

    [Fact]
    public async Task Broker_rejection_is_terminal_and_does_not_resubmit()
    {
        var values = CreatePrepared();
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        fake.Enqueue(PaperBrokerSendBehavior.REJECT);
        var service = CreateService(values, fake);

        var rejected = await service.SendPreparedAsync(values.Order.Id, values.Actor);
        var repeated = await service.SendPreparedAsync(values.Order.Id, values.Actor);

        Assert.Equal(OrderState.REJECTED, rejected.State);
        Assert.Equal(rejected, repeated);
        Assert.Equal(ExecutionIntentState.FAILED, values.Intents.Intent.State);
        Assert.Equal(1, fake.SendCallCount);
        Assert.Equal(1, fake.ExternalOrderCount);
    }

    [Fact]
    public async Task Final_exposure_failure_records_evaluation_without_creating_order()
    {
        var values = CreatePrepared(prepared: false);
        var fake = new DeterministicPaperBrokerOrderAdapter(values.Clock);
        var service = CreateService(values, fake);
        values.Authority.SetExposure(9_500m, 500m);

        var preparation = await service.PrepareAsync(
            values.Intents.Intent.Id, values.Actor);

        Assert.Equal(PreSubmitEvaluationResult.FAILED, preparation.Evaluation.Result);
        Assert.Null(preparation.Order);
        Assert.Equal(preparation.Evaluation, values.Orders.LastEvaluation);
        Assert.Equal(0, fake.SendCallCount);
        Assert.False(values.Orders.HasOrder);
    }

    private static PaperOrderService CreateService(Values values,
        IPaperBrokerOrderAdapter fake,
        IPaperRiskPolicyProvider? policyProvider = null,
        IPaperSubmissionInterlock? submissionInterlock = null,
        AuditIntegrityGuard? auditIntegrityGuard = null) =>
        new(values.Authority, values.Authority, values.Authority, values.Authority,
            values.Intents, values.Authority, values.Authority, values.Orders,
            fake, policyProvider ?? new FixedPaperRiskPolicyProvider(), values.Clock,
            submissionInterlock, auditIntegrityGuard);

    private static Values CreatePrepared(
        ExecutionMode mode = ExecutionMode.PAPER_REVIEW,
        TimeSpan? intentLifetime = null,
        TimeSpan? activationLifetime = null,
        bool prepared = true)
    {
        var now = GoalTestData.ReferenceTime;
        var clock = new ManualTimeProvider(now.AddSeconds(3));
        var actor = OperatorId.New();
        var goal = GoalTestData.CreateGoalVersion(mode);
        var activation = GoalTestData.CreateActivation(
            goal, now.Add(activationLifetime ?? TimeSpan.FromHours(1)));
        var createdSession = Session.Create(SessionId.New(), goal, activation,
            now, DataSnapshotId.New());
        var preflightSession = createdSession.TransitionTo(SessionState.PREFLIGHT, now);
        var session = preflightSession.CompleteExecutablePreflight(
                BrokerEnvironment.PAPER, goal.ExecutionBinding!.EndpointIdentity,
                goal.ExecutionBinding.ExpectedAccountIdentifier, now)
            .TransitionTo(SessionState.RUNNING, now);
        var binding = goal.ExecutionBinding!;
        var intentId = ExecutionIntentId.New();
        var decisionId = DecisionId.New();
        var sessionId = session.Id;
        var riskId = RiskEvaluationId.New();
        var reconciliationId = ReconciliationId.New();
        var allocationId = AccountAllocationEntryId.New();
        var accountBinding = new IntentAccountBinding(binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER, binding.EndpointIdentity, binding.ExpectedAccountIdentifier);
        var created = ExecutionIntent.Create(intentId, decisionId, goal.Id, sessionId,
            mode, accountBinding, "AAPL", OrderSide.BUY,
            IntentOrderType.LIMIT, 10m, 100m, IntentTimeInForce.DAY,
            now.Add(intentLifetime ?? TimeSpan.FromMinutes(5)), riskId, now);
        var risk = RiskEvaluation.Create(riskId, intentId, decisionId, goal.Id, sessionId,
            goal.RiskPolicyVersion, allocationId, reconciliationId, 100m, 100m, 1_000m,
            20_000m, "USD", [new RiskCheck(RiskCheckCode.CAPITAL_ALLOCATION, true, "Within allocation.")], now);
        var riskAccepted = created.ApplyRisk(risk);
        ReviewApproval? approval = null;
        var authorized = riskAccepted;
        if (mode == ExecutionMode.PAPER_REVIEW)
        {
            var reviewPending = riskAccepted.AwaitReview();
            approval = ReviewApproval.Create(ReviewApprovalId.New(), intentId, OperatorId.New(),
                ReviewResult.APPROVED, now.AddMilliseconds(100), reviewPending.ContentDigest,
                now.AddMinutes(2), "Approve the bounded paper order.");
            authorized = reviewPending.ApplyReview(approval);
        }

        var reservation = ExposureReservation.Reserve(ExposureReservationId.New(), authorized,
            allocationId, "USD", now.AddMilliseconds(200));
        var accountLimit = AccountAllocationLimit.Create(
            new VersionReference("account-limit", "1", "digest-account-limit-1"),
            binding.BrokerConnectionVersion, BrokerEnvironment.PAPER,
            binding.EndpointIdentity, binding.ExpectedAccountIdentifier,
            new Money(100_000m, "USD"), actor, now);
        var allocation = AccountAllocationEntry.Reserve(
            allocationId, activation.Id, goal, accountLimit,
            BrokerEnvironment.PAPER, "equity-limit-price-v1", now);
        var checks = Enum.GetValues<PreSubmitCheckCode>()
            .Select(code => new PreSubmitCheck(code, true, "The final check passed."))
            .ToArray();
        var evaluation = approval is null
            ? PreSubmitEvaluation.CreateAutomatic(PreSubmitEvaluationId.New(), authorized,
                reservation, reconciliationId, 100m, 100m, 20_000m, "USD", checks,
                now.AddSeconds(1))
            : PreSubmitEvaluation.Create(PreSubmitEvaluationId.New(), authorized, approval,
                reservation, reconciliationId, 100m, 100m, 20_000m, "USD", checks,
                now.AddSeconds(1));
        var order = OrderRecord.Prepare(OrderId.New(), authorized, evaluation, now.AddSeconds(2));
        var currentIntent = prepared
            ? authorized.BeginSubmission(evaluation)
            : authorized;
        var intents = new MemoryIntentPersistence(
            currentIntent, risk, approval, reconciliationId);
        var authority = new SubmissionAuthorityStore(
            goal, activation, session, allocation, reservation, clock, now,
            reconciliationId);
        return new(clock, actor, order, intents,
            new MemoryOrderPersistence(prepared ? order : null, evaluation, intents), authority);
    }

    private sealed record Values(ManualTimeProvider Clock, OperatorId Actor, OrderRecord Order,
        MemoryIntentPersistence Intents, MemoryOrderPersistence Orders,
        SubmissionAuthorityStore Authority);

    private sealed class InvalidAuditIntegrityVerifier : IAuditIntegrityVerifier
    {
        public ValueTask<AuditIntegrityResult> VerifyAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuditIntegrityResult.Invalid(0, "TEST_FAILURE"));
    }

    private sealed class MemoryIntentPersistence(
        ExecutionIntent intent,
        RiskEvaluation risk,
        ReviewApproval? review,
        ReconciliationId reviewReconciliationId) : IPaperReviewIntentPersistence
    {
        public ExecutionIntent Intent { get; private set; } = intent;
        public ReviewApproval? Review => review;

        public ValueTask<ExecutionIntent?> FindIntentAsync(ExecutionIntentId intentId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ExecutionIntent?>(Intent.Id == intentId ? Intent : null);

        public ValueTask<RiskEvaluation?> FindRiskEvaluationAsync(RiskEvaluationId riskEvaluationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RiskEvaluation?>(risk.Id == riskEvaluationId ? risk : null);

        public ValueTask<ReviewApproval?> FindReviewAsync(ExecutionIntentId intentId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(review?.IntentId == intentId ? review : null);

        public ValueTask<ReconciliationId?> FindReviewReconciliationAsync(
            ExecutionIntentId intentId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ReconciliationId?>(
                review?.IntentId == intentId ? reviewReconciliationId : null);

        public ValueTask CreateAsync(RiskEvaluation riskEvaluation, ExecutionIntent createdIntent,
            ExposureReservation? reservation, ReconciliationId reconciliationId,
            OperatorId actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask RecordReviewAsync(ExecutionIntent reviewedIntent,
            ExecutionIntentState expectedState, ReviewApproval approval, ExposureReservation? reservation,
            ReconciliationId? reconciliationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void SetIntent(ExecutionIntent value) => Intent = value;
    }

    private sealed class MemoryOrderPersistence(
        OrderRecord? order,
        PreSubmitEvaluation evaluation,
        MemoryIntentPersistence intents) : IPaperOrderPersistence
    {
        private OrderRecord? _order = order;

        public OrderRecord Current => _order
            ?? throw new InvalidOperationException("The test Order is unavailable.");
        public bool HasOrder => _order is not null;
        public PreSubmitEvaluation? LastEvaluation { get; private set; }

        public ValueTask<OrderRecord?> FindOrderAsync(OrderId orderId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<OrderRecord?>(_order?.Id == orderId ? _order : null);

        public ValueTask<OrderRecord?> FindByIntentAsync(ExecutionIntentId intentId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<OrderRecord?>(_order?.IntentId == intentId ? _order : null);

        public ValueTask<PreSubmitEvaluation?> FindEvaluationAsync(
            PreSubmitEvaluationId evaluationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PreSubmitEvaluation?>(
                LastEvaluation?.Id == evaluationId
                    ? LastEvaluation
                    : evaluation.Id == evaluationId ? evaluation : null);

        public ValueTask<IReadOnlyList<OrderRecord>> FindRecoverableAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<OrderRecord>>(
                _order?.State is OrderState.SUBMITTING or OrderState.SUBMISSION_UNKNOWN
                    ? [_order]
                    : []);

        public ValueTask RecordTransitionAsync(OrderRecord updated, OrderState expectedOrderState,
            ExecutionIntent intent, ExecutionIntentState expectedIntentState,
            BrokerOrderEvent? brokerEvent, OperatorId actorId, CancellationToken cancellationToken)
        {
            Assert.NotNull(_order);
            Assert.Equal(expectedOrderState, _order.State);
            _order = updated;
            intents.SetIntent(intent);
            return ValueTask.CompletedTask;
        }

        public ValueTask RecordEvaluationAsync(PreSubmitEvaluation evaluation, OperatorId actorId,
            CancellationToken cancellationToken)
        {
            LastEvaluation = evaluation;
            return ValueTask.CompletedTask;
        }

        public ValueTask PrepareAsync(PreSubmitEvaluation evaluation, OrderRecord prepared,
            ExecutionIntent pendingIntent, ExecutionIntentState expectedIntentState,
            OperatorId actorId, CancellationToken cancellationToken)
        {
            Assert.Null(_order);
            Assert.Equal(expectedIntentState, intents.Intent.State);
            LastEvaluation = evaluation;
            _order = prepared;
            intents.SetIntent(pendingIntent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SubmissionAuthorityStore(
        GoalVersion goal,
        GoalActivation activation,
        Session session,
        AccountAllocationEntry allocation,
        ExposureReservation reservation,
        TimeProvider clock,
        DateTimeOffset now,
        ReconciliationId reconciliationId) : IGoalPersistence, IGoalActivationPersistence,
            ISessionPersistence, IAccountAllocationPersistence,
            IExecutionSafetyPersistence, IIntentRiskProbe
    {
        private AccountAllocationEntry currentAllocation = allocation;
        private string observedEndpointIdentity =
            goal.ExecutionBinding!.EndpointIdentity;
        private string observedAccountIdentifier =
            goal.ExecutionBinding!.ExpectedAccountIdentifier;
        private decimal positionExposure = 100m;
        private decimal openOrderExposure = 100m;
        private readonly List<KillSwitch> switches = PaperExecutionSafetyScopes
            .Required(goal.Id, ExecutionSafetyService.ToAccountBinding(goal))
            .Select(scope => KillSwitch.Configure(KillSwitchId.New(), scope.ScopeType,
                scope.ScopeIdentifier, now))
            .ToList();
        private readonly List<ReconciliationRecord> reconciliations =
        [
            ReconciliationRecord.Create(
                reconciliationId, goal.Id, ExecutionSafetyService.ToAccountBinding(goal),
                goal.ExecutionBinding!.ReconciliationPolicyVersion, now, now, now,
                100m, 100m, 20_000m, "USD",
                "orders:sha256:0000000000000000000000000000000000000000000000000000000000000000",
                "fills:sha256:0000000000000000000000000000000000000000000000000000000000000000",
                "positions:sha256:0000000000000000000000000000000000000000000000000000000000000000",
                [], [], null, true)
        ];

        public void TriggerSystem(DateTimeOffset at)
        {
            var index = switches.FindIndex(value =>
                value.ScopeType == KillSwitchScopeType.SYSTEM);
            switches[index] = switches[index].Trigger(at, "Test submission stop.");
        }

        public KillSwitchId SystemSwitchId => switches.Single(value =>
            value.ScopeType == KillSwitchScopeType.SYSTEM).Id;

        public void ObserveWrongAccount() =>
            observedAccountIdentifier = "paper-account-other";

        public void ObserveWrongEndpoint() =>
            observedEndpointIdentity = "simulated-paper-endpoint-other";

        public void ReleaseAllocation(DateTimeOffset at) =>
            currentAllocation = currentAllocation.Release(at);

        public void SetExposure(decimal positions, decimal openOrders)
        {
            positionExposure = positions;
            openOrderExposure = openOrders;
        }

        public void RecordReconciliationDifference(DateTimeOffset at) =>
            reconciliations.Add(ReconciliationRecord.Create(
                ReconciliationId.New(), goal.Id,
                ExecutionSafetyService.ToAccountBinding(goal),
                goal.ExecutionBinding!.ReconciliationPolicyVersion,
                at, at, at, positionExposure, openOrderExposure, 20_000m, "USD",
                "orders:sha256:1111111111111111111111111111111111111111111111111111111111111111",
                "fills:sha256:1111111111111111111111111111111111111111111111111111111111111111",
                "positions:sha256:1111111111111111111111111111111111111111111111111111111111111111",
                ["Broker truth differs."], [],
                "Review the Broker difference.", true));

        public ValueTask<GoalVersion?> FindGoalVersionAsync(
            GoalVersionId goalVersionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<GoalVersion?>(goalVersionId == goal.Id ? goal : null);

        public ValueTask<GoalActivation?> FindAsync(
            ActivationId activationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<GoalActivation?>(
                activationId == activation.Id ? activation : null);

        ValueTask<Session?> ISessionPersistence.FindAsync(
            SessionId sessionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Session?>(sessionId == session.Id ? session : null);

        public ValueTask<AccountAllocationEntry?> FindByActivationAsync(
            ActivationId activationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AccountAllocationEntry?>(
                activationId == currentAllocation.ActivationId
                    ? currentAllocation
                    : null);

        public ValueTask<IntentRiskObservation> ObserveAsync(
            IntentAccountBinding accountBinding, CancellationToken cancellationToken)
        {
            var observedAt = clock.GetUtcNow();
            var broker = new BrokerConnectionObservation(
                BrokerEnvironment.PAPER, observedEndpointIdentity,
                observedAccountIdentifier, ["EQUITY_LIMIT_DAY"], observedAt);
            return ValueTask.FromResult(new IntentRiskObservation(
                broker, true, true, true, observedAt, positionExposure,
                openOrderExposure, 20_000m,
                "USD", true, 0, new Dictionary<string, decimal>()));
        }

        public ValueTask<IReadOnlyList<KillSwitch>> FindKillSwitchesAsync(
            IReadOnlyList<KillSwitchScopeReference> scopes,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<KillSwitch>>(switches.Where(value =>
                scopes.Any(scope => scope.ScopeType == value.ScopeType
                    && scope.ScopeIdentifier == value.ScopeIdentifier)).ToArray());

        public ValueTask<ReconciliationRecord?> FindLatestCurrentReconciliationAsync(
            GoalVersionId goalVersionId, IntentAccountBinding accountBinding,
            CancellationToken cancellationToken)
        {
            var latest = reconciliations.LastOrDefault(value =>
                value.GoalVersionId == goalVersionId
                && value.AccountBinding == accountBinding);
            return ValueTask.FromResult<ReconciliationRecord?>(
                latest?.Result == ReconciliationResult.CURRENT ? latest : null);
        }

        public ValueTask InsertDraftAsync(GoalDraft draft,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GoalDraft?> FindDraftAsync(GoalDraftId draftId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UpdateDraftAsync(GoalDraft revisedDraft, int expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GoalVersion> PublishDraftAsync(GoalDraftId draftId,
            int expectedRevision, OperatorId publisherId, DateTimeOffset publishedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask InsertAsync(GoalId goalId, GoalActivation value,
            OperatorId actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask UpdateAsync(GoalActivation value,
            GoalActivationState expectedState, int expectedRevision,
            OperatorId actorId, DateTimeOffset changedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask InsertAsync(Session value, OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UpdateAsync(Session value, SessionState expectedState,
            int expectedRevision, OperatorId actorId, DateTimeOffset changedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<Session>> FindInterruptedReadOnlyAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask ConfigureKillSwitchesAsync(IReadOnlyList<KillSwitch> values,
            OperatorId actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<KillSwitch?> FindKillSwitchAsync(KillSwitchId killSwitchId,
            CancellationToken cancellationToken) => ValueTask.FromResult<KillSwitch?>(
            switches.SingleOrDefault(value => value.Id == killSwitchId));
        public ValueTask UpdateKillSwitchAsync(KillSwitch killSwitch,
            KillSwitchState expectedState, int expectedRevision, OperatorId actorId,
            DateTimeOffset changedAt, CancellationToken cancellationToken)
        {
            var index = switches.FindIndex(value => value.Id == killSwitch.Id);
            if (index < 0
                || switches[index].State != expectedState
                || switches[index].Revision != expectedRevision)
            {
                throw new InvalidOperationException(
                    "The kill switch changed concurrently.");
            }

            switches[index] = killSwitch;
            return ValueTask.CompletedTask;
        }
        public ValueTask InsertReconciliationAsync(ReconciliationRecord value,
            OperatorId actorId, CancellationToken cancellationToken) =>
            AddReconciliation(value);
        public ValueTask<ReconciliationRecord?> FindReconciliationAsync(
            ReconciliationId reconciliationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ReconciliationRecord?>(
                reconciliations.SingleOrDefault(value => value.Id == reconciliationId));
        public ValueTask<ExposureReservation?> FindReservationAsync(
            ExecutionIntentId intentId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ExposureReservation?>(
                reservation.IntentId == intentId ? reservation : null);
        public ValueTask ReleaseReservationAsync(ExposureReservation reservation,
            int expectedRevision, OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private ValueTask AddReconciliation(ReconciliationRecord value)
        {
            reconciliations.Add(value);
            return ValueTask.CompletedTask;
        }
        public ValueTask InsertLimitAsync(AccountAllocationLimit accountLimit,
            OperatorId actorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<AccountAllocationLimit?> FindLimitAsync(
            VersionReference limitVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask ReserveAndActivateAsync(GoalId goalId,
            GoalActivation activationValue, AccountAllocationEntry allocationValue,
            BrokerConnectionObservation observation, OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MissingPaperRiskPolicyProvider : IPaperRiskPolicyProvider
    {
        public ValueTask<PaperRiskPolicy?> FindAsync(
            VersionReference version, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PaperRiskPolicy?>(null);
    }

    private sealed class BlockingPaperBrokerOrderAdapter(TimeProvider clock)
        : IPaperBrokerOrderAdapter
    {
        private readonly TaskCompletionSource entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => entered.Task;
        public int SendCallCount { get; private set; }

        public void Release() => released.TrySetResult();

        public async ValueTask<PaperBrokerResult> SendAsync(
            OrderRecord preparedOrder,
            CancellationToken cancellationToken)
        {
            SendCallCount++;
            entered.TrySetResult();
            await released.Task.WaitAsync(cancellationToken);
            return PaperBrokerResult.Acknowledged(
                "blocking-paper-order", "blocking-paper-event", clock.GetUtcNow());
        }

        public ValueTask<PaperBrokerResult> FindByClientOrderIdAsync(
            IntentAccountBinding accountBinding,
            string brokerClientOrderId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<PaperBrokerCancellationResult> RequestCancellationAsync(
            CancellationRequest cancellationRequest,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

}
