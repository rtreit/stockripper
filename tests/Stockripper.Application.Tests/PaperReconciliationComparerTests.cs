using Stockripper.Application.Execution;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class PaperReconciliationComparerTests
{
    [Fact]
    public void Complete_matching_truth_is_current()
    {
        var values = CreateValues(OrderState.FILLED, 10m);
        var fill = new PaperReconciliationFill("fill-1", "broker-order-1", "AAPL",
            10m, 99m, values.Now);
        var local = new PaperLocalReconciliationState([values.LocalOrder], [fill],
            [new(values.IntentId, 0m, ExposureReservationState.RELEASED)]);
        var broker = Snapshot(values,
            [values.BrokerOrder], [fill], [new("AAPL", 10m, 990m, "USD")]);

        var result = PaperReconciliationComparer.Compare(values.Goal, local, broker,
            values.Now, values.Now);

        Assert.Equal(ReconciliationResult.CURRENT, result.Result);
        Assert.Empty(result.Differences);
        Assert.Equal(990m, result.PositionExposure);
        Assert.Equal(0m, result.OpenOrderExposure);
        Assert.StartsWith("orders:sha256:", result.ComparedOrderRange);
        Assert.StartsWith("fills:sha256:", result.ComparedFillRange);
        Assert.StartsWith("positions:sha256:", result.ComparedPositionSnapshot);
        Assert.Null(result.OperatorAction);
        Assert.True(result.HasValidContentDigest());
    }

    [Fact]
    public void Unevaluated_fill_prevents_current_reconciliation()
    {
        var values = CreateValues(OrderState.FILLED, 10m);
        var fill = new PaperReconciliationFill("fill-1", "broker-order-1", "AAPL",
            10m, 99m, values.Now);
        var local = new PaperLocalReconciliationState([values.LocalOrder], [fill],
            [new(values.IntentId, 0m, ExposureReservationState.RELEASED)], 1);
        var broker = Snapshot(values,
            [values.BrokerOrder], [fill], [new("AAPL", 10m, 990m, "USD")]);

        var result = PaperReconciliationComparer.Compare(values.Goal, local, broker,
            values.Now, values.Now);

        Assert.Equal(ReconciliationResult.DIFFERENCE_LOCKED, result.Result);
        Assert.Contains(result.Differences, difference => difference.Contains(
            "lack post-Fill safety evaluation", StringComparison.Ordinal));
        Assert.NotNull(result.OperatorAction);
    }

    [Fact]
    public void External_order_missing_fill_position_and_reservation_lock_execution()
    {
        var values = CreateValues(OrderState.PARTIALLY_FILLED, 4m);
        var localFill = new PaperReconciliationFill("fill-local", "broker-order-1",
            "AAPL", 4m, 99m, values.Now);
        var external = values.BrokerOrder with
        {
            IntentId = null,
            BrokerClientOrderId = "external-1",
            BrokerOrderId = "external-order-1",
            State = OrderState.ACKNOWLEDGED,
            FilledQuantity = 0m
        };
        var local = new PaperLocalReconciliationState([values.LocalOrder], [localFill], []);
        var broker = Snapshot(values, [values.BrokerOrder, external], [], []);

        var result = PaperReconciliationComparer.Compare(values.Goal, local, broker,
            values.Now, values.Now);

        Assert.Equal(ReconciliationResult.DIFFERENCE_LOCKED, result.Result);
        Assert.Contains(result.Differences,
            item => item.Contains("external to Stockripper", StringComparison.Ordinal));
        Assert.Contains(result.Differences,
            item => item.Contains("Local Fill", StringComparison.Ordinal));
        Assert.Contains(result.Differences,
            item => item.Contains("Position AAPL", StringComparison.Ordinal));
        Assert.Contains(result.Differences,
            item => item.Contains("Exposure reservation", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrong_account_records_a_difference_without_accepting_current_truth()
    {
        var values = CreateValues(OrderState.ACKNOWLEDGED, 0m);
        var local = new PaperLocalReconciliationState([values.LocalOrder], [],
            [new(values.IntentId, 1_000m, ExposureReservationState.RESERVED)]);
        var broker = Snapshot(values, [values.BrokerOrder], [], []) with
        {
            ObservedBinding = new IntentAccountBinding(
                values.Binding.BrokerConnectionVersion, BrokerEnvironment.PAPER,
                values.Binding.EndpointIdentity, "wrong-paper-account")
        };

        var result = PaperReconciliationComparer.Compare(values.Goal, local, broker,
            values.Now, values.Now);

        Assert.Equal(ReconciliationResult.DIFFERENCE_LOCKED, result.Result);
        Assert.Contains("Observed Broker authority differs from the Goal binding.",
            result.Differences);
    }

    [Fact]
    public void Duplicate_untrusted_broker_identity_is_rejected()
    {
        var values = CreateValues(OrderState.ACKNOWLEDGED, 0m);
        var local = new PaperLocalReconciliationState([values.LocalOrder], [],
            [new(values.IntentId, 1_000m, ExposureReservationState.RESERVED)]);
        var broker = Snapshot(values, [values.BrokerOrder, values.BrokerOrder], [], []);

        Assert.Throws<ExecutionSafetyException>(() => PaperReconciliationComparer.Compare(
            values.Goal, local, broker, values.Now, values.Now));
    }

    [Fact]
    public void Unresolved_order_keeps_its_exposure_reservation()
    {
        var values = CreateValues(OrderState.RECONCILIATION_REQUIRED, 0m);
        var local = new PaperLocalReconciliationState([values.LocalOrder], [],
            [new(values.IntentId, 1_000m, ExposureReservationState.RESERVED)]);
        var broker = Snapshot(values, [], [], []);

        var result = PaperReconciliationComparer.Compare(values.Goal, local, broker,
            values.Now, values.Now);

        Assert.Equal(ReconciliationResult.DIFFERENCE_LOCKED, result.Result);
        Assert.DoesNotContain(result.Differences,
            item => item.Contains("Exposure reservation", StringComparison.Ordinal));
    }

    private static Values CreateValues(OrderState state, decimal filledQuantity)
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var binding = ExecutionSafetyService.ToAccountBinding(goal);
        var intentId = ExecutionIntentId.New();
        var now = GoalTestData.ReferenceTime;
        var localOrder = new PaperReconciliationOrder(intentId, "client-order-1",
            "broker-order-1", "AAPL", OrderSide.BUY, 10m, 100m,
            filledQuantity, state);
        var brokerOrder = localOrder with { IntentId = null };
        return new(goal, binding, intentId, localOrder, brokerOrder, now);
    }

    private static PaperBrokerReconciliationSnapshot Snapshot(Values values,
        IReadOnlyList<PaperReconciliationOrder> orders,
        IReadOnlyList<PaperReconciliationFill> fills,
        IReadOnlyList<PaperReconciliationPosition> positions) => new(
        values.Binding, values.Now, 25_000m, "USD", orders, fills, positions);

    private sealed record Values(GoalVersion Goal, IntentAccountBinding Binding,
        ExecutionIntentId IntentId, PaperReconciliationOrder LocalOrder,
        PaperReconciliationOrder BrokerOrder, DateTimeOffset Now);
}
