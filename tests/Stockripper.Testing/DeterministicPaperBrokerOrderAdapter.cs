using Stockripper.Application.Execution;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Testing;

public enum PaperBrokerSendBehavior
{
    ACKNOWLEDGE,
    REJECT,
    TIMEOUT_BEFORE_ACCEPTANCE,
    TIMEOUT_AFTER_ACCEPTANCE
}

public enum PaperBrokerCancellationBehavior
{
    ACCEPT,
    TIMEOUT_BEFORE_ACCEPTANCE,
    TIMEOUT_AFTER_ACCEPTANCE
}

public sealed class DeterministicPaperBrokerOrderAdapter(TimeProvider timeProvider)
    : IPaperBrokerOrderAdapter, IPaperBrokerReconciliationAdapter
{
    private readonly Dictionary<string, StoredOrder> _orders = new(StringComparer.Ordinal);
    private readonly Queue<PaperBrokerSendBehavior> _behaviors = new();
    private readonly Queue<PaperBrokerCancellationBehavior> _cancellationBehaviors = new();
    private readonly Dictionary<string, StoredCancellation> _cancellations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaperReconciliationFill> _fills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaperReconciliationPosition> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaperBrokerExecutionUpdate> _executionUpdates =
        new(StringComparer.Ordinal);
    private int _nextOrderNumber;
    private int _nextCancellationNumber;

    public int SendCallCount { get; private set; }
    public int ExternalOrderCount => _orders.Count;
    public bool LookupAvailable { get; set; } = true;
    public int CancellationCallCount { get; private set; }
    public int ExternalCancellationCount => _cancellations.Count;
    public bool ReconciliationAvailable { get; set; } = true;
    public decimal BuyingPower { get; set; } = 25_000m;
    public string Currency { get; set; } = "USD";
    public IntentAccountBinding? ObservedBindingOverride { get; set; }

    public void Enqueue(PaperBrokerSendBehavior behavior) => _behaviors.Enqueue(behavior);

    public void EnqueueCancellation(PaperBrokerCancellationBehavior behavior) =>
        _cancellationBehaviors.Enqueue(behavior);

    public void SetOrderState(string brokerClientOrderId, OrderState state,
        decimal filledQuantity)
    {
        if (!_orders.TryGetValue(brokerClientOrderId, out var order)
            || order.Result.Type != PaperBrokerResultType.ACKNOWLEDGED
            || state is OrderState.CREATED or OrderState.SUBMITTING
                or OrderState.SUBMISSION_UNKNOWN or OrderState.RECONCILIATION_REQUIRED
            || filledQuantity < 0m || filledQuantity > order.Request.Quantity)
        {
            throw new InvalidOperationException("The fake Broker Order state is invalid.");
        }

        order.State = state;
        order.FilledQuantity = filledQuantity;
    }

    public void AddFill(PaperReconciliationFill fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        _fills.Add(fill.Identity, fill);
    }

    public void SetPosition(PaperReconciliationPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        _positions[position.InstrumentIdentifier] = position;
    }

    public void AddExecutionUpdate(PaperBrokerExecutionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        _executionUpdates.Add(update.EventReference, update);
    }

    public ValueTask<PaperBrokerResult> SendAsync(
        OrderRecord preparedOrder,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (preparedOrder.AccountBinding.Environment != BrokerEnvironment.PAPER
            || preparedOrder.State != OrderState.SUBMITTING)
        {
            throw new InvalidOperationException("The fake accepts only a prepared paper Order.");
        }

        SendCallCount++;
        if (_orders.TryGetValue(preparedOrder.BrokerClientOrderId, out var existing))
        {
            if (existing.Request != preparedOrder.Request)
            {
                throw new InvalidOperationException("The client Order identifier has conflicting content.");
            }

            return ValueTask.FromResult(existing.Result);
        }

        var behavior = _behaviors.Count == 0
            ? PaperBrokerSendBehavior.ACKNOWLEDGE
            : _behaviors.Dequeue();
        if (behavior == PaperBrokerSendBehavior.TIMEOUT_BEFORE_ACCEPTANCE)
        {
            throw new PaperBrokerOutcomeUnknownException("The fake timed out before acceptance.");
        }

        var now = timeProvider.GetUtcNow();
        var sequence = ++_nextOrderNumber;
        var result = behavior == PaperBrokerSendBehavior.REJECT
            ? PaperBrokerResult.Rejected($"fake-event-{sequence}", now)
            : PaperBrokerResult.Acknowledged($"fake-order-{sequence}", $"fake-event-{sequence}", now);
        _orders.Add(preparedOrder.BrokerClientOrderId, new StoredOrder(
            preparedOrder.AccountBinding, preparedOrder.Request, result,
            behavior == PaperBrokerSendBehavior.REJECT
                ? OrderState.REJECTED
                : OrderState.ACKNOWLEDGED,
            0m));
        if (behavior == PaperBrokerSendBehavior.TIMEOUT_AFTER_ACCEPTANCE)
        {
            throw new PaperBrokerOutcomeUnknownException("The fake timed out after acceptance.");
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask<PaperBrokerResult> FindByClientOrderIdAsync(
        IntentAccountBinding accountBinding,
        string brokerClientOrderId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (accountBinding.Environment != BrokerEnvironment.PAPER)
        {
            throw new InvalidOperationException("The fake lookup accepts only paper authority.");
        }

        if (!LookupAvailable)
        {
            return ValueTask.FromResult(PaperBrokerResult.Unavailable(timeProvider.GetUtcNow()));
        }

        return ValueTask.FromResult(_orders.TryGetValue(brokerClientOrderId, out var stored)
            ? stored.Result
            : PaperBrokerResult.NotFound(timeProvider.GetUtcNow()));
    }

    public ValueTask<PaperBrokerCancellationResult> RequestCancellationAsync(
        CancellationRequest cancellationRequest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationRequest.AccountBinding.Environment != BrokerEnvironment.PAPER
            || cancellationRequest.State != CancellationRequestState.PREPARED
            || !_orders.TryGetValue(cancellationRequest.BrokerClientOrderId, out var storedOrder)
            || !string.Equals(storedOrder.Result.BrokerOrderId,
                cancellationRequest.BrokerOrderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fake cancellation request is invalid.");
        }

        CancellationCallCount++;
        if (_cancellations.TryGetValue(cancellationRequest.IdempotencyKey, out var existing))
        {
            if (existing.OrderId != cancellationRequest.OrderId
                || !string.Equals(existing.ContentDigest,
                    cancellationRequest.ContentDigest, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The cancellation identifier has conflicting content.");
            }

            return ValueTask.FromResult(existing.Result);
        }

        var behavior = _cancellationBehaviors.Count == 0
            ? PaperBrokerCancellationBehavior.ACCEPT
            : _cancellationBehaviors.Dequeue();
        if (behavior == PaperBrokerCancellationBehavior.TIMEOUT_BEFORE_ACCEPTANCE)
        {
            throw new PaperBrokerCancellationOutcomeUnknownException(
                "The fake cancellation timed out before acceptance.");
        }

        var sequence = ++_nextCancellationNumber;
        var result = PaperBrokerCancellationResult.Accepted(
            $"fake-cancel-{sequence}", timeProvider.GetUtcNow());
        _cancellations.Add(cancellationRequest.IdempotencyKey,
            new(cancellationRequest.OrderId, cancellationRequest.ContentDigest, result));
        if (behavior == PaperBrokerCancellationBehavior.TIMEOUT_AFTER_ACCEPTANCE)
        {
            throw new PaperBrokerCancellationOutcomeUnknownException(
                "The fake cancellation timed out after acceptance.");
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask<PaperBrokerReconciliationSnapshot> ObserveAsync(
        IntentAccountBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedBinding.Environment != BrokerEnvironment.PAPER)
        {
            throw new InvalidOperationException("The fake observes only paper authority.");
        }

        if (!ReconciliationAvailable)
        {
            throw new PaperBrokerReconciliationUnavailableException(
                "The fake Broker reconciliation snapshot is unavailable.");
        }

        var observedBinding = ObservedBindingOverride ?? expectedBinding;
        var orders = _orders.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new PaperReconciliationOrder(null, item.Key,
                item.Value.Result.BrokerOrderId, item.Value.Request.InstrumentIdentifier,
                item.Value.Request.Side, item.Value.Request.Quantity,
                item.Value.Request.LimitPrice, item.Value.FilledQuantity,
                item.Value.State))
            .ToArray();
        return ValueTask.FromResult(new PaperBrokerReconciliationSnapshot(
            observedBinding, timeProvider.GetUtcNow(), BuyingPower, Currency,
            orders, _fills.Values.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray(),
            _positions.Values.OrderBy(item => item.InstrumentIdentifier,
                StringComparer.Ordinal).ToArray(),
            _executionUpdates.Values.OrderBy(item => item.BrokerEventTime)
                .ThenBy(item => item.EventReference, StringComparer.Ordinal).ToArray()));
    }

    private sealed class StoredOrder(IntentAccountBinding accountBinding,
        OrderRequestSnapshot request, PaperBrokerResult result, OrderState state,
        decimal filledQuantity)
    {
        public IntentAccountBinding AccountBinding { get; } = accountBinding;
        public OrderRequestSnapshot Request { get; } = request;
        public PaperBrokerResult Result { get; } = result;
        public OrderState State { get; set; } = state;
        public decimal FilledQuantity { get; set; } = filledQuantity;
    }
    private sealed record StoredCancellation(OrderId OrderId, string ContentDigest,
        PaperBrokerCancellationResult Result);
}
