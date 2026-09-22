using Stockripper.Application.Execution;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class PaperBrokerEventServiceTests
{
    [Fact]
    public async Task Duplicate_fill_event_has_one_fill_and_one_exposure_adjustment()
    {
        var values = CreateValues();
        var update = CreateUpdate(values.Order, BrokerExecutionEventKind.PARTIALLY_FILLED,
            4m, "event-partial", new("fill-1", 4m, 99m, null, null, values.Now.AddSeconds(2)));

        var first = await values.Service.IngestAsync(update, values.Actor);
        var duplicate = await values.Service.IngestAsync(update, values.Actor);

        Assert.Equal(PaperBrokerEventDisposition.APPLIED, first.Disposition);
        Assert.Equal(PaperBrokerEventDisposition.DUPLICATE, duplicate.Disposition);
        Assert.Single(values.Store.Fills);
        Assert.Equal(600m, values.Store.RemainingExposure);
        Assert.Equal(OrderState.PARTIALLY_FILLED, values.Store.Order.State);
    }

    [Fact]
    public async Task Late_event_is_recorded_without_regressing_a_filled_order()
    {
        var values = CreateValues();
        await values.Service.IngestAsync(CreateUpdate(values.Order, BrokerExecutionEventKind.FILLED,
            10m, "event-filled", new("fill-2", 10m, 99m, null, null, values.Now.AddSeconds(2))), values.Actor);
        var late = await values.Service.IngestAsync(CreateUpdate(values.Store.Order,
            BrokerExecutionEventKind.PARTIALLY_FILLED, 4m, "event-late",
            new("fill-1", 4m, 99m, null, null, values.Now.AddSeconds(1))), values.Actor);

        Assert.Equal(PaperBrokerEventDisposition.RECORDED_NO_CHANGE, late.Disposition);
        Assert.Equal(OrderState.FILLED, values.Store.Order.State);
        Assert.Equal(0m, values.Store.RemainingExposure);
    }

    [Fact]
    public async Task Kill_switch_state_is_not_a_dependency_for_recording_broker_truth()
    {
        var values = CreateValues();

        var result = await values.Service.IngestAsync(CreateUpdate(values.Order,
            BrokerExecutionEventKind.CANCELED, 0m, "event-canceled", null), values.Actor);

        Assert.Equal(PaperBrokerEventDisposition.APPLIED, result.Disposition);
        Assert.Equal(OrderState.CANCELED, result.Order.State);
        Assert.Equal(0m, values.Store.RemainingExposure);
    }

    private static PaperBrokerExecutionUpdate CreateUpdate(OrderRecord order,
        BrokerExecutionEventKind kind, decimal cumulative, string eventReference,
        PaperBrokerFillData? fill) => new(order.Id, order.AccountBinding,
        order.BrokerClientOrderId, "broker-order-1", eventReference, kind, cumulative,
        GoalTestData.ReferenceTime.AddSeconds(2), $"payload/{eventReference}", fill);

    private static Values CreateValues()
    {
        var now = GoalTestData.ReferenceTime;
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var binding = goal.ExecutionBinding!;
        var accountBinding = new IntentAccountBinding(binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER, binding.EndpointIdentity, binding.ExpectedAccountIdentifier);
        var intent = ExecutionIntent.Create(ExecutionIntentId.New(), DecisionId.New(), goal.Id,
            SessionId.New(), ExecutionMode.PAPER_REVIEW, accountBinding, "AAPL", OrderSide.BUY,
            IntentOrderType.LIMIT, 10m, 100m, IntentTimeInForce.DAY, now.AddMinutes(5),
            RiskEvaluationId.New(), now);
        var risk = RiskEvaluation.Create(intent.RiskEvaluationId, intent.Id, intent.DecisionId,
            goal.Id, intent.SessionId, goal.RiskPolicyVersion, AccountAllocationEntryId.New(),
            ReconciliationId.New(), 0m, 0m, 1_000m, 20_000m, "USD",
            [new RiskCheck(RiskCheckCode.CAPITAL_ALLOCATION, true, "Within allocation.")], now);
        var pending = intent.ApplyRisk(risk).AwaitReview();
        var approval = ReviewApproval.Create(ReviewApprovalId.New(), intent.Id, OperatorId.New(),
            ReviewResult.APPROVED, now.AddMilliseconds(100), pending.ContentDigest,
            now.AddMinutes(4), "Approve the bounded paper order.");
        var approved = pending.ApplyReview(approval);
        var reservation = ExposureReservation.Reserve(ExposureReservationId.New(), approved,
            risk.AllocationEntryId, "USD", now.AddMilliseconds(200));
        var checks = Enum.GetValues<PreSubmitCheckCode>()
            .Select(code => new PreSubmitCheck(code, true, "The final check passed."))
            .ToArray();
        var evaluation = PreSubmitEvaluation.Create(PreSubmitEvaluationId.New(), approved, approval,
            reservation, ReconciliationId.New(), 0m, 0m, 20_000m, "USD", checks,
            now.AddSeconds(1));
        var order = OrderRecord.Prepare(OrderId.New(), approved, evaluation, now.AddSeconds(2));
        var store = new MemoryStore(order);
        var clock = new ManualTimeProvider(now.AddSeconds(3));
        return new(now, OperatorId.New(), order, store,
            new PaperBrokerEventService(store, store, clock));
    }

    private sealed record Values(DateTimeOffset Now, OperatorId Actor, OrderRecord Order,
        MemoryStore Store, PaperBrokerEventService Service);

    private sealed class MemoryStore(OrderRecord order) : IPaperOrderPersistence, IPaperBrokerEventPersistence
    {
        private readonly HashSet<string> _events = new(StringComparer.Ordinal);
        public OrderRecord Order { get; private set; } = order;
        public List<FillRecord> Fills { get; } = [];
        public decimal RemainingExposure { get; private set; } = order.Request.Quantity * order.Request.LimitPrice;

        public ValueTask<PaperBrokerEventIngestion> ApplyAsync(BrokerExecutionEvent brokerEvent,
            FillRecord? fill, OrderRecord updatedOrder, OrderState expectedOrderState,
            decimal remainingExposure, OperatorId actorId, CancellationToken cancellationToken)
        {
            if (!_events.Add(brokerEvent.EventReference))
            {
                return ValueTask.FromResult(new PaperBrokerEventIngestion(
                    PaperBrokerEventDisposition.DUPLICATE, Order, null));
            }

            var changed = updatedOrder != Order;
            Order = updatedOrder;
            RemainingExposure = remainingExposure;
            if (fill is not null && Fills.All(item => item.BrokerFillId != fill.BrokerFillId))
            {
                Fills.Add(fill);
            }

            return ValueTask.FromResult(new PaperBrokerEventIngestion(
                changed ? PaperBrokerEventDisposition.APPLIED : PaperBrokerEventDisposition.RECORDED_NO_CHANGE,
                Order, fill));
        }

        public ValueTask<OrderRecord?> FindOrderAsync(OrderId orderId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<OrderRecord?>(Order.Id == orderId ? Order : null);
        public ValueTask<OrderRecord?> FindByIntentAsync(ExecutionIntentId intentId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<OrderRecord?>(Order.IntentId == intentId ? Order : null);
        public ValueTask<IReadOnlyList<OrderRecord>> FindRecoverableAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<OrderRecord>>([]);
        public ValueTask RecordEvaluationAsync(PreSubmitEvaluation evaluation, OperatorId actorId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask PrepareAsync(PreSubmitEvaluation evaluation, OrderRecord order, ExecutionIntent pendingIntent, ExecutionIntentState expectedIntentState, OperatorId actorId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask RecordTransitionAsync(OrderRecord order, OrderState expectedOrderState, ExecutionIntent intent, ExecutionIntentState expectedIntentState, BrokerOrderEvent? brokerEvent, OperatorId actorId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
