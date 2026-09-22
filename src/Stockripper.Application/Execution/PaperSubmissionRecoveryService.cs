using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Application.Execution;

public interface IPaperSubmissionRecovery
{
    ValueTask<IReadOnlyList<OrderRecord>> RecoverAsync(
        OperatorId actorId,
        CancellationToken cancellationToken = default);
}

public sealed class PaperSubmissionRecoveryService(
    IPaperOrderPersistence orderPersistence,
    IPaperReviewIntentPersistence intentPersistence,
    IPaperBrokerOrderAdapter brokerAdapter,
    TimeProvider timeProvider) : IPaperSubmissionRecovery
{
    public async ValueTask<IReadOnlyList<OrderRecord>> RecoverAsync(
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<OrderRecord>();
        foreach (var order in await orderPersistence.FindRecoverableAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var intent = await intentPersistence.FindIntentAsync(order.IntentId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new PaperOrderException("A recoverable Order has no Execution intent.");
            var lookup = await brokerAdapter.FindByClientOrderIdAsync(
                order.AccountBinding, order.BrokerClientOrderId, cancellationToken)
                .ConfigureAwait(false);
            if (lookup.Type == PaperBrokerResultType.UNAVAILABLE)
            {
                results.Add(order.State == OrderState.SUBMITTING
                    ? await MarkUnknownAsync(order, intent, actorId, cancellationToken)
                        .ConfigureAwait(false)
                    : order);
                continue;
            }

            if (lookup.Type == PaperBrokerResultType.NOT_FOUND)
            {
                var uncertain = order.State == OrderState.SUBMITTING
                    ? await MarkUnknownAsync(order, intent, actorId, cancellationToken)
                        .ConfigureAwait(false)
                    : order;
                var required = uncertain.RequireReconciliation(timeProvider.GetUtcNow());
                var failed = intent.State == ExecutionIntentState.SUBMISSION_PENDING
                    ? intent.MarkSubmissionUnknown(uncertain).MarkSubmissionFailed(required)
                    : intent.MarkSubmissionFailed(required);
                await orderPersistence.RecordTransitionAsync(required, uncertain.State, failed,
                    intent.State == ExecutionIntentState.SUBMISSION_PENDING
                        ? ExecutionIntentState.SUBMISSION_UNKNOWN
                        : intent.State,
                    null, actorId, cancellationToken).ConfigureAwait(false);
                results.Add(required);
                continue;
            }

            results.Add(await ApplyBrokerResultAsync(order, intent, lookup, actorId,
                cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async ValueTask<OrderRecord> MarkUnknownAsync(OrderRecord order,
        ExecutionIntent intent, OperatorId actorId, CancellationToken cancellationToken)
    {
        var unknown = order.MarkSubmissionUnknown(timeProvider.GetUtcNow());
        var unknownIntent = intent.MarkSubmissionUnknown(unknown);
        await orderPersistence.RecordTransitionAsync(unknown, order.State, unknownIntent,
            intent.State, null, actorId, cancellationToken).ConfigureAwait(false);
        return unknown;
    }

    private async ValueTask<OrderRecord> ApplyBrokerResultAsync(OrderRecord order,
        ExecutionIntent intent, PaperBrokerResult result, OperatorId actorId,
        CancellationToken cancellationToken)
    {
        if (result.Type is not (PaperBrokerResultType.ACKNOWLEDGED
            or PaperBrokerResultType.REJECTED)
            || string.IsNullOrWhiteSpace(result.EventReference))
        {
            if (order.State == OrderState.SUBMITTING)
            {
                return await MarkUnknownAsync(order, intent, actorId, cancellationToken)
                    .ConfigureAwait(false);
            }

            return order;
        }

        var eventResult = result.Type == PaperBrokerResultType.ACKNOWLEDGED
            ? BrokerOrderEventResult.ACKNOWLEDGED
            : BrokerOrderEventResult.REJECTED;
        var brokerEvent = BrokerOrderEvent.Create(BrokerOrderEventId.New(), order,
            result.BrokerOrderId, result.EventReference, eventResult, result.ObservedAt);
        var updated = result.Type == PaperBrokerResultType.ACKNOWLEDGED
            ? order.Acknowledge(result.BrokerOrderId!, result.EventReference, result.ObservedAt)
            : order.Reject(result.EventReference, result.ObservedAt);
        var updatedIntent = result.Type == PaperBrokerResultType.ACKNOWLEDGED
            ? intent.MarkSubmitted(updated)
            : intent.MarkSubmissionFailed(updated);
        await orderPersistence.RecordTransitionAsync(updated, order.State, updatedIntent,
            intent.State, brokerEvent, actorId, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
