using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;

namespace Stockripper.Domain.Execution;

public enum BrokerExecutionEventKind
{
    ACKNOWLEDGED,
    PARTIALLY_FILLED,
    FILLED,
    CANCELED,
    REJECTED,
    EXPIRED
}

public sealed record BrokerExecutionEvent
{
    private BrokerExecutionEvent(
        BrokerExecutionEventId id,
        OrderId orderId,
        IntentAccountBinding accountBinding,
        string brokerClientOrderId,
        string brokerOrderId,
        string eventReference,
        BrokerExecutionEventKind kind,
        decimal cumulativeFilledQuantity,
        DateTimeOffset brokerEventTime,
        DateTimeOffset receivedAt,
        string sourcePayloadReference,
        string contentDigest)
    {
        Id = id;
        OrderId = orderId;
        AccountBinding = accountBinding;
        BrokerClientOrderId = brokerClientOrderId;
        BrokerOrderId = brokerOrderId;
        EventReference = eventReference;
        Kind = kind;
        CumulativeFilledQuantity = cumulativeFilledQuantity;
        BrokerEventTime = brokerEventTime;
        ReceivedAt = receivedAt;
        SourcePayloadReference = sourcePayloadReference;
        ContentDigest = contentDigest;
    }

    public BrokerExecutionEventId Id { get; }
    public OrderId OrderId { get; }
    public IntentAccountBinding AccountBinding { get; }
    public string BrokerClientOrderId { get; }
    public string BrokerOrderId { get; }
    public string EventReference { get; }
    public BrokerExecutionEventKind Kind { get; }
    public decimal CumulativeFilledQuantity { get; }
    public DateTimeOffset BrokerEventTime { get; }
    public DateTimeOffset ReceivedAt { get; }
    public string SourcePayloadReference { get; }
    public string ContentDigest { get; }

    public static BrokerExecutionEvent Create(
        BrokerExecutionEventId id,
        OrderRecord order,
        IntentAccountBinding observedBinding,
        string brokerClientOrderId,
        string brokerOrderId,
        string eventReference,
        BrokerExecutionEventKind kind,
        decimal cumulativeFilledQuantity,
        DateTimeOffset brokerEventTime,
        DateTimeOffset receivedAt,
        string sourcePayloadReference)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(observedBinding);
        var occurred = UtcTimestamp.Require(brokerEventTime, nameof(brokerEventTime));
        var received = UtcTimestamp.Require(receivedAt, nameof(receivedAt));
        if (observedBinding.Environment != BrokerEnvironment.PAPER
            || observedBinding != order.AccountBinding
            || !string.Equals(brokerClientOrderId, order.BrokerClientOrderId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(brokerOrderId)
            || string.IsNullOrWhiteSpace(eventReference)
            || string.IsNullOrWhiteSpace(sourcePayloadReference)
            || cumulativeFilledQuantity < 0m
            || cumulativeFilledQuantity > order.Request.Quantity
            || (kind == BrokerExecutionEventKind.ACKNOWLEDGED && cumulativeFilledQuantity != 0m)
            || (kind == BrokerExecutionEventKind.PARTIALLY_FILLED
                && (cumulativeFilledQuantity <= 0m || cumulativeFilledQuantity >= order.Request.Quantity))
            || (kind == BrokerExecutionEventKind.FILLED
                && cumulativeFilledQuantity != order.Request.Quantity)
            || (kind == BrokerExecutionEventKind.REJECTED && cumulativeFilledQuantity != 0m))
        {
            throw new DomainRuleViolationException("The Broker execution event is invalid.");
        }

        if (order.BrokerOrderId is not null
            && !string.Equals(order.BrokerOrderId, brokerOrderId, StringComparison.Ordinal))
        {
            throw new DomainRuleViolationException("The Broker Order identifier changed.");
        }

        var digest = ComputeDigest(id, order.Id, observedBinding, brokerClientOrderId,
            brokerOrderId, eventReference, kind, cumulativeFilledQuantity, occurred,
            received, sourcePayloadReference);
        return new(id, order.Id, observedBinding, brokerClientOrderId, brokerOrderId,
            eventReference, kind, cumulativeFilledQuantity, occurred, received,
            sourcePayloadReference, digest);
    }

    private static string ComputeDigest(BrokerExecutionEventId id, OrderId orderId,
        IntentAccountBinding binding, string clientOrderId, string brokerOrderId,
        string eventReference, BrokerExecutionEventKind kind, decimal cumulativeQuantity,
        DateTimeOffset eventTime, DateTimeOffset receivedAt, string payloadReference)
    {
        var value = new
        {
            Schema = "stockripper-broker-execution-event-v1",
            Id = id.Value,
            OrderId = orderId.Value,
            Connection = new
            {
                binding.BrokerConnectionVersion.Identifier,
                binding.BrokerConnectionVersion.Version,
                binding.BrokerConnectionVersion.Digest
            },
            Environment = binding.Environment.ToString(),
            binding.EndpointIdentity,
            binding.AccountIdentifier,
            BrokerClientOrderId = clientOrderId,
            BrokerOrderId = brokerOrderId,
            EventReference = eventReference,
            Kind = kind.ToString(),
            CumulativeFilledQuantity = cumulativeQuantity.ToString("G29", CultureInfo.InvariantCulture),
            BrokerEventTime = eventTime.ToString("O"),
            ReceivedAt = receivedAt.ToString("O"),
            SourcePayloadReference = payloadReference
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }
}

public sealed record FillRecord
{
    private FillRecord(FillId id, string? brokerFillId, OrderId orderId,
        BrokerExecutionEventId eventId, string brokerOrderId, string instrumentIdentifier,
        decimal quantity, decimal price, decimal? feeAmount, string? feeCurrency,
        DateTimeOffset brokerExecutionTime, DateTimeOffset receivedAt,
        string sourcePayloadReference, string contentDigest)
    {
        Id = id;
        BrokerFillId = brokerFillId;
        OrderId = orderId;
        EventId = eventId;
        BrokerOrderId = brokerOrderId;
        InstrumentIdentifier = instrumentIdentifier;
        Quantity = quantity;
        Price = price;
        FeeAmount = feeAmount;
        FeeCurrency = feeCurrency;
        BrokerExecutionTime = brokerExecutionTime;
        ReceivedAt = receivedAt;
        SourcePayloadReference = sourcePayloadReference;
        ContentDigest = contentDigest;
    }

    public FillId Id { get; }
    public string? BrokerFillId { get; }
    public OrderId OrderId { get; }
    public BrokerExecutionEventId EventId { get; }
    public string BrokerOrderId { get; }
    public string InstrumentIdentifier { get; }
    public decimal Quantity { get; }
    public decimal Price { get; }
    public decimal? FeeAmount { get; }
    public string? FeeCurrency { get; }
    public DateTimeOffset BrokerExecutionTime { get; }
    public DateTimeOffset ReceivedAt { get; }
    public string SourcePayloadReference { get; }
    public string ContentDigest { get; }

    public static FillRecord Create(FillId id, BrokerExecutionEvent brokerEvent,
        OrderRecord order, string? brokerFillId, decimal quantity, decimal price,
        decimal? feeAmount, string? feeCurrency, DateTimeOffset brokerExecutionTime)
    {
        ArgumentNullException.ThrowIfNull(brokerEvent);
        ArgumentNullException.ThrowIfNull(order);
        var executionTime = UtcTimestamp.Require(brokerExecutionTime, nameof(brokerExecutionTime));
        if (brokerEvent.OrderId != order.Id
            || brokerEvent.Kind is not (BrokerExecutionEventKind.PARTIALLY_FILLED or BrokerExecutionEventKind.FILLED)
            || quantity <= 0m || quantity > brokerEvent.CumulativeFilledQuantity
            || decimal.Truncate(quantity) != quantity
            || price <= 0m || feeAmount < 0m
            || (feeAmount.HasValue != !string.IsNullOrWhiteSpace(feeCurrency))
            || (feeCurrency is not null
                && (feeCurrency.Length != 3 || feeCurrency.Any(character => character is < 'A' or > 'Z'))))
        {
            throw new DomainRuleViolationException("The Fill record is invalid.");
        }

        var value = new
        {
            Schema = "stockripper-fill-v1",
            Id = id.Value,
            BrokerFillId = string.IsNullOrWhiteSpace(brokerFillId) ? null : brokerFillId,
            OrderId = order.Id.Value,
            EventId = brokerEvent.Id.Value,
            brokerEvent.BrokerOrderId,
            order.Request.InstrumentIdentifier,
            Quantity = quantity.ToString("G29", CultureInfo.InvariantCulture),
            Price = price.ToString("G29", CultureInfo.InvariantCulture),
            FeeAmount = feeAmount?.ToString("G29", CultureInfo.InvariantCulture),
            FeeCurrency = feeCurrency,
            BrokerExecutionTime = executionTime.ToString("O"),
            ReceivedAt = brokerEvent.ReceivedAt.ToString("O"),
            brokerEvent.SourcePayloadReference
        };
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
        return new(id, string.IsNullOrWhiteSpace(brokerFillId) ? null : brokerFillId,
            order.Id, brokerEvent.Id, brokerEvent.BrokerOrderId, order.Request.InstrumentIdentifier,
            quantity, price, feeAmount, feeCurrency, executionTime, brokerEvent.ReceivedAt,
            brokerEvent.SourcePayloadReference, digest);
    }
}
