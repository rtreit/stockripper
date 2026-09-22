using System.Collections.Immutable;
using Stockripper.Application.Brokers;
using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Sessions;

public sealed record ExecutablePreflightObservation
{
    public ExecutablePreflightObservation(
        BrokerConnectionObservation broker,
        DateTimeOffset brokerTime,
        bool brokerClockUsable,
        bool marketSessionOpen,
        bool dataSourcesAvailable,
        IEnumerable<VersionReference> availablePolicies,
        bool killSwitchClear,
        bool reconciliationCurrent,
        DateTimeOffset reconciliationObservedAt,
        bool capitalDataCurrent,
        bool buyingPowerDataCurrent,
        DateTimeOffset capitalObservedAt,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(availablePolicies);
        if (brokerTime.Offset != TimeSpan.Zero
            || reconciliationObservedAt.Offset != TimeSpan.Zero
            || capitalObservedAt.Offset != TimeSpan.Zero
            || positionExposure < 0m
            || openOrderExposure < 0m
            || buyingPower < 0m
            || string.IsNullOrWhiteSpace(currency)
            || currency.Length != 3
            || currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("The executable preflight observation is invalid.");
        }

        Broker = broker;
        BrokerTime = brokerTime;
        BrokerClockUsable = brokerClockUsable;
        MarketSessionOpen = marketSessionOpen;
        DataSourcesAvailable = dataSourcesAvailable;
        AvailablePolicies = availablePolicies.ToImmutableHashSet();
        KillSwitchClear = killSwitchClear;
        ReconciliationCurrent = reconciliationCurrent;
        ReconciliationObservedAt = reconciliationObservedAt;
        CapitalDataCurrent = capitalDataCurrent;
        BuyingPowerDataCurrent = buyingPowerDataCurrent;
        CapitalObservedAt = capitalObservedAt;
        PositionExposure = positionExposure;
        OpenOrderExposure = openOrderExposure;
        BuyingPower = buyingPower;
        Currency = currency;
    }

    public BrokerConnectionObservation Broker { get; }
    public DateTimeOffset BrokerTime { get; }
    public bool BrokerClockUsable { get; }
    public bool MarketSessionOpen { get; }
    public bool DataSourcesAvailable { get; }
    public ImmutableHashSet<VersionReference> AvailablePolicies { get; }
    public bool KillSwitchClear { get; }
    public bool ReconciliationCurrent { get; }
    public DateTimeOffset ReconciliationObservedAt { get; }
    public bool CapitalDataCurrent { get; }
    public bool BuyingPowerDataCurrent { get; }
    public DateTimeOffset CapitalObservedAt { get; }
    public decimal PositionExposure { get; }
    public decimal OpenOrderExposure { get; }
    public decimal BuyingPower { get; }
    public string Currency { get; }
}

public interface IExecutablePreflightProbe
{
    ValueTask<ExecutablePreflightObservation> ObserveAsync(
        VersionReference brokerConnectionVersion,
        CancellationToken cancellationToken);
}

public interface IExecutablePreflightPersistence
{
    ValueTask CompleteAsync(
        Session preflightSession,
        Session completedSession,
        ExecutableSessionPreflight evidence,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<ExecutableSessionPreflight?> FindAsync(
        SessionId sessionId,
        CancellationToken cancellationToken);
}

public interface IExecutableSessionRecoveryPersistence
{
    ValueTask<IReadOnlyList<Session>> FindInterruptedExecutableAsync(
        CancellationToken cancellationToken);
}

public sealed record PaperReviewSessionOutcome(
    Session Session,
    ExecutableSessionPreflight Preflight);

public sealed class PaperReviewSessionPreflightException(string message)
    : InvalidOperationException(message);
