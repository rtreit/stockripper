using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;

namespace Stockripper.Domain.Execution;

public enum CancellationRequestState
{
    PREPARED,
    REQUESTED,
    RECONCILIATION_REQUIRED,
    CONFIRMED,
    SUPERSEDED
}

public sealed record CancellationRequest
{
    private CancellationRequest(CancellationRequestId id, OrderId orderId,
        IntentAccountBinding accountBinding, string brokerClientOrderId,
        string brokerOrderId, string idempotencyKey, string reason,
        CancellationRequestState state, string? brokerRequestReference,
        string? resolutionEventReference, DateTimeOffset preparedAt,
        DateTimeOffset lastUpdatedAt, string contentDigest)
    {
        Id = id;
        OrderId = orderId;
        AccountBinding = accountBinding;
        BrokerClientOrderId = brokerClientOrderId;
        BrokerOrderId = brokerOrderId;
        IdempotencyKey = idempotencyKey;
        Reason = reason;
        State = state;
        BrokerRequestReference = brokerRequestReference;
        ResolutionEventReference = resolutionEventReference;
        PreparedAt = preparedAt;
        LastUpdatedAt = lastUpdatedAt;
        ContentDigest = contentDigest;
    }

    public CancellationRequestId Id { get; }
    public OrderId OrderId { get; }
    public IntentAccountBinding AccountBinding { get; }
    public string BrokerClientOrderId { get; }
    public string BrokerOrderId { get; }
    public string IdempotencyKey { get; }
    public string Reason { get; }
    public CancellationRequestState State { get; private init; }
    public string? BrokerRequestReference { get; private init; }
    public string? ResolutionEventReference { get; private init; }
    public DateTimeOffset PreparedAt { get; }
    public DateTimeOffset LastUpdatedAt { get; private init; }
    public string ContentDigest { get; }
    public bool IsResolved => State is CancellationRequestState.CONFIRMED
        or CancellationRequestState.SUPERSEDED;

    public static CancellationRequest Prepare(CancellationRequestId id,
        OrderRecord order, string reason, DateTimeOffset preparedAt)
    {
        ArgumentNullException.ThrowIfNull(order);
        var time = UtcTimestamp.Require(preparedAt, nameof(preparedAt));
        if (order.AccountBinding.Environment != BrokerEnvironment.PAPER
            || order.State is not (OrderState.ACKNOWLEDGED or OrderState.PARTIALLY_FILLED)
            || string.IsNullOrWhiteSpace(order.BrokerOrderId)
            || string.IsNullOrWhiteSpace(reason)
            || reason != reason.Trim()
            || time < order.LastUpdatedAt)
        {
            throw new DomainRuleViolationException("The cancellation request is invalid.");
        }

        var key = $"stockripper-cancel:{order.Id.Value:D}";
        var digest = ComputeDigest(id, order.Id, order.AccountBinding,
            order.BrokerClientOrderId, order.BrokerOrderId, key, reason, time);
        return new(id, order.Id, order.AccountBinding, order.BrokerClientOrderId,
            order.BrokerOrderId, key, reason, CancellationRequestState.PREPARED,
            null, null, time, time, digest);
    }

    public CancellationRequest BrokerAccepted(string brokerRequestReference, DateTimeOffset at)
    {
        var time = RequireTransitionTime(at);
        if (State != CancellationRequestState.PREPARED
            || string.IsNullOrWhiteSpace(brokerRequestReference)
            || brokerRequestReference != brokerRequestReference.Trim())
        {
            throw new DomainRuleViolationException("The cancellation acceptance is invalid.");
        }

        return this with
        {
            State = CancellationRequestState.REQUESTED,
            BrokerRequestReference = brokerRequestReference,
            LastUpdatedAt = time
        };
    }

    public CancellationRequest RequireReconciliation(DateTimeOffset at)
    {
        var time = RequireTransitionTime(at);
        if (State != CancellationRequestState.PREPARED)
        {
            throw new DomainRuleViolationException("The cancellation outcome cannot become unknown.");
        }

        return this with
        {
            State = CancellationRequestState.RECONCILIATION_REQUIRED,
            LastUpdatedAt = time
        };
    }

    public CancellationRequest Resolve(BrokerExecutionEvent brokerEvent)
    {
        ArgumentNullException.ThrowIfNull(brokerEvent);
        if (brokerEvent.OrderId != OrderId
            || brokerEvent.AccountBinding != AccountBinding
            || !string.Equals(brokerEvent.BrokerClientOrderId, BrokerClientOrderId, StringComparison.Ordinal)
            || !string.Equals(brokerEvent.BrokerOrderId, BrokerOrderId, StringComparison.Ordinal))
        {
            throw new DomainRuleViolationException("The cancellation resolution does not match the Order.");
        }

        if (IsResolved || brokerEvent.Kind is BrokerExecutionEventKind.ACKNOWLEDGED
            or BrokerExecutionEventKind.PARTIALLY_FILLED)
        {
            return this;
        }

        var state = brokerEvent.Kind == BrokerExecutionEventKind.CANCELED
            ? CancellationRequestState.CONFIRMED
            : CancellationRequestState.SUPERSEDED;
        return this with
        {
            State = state,
            ResolutionEventReference = brokerEvent.EventReference,
            LastUpdatedAt = brokerEvent.ReceivedAt
        };
    }

    public static CancellationRequest Restore(CancellationRequestId id, OrderId orderId,
        IntentAccountBinding accountBinding, string brokerClientOrderId,
        string brokerOrderId, string idempotencyKey, string reason,
        CancellationRequestState state, string? brokerRequestReference,
        string? resolutionEventReference, DateTimeOffset preparedAt,
        DateTimeOffset lastUpdatedAt, string contentDigest)
    {
        var prepared = UtcTimestamp.Require(preparedAt, nameof(preparedAt));
        var updated = UtcTimestamp.Require(lastUpdatedAt, nameof(lastUpdatedAt));
        var expectedDigest = ComputeDigest(id, orderId, accountBinding,
            brokerClientOrderId, brokerOrderId, idempotencyKey, reason, prepared);
        var resolved = state is CancellationRequestState.CONFIRMED
            or CancellationRequestState.SUPERSEDED;
        var lifecycleValid = state switch
        {
            CancellationRequestState.PREPARED or CancellationRequestState.RECONCILIATION_REQUIRED =>
                brokerRequestReference is null && resolutionEventReference is null,
            CancellationRequestState.REQUESTED =>
                !string.IsNullOrWhiteSpace(brokerRequestReference) && resolutionEventReference is null,
            CancellationRequestState.CONFIRMED or CancellationRequestState.SUPERSEDED =>
                !string.IsNullOrWhiteSpace(resolutionEventReference),
            _ => false
        };
        if (accountBinding.Environment != BrokerEnvironment.PAPER
            || string.IsNullOrWhiteSpace(brokerClientOrderId)
            || string.IsNullOrWhiteSpace(brokerOrderId)
            || idempotencyKey != $"stockripper-cancel:{orderId.Value:D}"
            || string.IsNullOrWhiteSpace(reason)
            || updated < prepared
            || !lifecycleValid
            || resolved != !string.IsNullOrWhiteSpace(resolutionEventReference)
            || !string.Equals(expectedDigest, contentDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored cancellation request is invalid.");
        }

        return new(id, orderId, accountBinding, brokerClientOrderId, brokerOrderId,
            idempotencyKey, reason, state, brokerRequestReference,
            resolutionEventReference, prepared, updated, contentDigest);
    }

    private DateTimeOffset RequireTransitionTime(DateTimeOffset at)
    {
        var time = UtcTimestamp.Require(at, nameof(at));
        if (time < LastUpdatedAt)
        {
            throw new DomainRuleViolationException("The cancellation transition time is invalid.");
        }

        return time;
    }

    private static string ComputeDigest(CancellationRequestId id, OrderId orderId,
        IntentAccountBinding binding, string brokerClientOrderId, string brokerOrderId,
        string idempotencyKey, string reason, DateTimeOffset preparedAt)
    {
        var value = new
        {
            Schema = "stockripper-cancellation-request-v1",
            Id = id.Value,
            OrderId = orderId.Value,
            Binding = PositionSnapshotBatch.BindingValue(binding),
            BrokerClientOrderId = brokerClientOrderId,
            BrokerOrderId = brokerOrderId,
            IdempotencyKey = idempotencyKey,
            Reason = reason,
            PreparedAt = preparedAt.ToString("O")
        };
        return Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }
}
