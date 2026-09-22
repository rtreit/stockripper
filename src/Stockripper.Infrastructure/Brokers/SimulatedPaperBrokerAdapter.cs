using Stockripper.Application.Brokers;
using Stockripper.Application.Execution;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;

namespace Stockripper.Infrastructure.Brokers;

public sealed record SimulatedPaperBrokerOptions(
    VersionReference BrokerConnectionVersion,
    string EndpointIdentity,
    string AccountIdentifier,
    decimal InitialBuyingPower,
    string Currency,
    IReadOnlyList<VersionReference> AvailablePolicies);

public interface ISimulatedPaperBroker
{
    ValueTask<PaperBrokerExecutionUpdate> FillAsync(
        OrderRecord order,
        decimal fillPrice,
        CancellationToken cancellationToken = default);

    PaperPositionObservation CreatePositionObservation(FillId fillId);
}

public sealed class SimulatedPaperBrokerAdapter(
    SimulatedPaperBrokerOptions options,
    TimeProvider timeProvider) :
    IPaperBrokerOrderAdapter,
    IPaperBrokerReconciliationAdapter,
    IBrokerConnectionProbe,
    IExecutablePreflightProbe,
    IIntentRiskProbe,
    ISimulatedPaperBroker
{
    private static readonly string[] Capabilities =
        ["EQUITY_LIMIT_DAY", "ORDER_HISTORY", "POSITIONS"];
    private readonly object gate = new();
    private readonly Dictionary<string, StoredOrder> orders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaperReconciliationFill> fills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PositionValue> positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaperBrokerExecutionUpdate> executionUpdates =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaperBrokerCancellationResult> cancellations =
        new(StringComparer.Ordinal);
    private int nextOrderNumber;
    private int nextEventNumber;
    private int nextCancellationNumber;

    public ValueTask<BrokerConnectionObservation> ObserveAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CreateBrokerObservation());
    }

    public ValueTask<ExecutablePreflightObservation> ObserveAsync(
        VersionReference brokerConnectionVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (brokerConnectionVersion != options.BrokerConnectionVersion)
        {
            throw new InvalidOperationException(
                "The simulated Broker connection version does not match.");
        }

        var now = timeProvider.GetUtcNow();
        var account = ReadAccount();
        return ValueTask.FromResult(new ExecutablePreflightObservation(
            CreateBrokerObservation(), now, true, true, true,
            options.AvailablePolicies, true, true, now, true, true, now,
            account.PositionExposure, account.OpenOrderExposure,
            account.BuyingPower, options.Currency));
    }

    ValueTask<IntentRiskObservation> IIntentRiskProbe.ObserveAsync(
        IntentAccountBinding accountBinding,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireBinding(accountBinding);
        var now = timeProvider.GetUtcNow();
        var account = ReadAccount();
        return ValueTask.FromResult(new IntentRiskObservation(
            CreateBrokerObservation(), true, true, true, now,
            account.PositionExposure, account.OpenOrderExposure,
            account.BuyingPower, options.Currency, true, account.OpenOrderCount,
            account.PositionQuantities));
    }

    public ValueTask<PaperBrokerResult> SendAsync(
        OrderRecord preparedOrder,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireBinding(preparedOrder.AccountBinding);
        if (preparedOrder.State != OrderState.SUBMITTING)
        {
            throw new InvalidOperationException(
                "The simulated Broker accepts only a prepared paper Order.");
        }

        lock (gate)
        {
            if (orders.TryGetValue(preparedOrder.BrokerClientOrderId, out var existing))
            {
                if (existing.Request != preparedOrder.Request)
                {
                    throw new InvalidOperationException(
                        "The simulated client Order identifier has conflicting content.");
                }

                return ValueTask.FromResult(existing.Result);
            }

            var sequence = ++nextOrderNumber;
            var now = timeProvider.GetUtcNow();
            var result = PaperBrokerResult.Acknowledged(
                $"simulated-order-{sequence}", $"simulated-ack-{sequence}", now);
            orders.Add(preparedOrder.BrokerClientOrderId, new StoredOrder(
                preparedOrder.Id, preparedOrder.IntentId, preparedOrder.AccountBinding,
                preparedOrder.Request, result, OrderState.ACKNOWLEDGED, 0m));
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<PaperBrokerResult> FindByClientOrderIdAsync(
        IntentAccountBinding accountBinding,
        string brokerClientOrderId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireBinding(accountBinding);
        lock (gate)
        {
            return ValueTask.FromResult(
                orders.TryGetValue(brokerClientOrderId, out var order)
                    ? order.Result
                    : PaperBrokerResult.NotFound(timeProvider.GetUtcNow()));
        }
    }

    public ValueTask<PaperBrokerCancellationResult> RequestCancellationAsync(
        CancellationRequest cancellationRequest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireBinding(cancellationRequest.AccountBinding);
        lock (gate)
        {
            if (cancellations.TryGetValue(cancellationRequest.IdempotencyKey,
                    out var existing))
            {
                return ValueTask.FromResult(existing);
            }

            if (!orders.TryGetValue(cancellationRequest.BrokerClientOrderId,
                    out var order)
                || !string.Equals(order.Result.BrokerOrderId,
                    cancellationRequest.BrokerOrderId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The simulated cancellation authority is invalid.");
            }

            var now = timeProvider.GetUtcNow();
            var result = PaperBrokerCancellationResult.Accepted(
                $"simulated-cancel-{++nextCancellationNumber}", now);
            cancellations.Add(cancellationRequest.IdempotencyKey, result);
            if (!IsTerminal(order.State))
            {
                order.State = OrderState.CANCELED;
                var update = new PaperBrokerExecutionUpdate(order.OrderId,
                    order.AccountBinding, cancellationRequest.BrokerClientOrderId,
                    cancellationRequest.BrokerOrderId,
                    $"simulated-cancel-event-{++nextEventNumber}",
                    BrokerExecutionEventKind.CANCELED, order.FilledQuantity, now,
                    $"simulated://cancel/{cancellationRequest.Id.Value:D}", null);
                executionUpdates.Add(update.EventReference, update);
            }

            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<PaperBrokerExecutionUpdate> FillAsync(
        OrderRecord order,
        decimal fillPrice,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireBinding(order.AccountBinding);
        if (fillPrice <= 0m || string.IsNullOrWhiteSpace(order.BrokerOrderId))
        {
            throw new InvalidOperationException("The simulated Fill input is invalid.");
        }

        lock (gate)
        {
            if (!orders.TryGetValue(order.BrokerClientOrderId, out var stored)
                || stored.OrderId != order.Id
                || IsTerminal(stored.State))
            {
                throw new InvalidOperationException(
                    "The simulated Order cannot receive a Fill.");
            }

            var quantity = stored.Request.Quantity - stored.FilledQuantity;
            if (quantity <= 0m)
            {
                throw new InvalidOperationException(
                    "The simulated Order has no remaining quantity.");
            }

            var now = timeProvider.GetUtcNow();
            var eventNumber = ++nextEventNumber;
            var fillIdentity = $"simulated-fill-{eventNumber}";
            stored.FilledQuantity = stored.Request.Quantity;
            stored.State = OrderState.FILLED;
            var signedQuantity = stored.Request.Side == OrderSide.SELL
                ? -quantity
                : quantity;
            if (!positions.TryGetValue(stored.Request.InstrumentIdentifier,
                    out var position))
            {
                position = new PositionValue();
                positions.Add(stored.Request.InstrumentIdentifier, position);
            }

            position.Quantity += signedQuantity;
            position.LastPrice = fillPrice;
            var fill = new PaperReconciliationFill(fillIdentity,
                stored.Result.BrokerOrderId!, stored.Request.InstrumentIdentifier,
                quantity, fillPrice, now);
            fills.Add(fillIdentity, fill);
            var update = new PaperBrokerExecutionUpdate(stored.OrderId,
                stored.AccountBinding, order.BrokerClientOrderId,
                stored.Result.BrokerOrderId!, $"simulated-fill-event-{eventNumber}",
                BrokerExecutionEventKind.FILLED, stored.FilledQuantity, now,
                $"simulated://fill/{fillIdentity}",
                new(fillIdentity, quantity, fillPrice, null, null, now));
            executionUpdates.Add(update.EventReference, update);
            return ValueTask.FromResult(update);
        }
    }

    public PaperPositionObservation CreatePositionObservation(FillId fillId)
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            return new PaperPositionObservation(fillId, ExpectedBinding(),
                $"simulated-position-{Guid.NewGuid():N}", now,
                $"simulated://positions/{now:O}", positions
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new PaperPositionData(item.Key,
                        item.Value.Quantity, item.Value.LastPrice,
                        Math.Abs(item.Value.Quantity * item.Value.LastPrice),
                        options.Currency))
                    .ToArray());
        }
    }

    ValueTask<PaperBrokerReconciliationSnapshot> IPaperBrokerReconciliationAdapter.ObserveAsync(
        IntentAccountBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireBinding(expectedBinding);
        lock (gate)
        {
            var account = ReadAccountUnsafe();
            var brokerOrders = orders.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new PaperReconciliationOrder(item.Value.IntentId,
                    item.Key, item.Value.Result.BrokerOrderId,
                    item.Value.Request.InstrumentIdentifier, item.Value.Request.Side,
                    item.Value.Request.Quantity, item.Value.Request.LimitPrice,
                    item.Value.FilledQuantity, item.Value.State))
                .ToArray();
            var brokerPositions = positions.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new PaperReconciliationPosition(item.Key,
                    item.Value.Quantity,
                    Math.Abs(item.Value.Quantity * item.Value.LastPrice),
                    options.Currency))
                .ToArray();
            return ValueTask.FromResult(new PaperBrokerReconciliationSnapshot(
                expectedBinding, timeProvider.GetUtcNow(), account.BuyingPower,
                options.Currency, brokerOrders,
                fills.Values.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray(),
                brokerPositions, executionUpdates.Values
                    .OrderBy(item => item.BrokerEventTime)
                    .ThenBy(item => item.EventReference, StringComparer.Ordinal)
                    .ToArray()));
        }
    }

    private BrokerConnectionObservation CreateBrokerObservation() => new(
        BrokerEnvironment.PAPER, options.EndpointIdentity, options.AccountIdentifier,
        Capabilities, timeProvider.GetUtcNow());

    private AccountObservation ReadAccount()
    {
        lock (gate)
        {
            return ReadAccountUnsafe();
        }
    }

    private AccountObservation ReadAccountUnsafe()
    {
        var positionExposure = positions.Values.Sum(item =>
            Math.Abs(item.Quantity * item.LastPrice));
        var activeOrders = orders.Values.Where(item => !IsTerminal(item.State)).ToArray();
        var openOrderExposure = activeOrders.Sum(item =>
            (item.Request.Quantity - item.FilledQuantity) * item.Request.LimitPrice);
        var buyingPower = Math.Max(0m,
            options.InitialBuyingPower - positionExposure - openOrderExposure);
        return new AccountObservation(positionExposure, openOrderExposure, buyingPower,
            activeOrders.Length, positions.ToDictionary(item => item.Key,
                item => item.Value.Quantity, StringComparer.Ordinal));
    }

    private IntentAccountBinding ExpectedBinding() => new(
        options.BrokerConnectionVersion, BrokerEnvironment.PAPER,
        options.EndpointIdentity, options.AccountIdentifier);

    private void RequireBinding(IntentAccountBinding binding)
    {
        if (binding != ExpectedBinding())
        {
            throw new InvalidOperationException(
                "The simulated Broker authority does not match.");
        }
    }

    private static bool IsTerminal(OrderState state) => state is OrderState.FILLED
        or OrderState.CANCELED or OrderState.REJECTED or OrderState.EXPIRED;

    private sealed class StoredOrder(
        OrderId orderId,
        ExecutionIntentId intentId,
        IntentAccountBinding accountBinding,
        OrderRequestSnapshot request,
        PaperBrokerResult result,
        OrderState state,
        decimal filledQuantity)
    {
        public OrderId OrderId { get; } = orderId;
        public ExecutionIntentId IntentId { get; } = intentId;
        public IntentAccountBinding AccountBinding { get; } = accountBinding;
        public OrderRequestSnapshot Request { get; } = request;
        public PaperBrokerResult Result { get; } = result;
        public OrderState State { get; set; } = state;
        public decimal FilledQuantity { get; set; } = filledQuantity;
    }

    private sealed class PositionValue
    {
        public decimal Quantity { get; set; }
        public decimal LastPrice { get; set; }
    }

    private sealed record AccountObservation(
        decimal PositionExposure,
        decimal OpenOrderExposure,
        decimal BuyingPower,
        int OpenOrderCount,
        IReadOnlyDictionary<string, decimal> PositionQuantities);
}
