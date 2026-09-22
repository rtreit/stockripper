using Stockripper.Application.Brokers;
using System.Collections.Immutable;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;

namespace Stockripper.Application.Execution;

public sealed record IntentRiskObservation
{
    public IntentRiskObservation(
        BrokerConnectionObservation broker,
        bool killSwitchClear,
        bool reconciliationCurrent,
        bool capitalDataCurrent,
        DateTimeOffset observedAt,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency,
        bool postFillCurrent = true,
        int openOrderCount = 0,
        IReadOnlyDictionary<string, decimal>? positionQuantities = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        if (observedAt.Offset != TimeSpan.Zero
            || positionExposure < 0m
            || openOrderExposure < 0m
            || buyingPower < 0m
            || openOrderCount < 0
            || string.IsNullOrWhiteSpace(currency)
            || currency.Length != 3
            || currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("The intent risk observation is invalid.");
        }

        Broker = broker;
        KillSwitchClear = killSwitchClear;
        ReconciliationCurrent = reconciliationCurrent;
        CapitalDataCurrent = capitalDataCurrent;
        ObservedAt = observedAt;
        PositionExposure = positionExposure;
        OpenOrderExposure = openOrderExposure;
        BuyingPower = buyingPower;
        Currency = currency;
        PostFillCurrent = postFillCurrent;
        OpenOrderCount = openOrderCount;
        PositionQuantities = (positionQuantities ?? new Dictionary<string, decimal>())
            .ToImmutableDictionary(StringComparer.Ordinal);
        if (PositionQuantities.Any(item => string.IsNullOrWhiteSpace(item.Key)))
        {
            throw new ArgumentException("The intent position observation is invalid.");
        }
    }

    public BrokerConnectionObservation Broker { get; }
    public bool KillSwitchClear { get; }
    public bool ReconciliationCurrent { get; }
    public bool CapitalDataCurrent { get; }
    public DateTimeOffset ObservedAt { get; }
    public decimal PositionExposure { get; }
    public decimal OpenOrderExposure { get; }
    public decimal BuyingPower { get; }
    public string Currency { get; }
    public bool PostFillCurrent { get; }
    public int OpenOrderCount { get; }
    public ImmutableDictionary<string, decimal> PositionQuantities { get; }
}

public interface IIntentRiskProbe
{
    ValueTask<IntentRiskObservation> ObserveAsync(
        IntentAccountBinding accountBinding,
        CancellationToken cancellationToken);
}

public interface IPaperReviewIntentPersistence
{
    ValueTask CreateAsync(
        RiskEvaluation riskEvaluation,
        ExecutionIntent intent,
        ExposureReservation? reservation,
        ReconciliationId reconciliationId,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<ExecutionIntent?> FindIntentAsync(
        ExecutionIntentId intentId,
        CancellationToken cancellationToken);

    ValueTask<RiskEvaluation?> FindRiskEvaluationAsync(
        RiskEvaluationId riskEvaluationId,
        CancellationToken cancellationToken);

    ValueTask RecordReviewAsync(
        ExecutionIntent reviewedIntent,
        ExecutionIntentState expectedState,
        ReviewApproval review,
        ExposureReservation? reservation,
        ReconciliationId? reconciliationId,
        CancellationToken cancellationToken);

    ValueTask<ReviewApproval?> FindReviewAsync(
        ExecutionIntentId intentId,
        CancellationToken cancellationToken);

    ValueTask<ReconciliationId?> FindReviewReconciliationAsync(
        ExecutionIntentId intentId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<ReconciliationId?>(null);
}

public interface IPaperReviewExpiryPersistence
{
    ValueTask<int> ExpireDueAsync(
        DateTimeOffset at,
        OperatorId actorId,
        CancellationToken cancellationToken);
}

public sealed record PaperReviewIntentOutcome(
    RiskEvaluation RiskEvaluation,
    ExecutionIntent Intent);

public sealed class PaperReviewIntentException(string message) : InvalidOperationException(message);

public sealed class PaperReviewIntentConcurrencyException()
    : InvalidOperationException("The Execution intent changed before this operation completed.");
