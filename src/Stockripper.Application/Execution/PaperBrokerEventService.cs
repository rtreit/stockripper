using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Application.Execution;

public sealed record PaperBrokerFillData(
    string? BrokerFillId,
    decimal Quantity,
    decimal Price,
    decimal? FeeAmount,
    string? FeeCurrency,
    DateTimeOffset BrokerExecutionTime);

public sealed record PaperBrokerExecutionUpdate(
    OrderId OrderId,
    IntentAccountBinding ObservedBinding,
    string BrokerClientOrderId,
    string BrokerOrderId,
    string EventReference,
    BrokerExecutionEventKind Kind,
    decimal CumulativeFilledQuantity,
    DateTimeOffset BrokerEventTime,
    string SourcePayloadReference,
    PaperBrokerFillData? Fill);

public enum PaperBrokerEventDisposition
{
    APPLIED,
    RECORDED_NO_CHANGE,
    DUPLICATE
}

public sealed record PaperBrokerEventIngestion(
    PaperBrokerEventDisposition Disposition,
    OrderRecord Order,
    FillRecord? Fill);

public interface IPaperBrokerEventPersistence
{
    ValueTask<PaperBrokerEventIngestion> ApplyAsync(
        BrokerExecutionEvent brokerEvent,
        FillRecord? fill,
        OrderRecord updatedOrder,
        OrderState expectedOrderState,
        decimal remainingExposure,
        OperatorId actorId,
        CancellationToken cancellationToken);
}

public sealed class PaperBrokerEventService(
    IPaperOrderPersistence orderPersistence,
    IPaperBrokerEventPersistence eventPersistence,
    TimeProvider timeProvider)
{
    public async ValueTask<PaperBrokerEventIngestion> IngestAsync(
        PaperBrokerExecutionUpdate update,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var order = await orderPersistence.FindOrderAsync(update.OrderId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PaperOrderException("The Broker event Order does not exist.");
        var brokerEvent = BrokerExecutionEvent.Create(
            BrokerExecutionEventId.New(), order, update.ObservedBinding,
            update.BrokerClientOrderId, update.BrokerOrderId, update.EventReference,
            update.Kind, update.CumulativeFilledQuantity, update.BrokerEventTime,
            timeProvider.GetUtcNow(), update.SourcePayloadReference);

        var fillKind = update.Kind is BrokerExecutionEventKind.PARTIALLY_FILLED
            or BrokerExecutionEventKind.FILLED;
        if (fillKind != (update.Fill is not null))
        {
            throw new PaperOrderException("A Fill payload is required only for a Fill event.");
        }

        var fill = update.Fill is null
            ? null
            : FillRecord.Create(FillId.New(), brokerEvent, order, update.Fill.BrokerFillId,
                update.Fill.Quantity, update.Fill.Price, update.Fill.FeeAmount,
                update.Fill.FeeCurrency, update.Fill.BrokerExecutionTime);
        var updated = order.Apply(brokerEvent);
        var remainingExposure = updated.State is OrderState.FILLED or OrderState.CANCELED
            or OrderState.REJECTED or OrderState.EXPIRED
            ? 0m
            : checked((updated.Request.Quantity - updated.FilledQuantity) * updated.Request.LimitPrice);
        return await eventPersistence.ApplyAsync(brokerEvent, fill, updated, order.State,
            remainingExposure, actorId, cancellationToken).ConfigureAwait(false);
    }
}
