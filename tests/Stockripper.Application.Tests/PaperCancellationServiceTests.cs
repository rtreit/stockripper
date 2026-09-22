using Stockripper.Application.Execution;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class PaperCancellationServiceTests
{
    [Fact]
    public async Task Accepted_request_keeps_order_pending_until_broker_truth()
    {
        var values = await CreateValuesAsync();

        var request = await values.Service.CancelAsync(values.Order.Id, values.Actor,
            "The Operator requested cancellation.");

        Assert.Equal(CancellationRequestState.REQUESTED, request.State);
        Assert.Equal(OrderState.CANCEL_PENDING, values.Orders.Order.State);
        Assert.Null(values.Orders.Order.TerminalAt);
        Assert.Equal(1, values.Fake.CancellationCallCount);
        Assert.Equal(1, values.Fake.ExternalCancellationCount);
    }

    [Fact]
    public async Task Unknown_outcome_requires_reconciliation_without_duplicate_request()
    {
        var values = await CreateValuesAsync();
        values.Fake.EnqueueCancellation(PaperBrokerCancellationBehavior.TIMEOUT_AFTER_ACCEPTANCE);

        var first = await values.Service.CancelAsync(values.Order.Id, values.Actor,
            "The Operator requested cancellation.");
        var duplicate = await values.Service.CancelAsync(values.Order.Id, values.Actor,
            "The Operator repeated cancellation.");

        Assert.Equal(CancellationRequestState.RECONCILIATION_REQUIRED, first.State);
        Assert.Equal(first, duplicate);
        Assert.Equal(1, values.Fake.CancellationCallCount);
        Assert.Equal(1, values.Fake.ExternalCancellationCount);
    }

    [Fact]
    public async Task Restart_recovery_dispatches_one_durably_prepared_request()
    {
        var values = await CreateValuesAsync();
        var request = CancellationRequest.Prepare(CancellationRequestId.New(),
            values.Order, "The kill switch requested cancellation.", values.Clock.GetUtcNow());
        values.Cancellations.SetPrepared(request,
            values.Order.RequestCancellation(values.Clock.GetUtcNow()));

        var recovered = Assert.Single(await values.Service.RecoverAsync(values.Actor));

        Assert.Equal(CancellationRequestState.REQUESTED, recovered.State);
        Assert.Equal(1, values.Fake.CancellationCallCount);
        Assert.Equal(1, values.Fake.ExternalCancellationCount);
    }

    private static async ValueTask<Values> CreateValuesAsync()
    {
        var now = GoalTestData.ReferenceTime;
        var clock = new ManualTimeProvider(now.AddSeconds(4));
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var binding = goal.ExecutionBinding!;
        var intentId = ExecutionIntentId.New();
        var riskId = RiskEvaluationId.New();
        var accountBinding = new IntentAccountBinding(binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER, binding.EndpointIdentity, binding.ExpectedAccountIdentifier);
        var created = ExecutionIntent.Create(intentId, DecisionId.New(), goal.Id, SessionId.New(),
            ExecutionMode.PAPER_REVIEW, accountBinding, "AAPL", OrderSide.BUY,
            IntentOrderType.LIMIT, 10m, 100m, IntentTimeInForce.DAY,
            now.AddMinutes(5), riskId, now);
        var risk = RiskEvaluation.Create(riskId, intentId, created.DecisionId, goal.Id,
            created.SessionId, goal.RiskPolicyVersion, AccountAllocationEntryId.New(),
            ReconciliationId.New(), 100m, 100m, 1_000m, 20_000m, "USD",
            [new RiskCheck(RiskCheckCode.CAPITAL_ALLOCATION, true, "Within allocation.")], now);
        var reviewPending = created.ApplyRisk(risk).AwaitReview();
        var approval = ReviewApproval.Create(ReviewApprovalId.New(), intentId, OperatorId.New(),
            ReviewResult.APPROVED, now.AddMilliseconds(100), reviewPending.ContentDigest,
            now.AddMinutes(4), "Approve the bounded paper Order.");
        var approved = reviewPending.ApplyReview(approval);
        var reservation = ExposureReservation.Reserve(ExposureReservationId.New(), approved,
            risk.AllocationEntryId, "USD", now.AddMilliseconds(200));
        var checks = Enum.GetValues<PreSubmitCheckCode>()
            .Select(code => new PreSubmitCheck(code, true, "The final check passed."))
            .ToArray();
        var evaluation = PreSubmitEvaluation.Create(PreSubmitEvaluationId.New(), approved,
            approval, reservation, ReconciliationId.New(), 100m, 100m, 20_000m,
            "USD", checks, now.AddSeconds(1));
        var prepared = OrderRecord.Prepare(OrderId.New(), approved, evaluation, now.AddSeconds(2));
        var fake = new DeterministicPaperBrokerOrderAdapter(clock);
        var result = await fake.SendAsync(prepared, default);
        var acknowledged = prepared.Acknowledge(result.BrokerOrderId!, result.EventReference!,
            result.ObservedAt);
        var orders = new MemoryOrders(acknowledged);
        var cancellations = new MemoryCancellations(orders);
        var service = new PaperCancellationService(orders, cancellations, fake, null!, clock);
        return new(clock, OperatorId.New(), acknowledged, orders, cancellations, fake, service);
    }

    private sealed record Values(ManualTimeProvider Clock, OperatorId Actor,
        OrderRecord Order, MemoryOrders Orders, MemoryCancellations Cancellations,
        DeterministicPaperBrokerOrderAdapter Fake, PaperCancellationService Service);

    private sealed class MemoryOrders(OrderRecord order) : IPaperOrderPersistence
    {
        public OrderRecord Order { get; private set; } = order;

        public void Set(OrderRecord value) => Order = value;

        public ValueTask<OrderRecord?> FindOrderAsync(OrderId orderId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<OrderRecord?>(Order.Id == orderId ? Order : null);

        public ValueTask<OrderRecord?> FindByIntentAsync(ExecutionIntentId intentId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<OrderRecord?>(Order.IntentId == intentId ? Order : null);

        public ValueTask<IReadOnlyList<OrderRecord>> FindRecoverableAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<OrderRecord>>([]);

        public ValueTask RecordEvaluationAsync(PreSubmitEvaluation evaluation,
            OperatorId actorId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask PrepareAsync(PreSubmitEvaluation evaluation, OrderRecord prepared,
            ExecutionIntent pendingIntent, ExecutionIntentState expectedIntentState,
            OperatorId actorId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask RecordTransitionAsync(OrderRecord updated, OrderState expectedOrderState,
            ExecutionIntent intent, ExecutionIntentState expectedIntentState,
            BrokerOrderEvent? brokerEvent, OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryCancellations(MemoryOrders orders) : IPaperCancellationPersistence
    {
        private CancellationRequest? _request;

        public void SetPrepared(CancellationRequest request, OrderRecord pending)
        {
            _request = request;
            orders.Set(pending);
        }

        public ValueTask<CancellationPreparation> PrepareAsync(CancellationRequest request,
            OrderRecord cancelPendingOrder, OrderState expectedOrderState, OperatorId actorId,
            CancellationToken cancellationToken)
        {
            if (_request is not null)
            {
                return ValueTask.FromResult(new CancellationPreparation(_request, orders.Order, true));
            }

            Assert.Equal(expectedOrderState, orders.Order.State);
            _request = request;
            orders.Set(cancelPendingOrder);
            return ValueTask.FromResult(new CancellationPreparation(request, cancelPendingOrder, false));
        }

        public ValueTask RecordStateAsync(CancellationRequest request,
            CancellationRequestState expectedState, OperatorId actorId,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(_request);
            Assert.Equal(expectedState, _request.State);
            _request = request;
            return ValueTask.CompletedTask;
        }

        public ValueTask<CancellationRequest?> FindByOrderAsync(OrderId orderId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_request?.OrderId == orderId ? _request : null);

        public ValueTask<IReadOnlyList<CancellationRequest>> FindRecoverableAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<CancellationRequest>>(
                _request?.State == CancellationRequestState.PREPARED ? [_request] : []);

        public ValueTask<IReadOnlyList<OrderId>> FindCancelableOrderIdsAsync(
            KillSwitch killSwitch, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<OrderId>>([]);
    }
}
