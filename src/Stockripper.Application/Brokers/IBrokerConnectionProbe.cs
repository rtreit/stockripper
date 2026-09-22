namespace Stockripper.Application.Brokers;

public interface IBrokerConnectionProbe
{
    ValueTask<BrokerConnectionObservation> ObserveAsync(CancellationToken cancellationToken);
}
