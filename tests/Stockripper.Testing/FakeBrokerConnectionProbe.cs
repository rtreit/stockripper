using Stockripper.Application.Brokers;

namespace Stockripper.Testing;

public sealed class FakeBrokerConnectionProbe(BrokerConnectionObservation observation)
    : IBrokerConnectionProbe
{
    public int ObservationCount { get; private set; }

    public ValueTask<BrokerConnectionObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObservationCount++;
        return ValueTask.FromResult(observation);
    }
}
