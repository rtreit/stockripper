using Stockripper.Application.Execution;
using Stockripper.Domain.Execution;

namespace Stockripper.Infrastructure.Brokers;

public sealed class UnavailablePaperBrokerOrderAdapter(TimeProvider timeProvider)
    : IPaperBrokerOrderAdapter, IPaperBrokerReconciliationAdapter
{
    public ValueTask<PaperBrokerResult> SendAsync(
        OrderRecord preparedOrder,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The paper Broker adapter is not configured.");

    public ValueTask<PaperBrokerResult> FindByClientOrderIdAsync(
        IntentAccountBinding accountBinding,
        string brokerClientOrderId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (accountBinding.Environment != BrokerEnvironment.PAPER
            || string.IsNullOrWhiteSpace(brokerClientOrderId))
        {
            throw new InvalidOperationException(
                "The unavailable adapter accepts only paper Order lookup.");
        }

        return ValueTask.FromResult(PaperBrokerResult.Unavailable(timeProvider.GetUtcNow()));
    }

    public ValueTask<PaperBrokerCancellationResult> RequestCancellationAsync(
        CancellationRequest cancellationRequest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationRequest.AccountBinding.Environment != BrokerEnvironment.PAPER
            || cancellationRequest.State != CancellationRequestState.PREPARED)
        {
            throw new InvalidOperationException(
                "The unavailable adapter accepts only prepared paper cancellation.");
        }

        return ValueTask.FromResult(
            PaperBrokerCancellationResult.Unavailable(timeProvider.GetUtcNow()));
    }

    public ValueTask<PaperBrokerReconciliationSnapshot> ObserveAsync(
        IntentAccountBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedBinding.Environment != BrokerEnvironment.PAPER)
        {
            throw new InvalidOperationException(
                "The unavailable adapter accepts only paper reconciliation.");
        }

        throw new PaperBrokerReconciliationUnavailableException(
            "The paper Broker reconciliation adapter is not configured.");
    }
}
