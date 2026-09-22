using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Application.Execution;

public sealed record CancellationPreparation(
    CancellationRequest Request,
    OrderRecord Order,
    bool Duplicate);

public interface IPaperCancellationPersistence
{
    ValueTask<CancellationPreparation> PrepareAsync(
        CancellationRequest request,
        OrderRecord cancelPendingOrder,
        OrderState expectedOrderState,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask RecordStateAsync(
        CancellationRequest request,
        CancellationRequestState expectedState,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<CancellationRequest?> FindByOrderAsync(
        OrderId orderId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CancellationRequest>> FindRecoverableAsync(
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<OrderId>> FindCancelableOrderIdsAsync(
        KillSwitch killSwitch,
        CancellationToken cancellationToken);
}

public interface ITriggeredCancellationCoordinator
{
    ValueTask<IReadOnlyList<CancellationRequest>> CancelForTriggeredScopeAsync(
        KillSwitch killSwitch,
        OperatorId actorId,
        string reason,
        CancellationToken cancellationToken);
}

public interface IPaperCancellationRecovery
{
    ValueTask<IReadOnlyList<CancellationRequest>> RecoverAsync(
        OperatorId actorId,
        CancellationToken cancellationToken = default);
}

public sealed class PaperCancellationService(
    IPaperOrderPersistence orderPersistence,
    IPaperCancellationPersistence cancellationPersistence,
    IPaperBrokerOrderAdapter brokerAdapter,
    IExecutionSafetyPersistence safetyPersistence,
    TimeProvider timeProvider) : ITriggeredCancellationCoordinator, IPaperCancellationRecovery
{
    public async ValueTask<CancellationRequest> CancelAsync(
        OrderId orderId,
        OperatorId actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var existing = await cancellationPersistence.FindByOrderAsync(orderId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var order = await orderPersistence.FindOrderAsync(orderId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PaperOrderException("The cancellation Order does not exist.");
        var now = timeProvider.GetUtcNow();
        var request = CancellationRequest.Prepare(
            CancellationRequestId.New(), order, reason, now);
        var pending = order.RequestCancellation(now);
        var preparation = await cancellationPersistence.PrepareAsync(
            request, pending, order.State, actorId, cancellationToken).ConfigureAwait(false);
        return preparation.Duplicate
            ? preparation.Request
            : await DispatchAsync(preparation.Request, actorId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<CancellationRequest>> RecoverAsync(
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var recovered = new List<CancellationRequest>();
        foreach (var request in await cancellationPersistence.FindRecoverableAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            recovered.Add(await DispatchAsync(request, actorId, cancellationToken).ConfigureAwait(false));
        }

        return recovered;
    }

    public async ValueTask<IReadOnlyList<CancellationRequest>> CancelForTriggeredScopeAsync(
        KillSwitchId killSwitchId,
        OperatorId actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var killSwitch = await safetyPersistence.FindKillSwitchAsync(killSwitchId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ExecutionSafetyException("The kill switch does not exist.");
        if (killSwitch.State == KillSwitchState.CLEAR)
        {
            throw new ExecutionSafetyException("A clear kill switch cannot request cancellation.");
        }

        return await CancelForTriggeredScopeAsync(killSwitch, actorId, reason,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<CancellationRequest>> CancelForTriggeredScopeAsync(
        KillSwitch killSwitch,
        OperatorId actorId,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(killSwitch);
        if (killSwitch.State == KillSwitchState.CLEAR)
        {
            throw new ExecutionSafetyException("A clear kill switch cannot request cancellation.");
        }

        var results = new List<CancellationRequest>();
        foreach (var orderId in await cancellationPersistence.FindCancelableOrderIdsAsync(
            killSwitch, cancellationToken).ConfigureAwait(false))
        {
            results.Add(await CancelAsync(orderId, actorId, reason, cancellationToken)
                .ConfigureAwait(false));
        }

        return results;
    }

    private async ValueTask<CancellationRequest> DispatchAsync(
        CancellationRequest request,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        PaperBrokerCancellationResult result;
        try
        {
            result = await brokerAdapter.RequestCancellationAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PaperBrokerCancellationOutcomeUnknownException)
        {
            var unknown = request.RequireReconciliation(timeProvider.GetUtcNow());
            await cancellationPersistence.RecordStateAsync(unknown, request.State,
                actorId, cancellationToken).ConfigureAwait(false);
            return unknown;
        }

        var updated = result.Type == PaperBrokerCancellationResultType.ACCEPTED
            && !string.IsNullOrWhiteSpace(result.BrokerRequestReference)
            ? request.BrokerAccepted(result.BrokerRequestReference, result.ObservedAt)
            : request.RequireReconciliation(result.ObservedAt);
        await cancellationPersistence.RecordStateAsync(updated, request.State,
            actorId, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
