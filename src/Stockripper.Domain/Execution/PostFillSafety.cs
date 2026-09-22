using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Execution;

public sealed record PositionSnapshotBatch
{
    private PositionSnapshotBatch(PositionSnapshotBatchId id, IntentAccountBinding accountBinding,
        string observationReference, int positionCount, DateTimeOffset brokerObservationTime,
        DateTimeOffset receivedAt, string sourcePayloadReference, string contentDigest)
    {
        Id = id;
        AccountBinding = accountBinding;
        ObservationReference = observationReference;
        PositionCount = positionCount;
        BrokerObservationTime = brokerObservationTime;
        ReceivedAt = receivedAt;
        SourcePayloadReference = sourcePayloadReference;
        ContentDigest = contentDigest;
    }

    public PositionSnapshotBatchId Id { get; }
    public IntentAccountBinding AccountBinding { get; }
    public string ObservationReference { get; }
    public int PositionCount { get; }
    public DateTimeOffset BrokerObservationTime { get; }
    public DateTimeOffset ReceivedAt { get; }
    public string SourcePayloadReference { get; }
    public string ContentDigest { get; }

    public static PositionSnapshotBatch Create(PositionSnapshotBatchId id,
        IntentAccountBinding accountBinding, string observationReference, int positionCount,
        DateTimeOffset brokerObservationTime, DateTimeOffset receivedAt,
        string sourcePayloadReference)
    {
        ArgumentNullException.ThrowIfNull(accountBinding);
        var observed = UtcTimestamp.Require(brokerObservationTime, nameof(brokerObservationTime));
        var received = UtcTimestamp.Require(receivedAt, nameof(receivedAt));
        if (accountBinding.Environment != BrokerEnvironment.PAPER
            || string.IsNullOrWhiteSpace(observationReference)
            || observationReference != observationReference.Trim()
            || positionCount < 0
            || string.IsNullOrWhiteSpace(sourcePayloadReference)
            || sourcePayloadReference != sourcePayloadReference.Trim())
        {
            throw new DomainRuleViolationException("The Position snapshot batch is invalid.");
        }

        var value = new
        {
            Schema = "stockripper-position-snapshot-batch-v1",
            Id = id.Value,
            Binding = BindingValue(accountBinding),
            ObservationReference = observationReference,
            PositionCount = positionCount,
            BrokerObservationTime = observed.ToString("O"),
            ReceivedAt = received.ToString("O"),
            SourcePayloadReference = sourcePayloadReference
        };
        return new(id, accountBinding, observationReference, positionCount, observed, received,
            sourcePayloadReference, Digest(value));
    }

    internal static object BindingValue(IntentAccountBinding binding) => new
    {
        Connection = new
        {
            binding.BrokerConnectionVersion.Identifier,
            binding.BrokerConnectionVersion.Version,
            binding.BrokerConnectionVersion.Digest
        },
        Environment = binding.Environment.ToString(),
        binding.EndpointIdentity,
        binding.AccountIdentifier
    };

    internal static string DecimalValue(decimal value) =>
        value.ToString("G29", CultureInfo.InvariantCulture);

    internal static string Digest(object value) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}

public sealed record PositionSnapshot
{
    private PositionSnapshot(PositionSnapshotId id, PositionSnapshotBatchId batchId,
        IntentAccountBinding accountBinding, string instrumentIdentifier, decimal quantity,
        decimal? averageEntryPrice, decimal marketValue, string currency, string contentDigest)
    {
        Id = id;
        BatchId = batchId;
        AccountBinding = accountBinding;
        InstrumentIdentifier = instrumentIdentifier;
        Quantity = quantity;
        AverageEntryPrice = averageEntryPrice;
        MarketValue = marketValue;
        Currency = currency;
        ContentDigest = contentDigest;
    }

    public PositionSnapshotId Id { get; }
    public PositionSnapshotBatchId BatchId { get; }
    public IntentAccountBinding AccountBinding { get; }
    public string InstrumentIdentifier { get; }
    public decimal Quantity { get; }
    public decimal? AverageEntryPrice { get; }
    public decimal MarketValue { get; }
    public string Currency { get; }
    public string ContentDigest { get; }

    public static PositionSnapshot Create(PositionSnapshotId id, PositionSnapshotBatch batch,
        IntentAccountBinding accountBinding, string instrumentIdentifier, decimal quantity,
        decimal? averageEntryPrice, decimal marketValue, string currency)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(accountBinding);
        if (accountBinding != batch.AccountBinding
            || string.IsNullOrWhiteSpace(instrumentIdentifier)
            || instrumentIdentifier != instrumentIdentifier.Trim()
            || quantity < 0m || decimal.Truncate(quantity) != quantity
            || marketValue < 0m
            || (quantity == 0m && (averageEntryPrice is not null || marketValue != 0m))
            || (quantity > 0m && averageEntryPrice is not > 0m)
            || !IsCurrency(currency))
        {
            throw new DomainRuleViolationException("The Position snapshot is invalid.");
        }

        var value = new
        {
            Schema = "stockripper-position-snapshot-v1",
            Id = id.Value,
            BatchId = batch.Id.Value,
            Binding = PositionSnapshotBatch.BindingValue(accountBinding),
            InstrumentIdentifier = instrumentIdentifier,
            Quantity = PositionSnapshotBatch.DecimalValue(quantity),
            AverageEntryPrice = averageEntryPrice is null
                ? null
                : PositionSnapshotBatch.DecimalValue(averageEntryPrice.Value),
            MarketValue = PositionSnapshotBatch.DecimalValue(marketValue),
            Currency = currency
        };
        return new(id, batch.Id, accountBinding, instrumentIdentifier, quantity,
            averageEntryPrice, marketValue, currency, PositionSnapshotBatch.Digest(value));
    }

    private static bool IsCurrency(string value) => !string.IsNullOrWhiteSpace(value)
        && value.Length == 3 && value.All(character => character is >= 'A' and <= 'Z');
}

public enum PostFillEvaluationResult
{
    PASSED,
    VIOLATION_LOCKED
}

public sealed record PostFillEvaluation
{
    private PostFillEvaluation(PostFillEvaluationId id, FillId fillId, OrderId orderId,
        GoalVersionId goalVersionId, SessionId sessionId, VersionReference riskPolicyVersion,
        AccountAllocationEntryId allocationEntryId, PositionSnapshotBatchId positionBatchId,
        decimal positionExposure, decimal openOrderExposure, decimal allocationAmount,
        decimal largestInstrumentExposure, decimal maximumPositionExposure,
        decimal maximumPortfolioExposure, decimal maximumConcentration,
        string currency, bool positionEvidenceCurrent, PostFillEvaluationResult result,
        DateTimeOffset evaluatedAt, string contentDigest)
    {
        Id = id;
        FillId = fillId;
        OrderId = orderId;
        GoalVersionId = goalVersionId;
        SessionId = sessionId;
        RiskPolicyVersion = riskPolicyVersion;
        AllocationEntryId = allocationEntryId;
        PositionBatchId = positionBatchId;
        PositionExposure = positionExposure;
        OpenOrderExposure = openOrderExposure;
        AllocationAmount = allocationAmount;
        LargestInstrumentExposure = largestInstrumentExposure;
        MaximumPositionExposure = maximumPositionExposure;
        MaximumPortfolioExposure = maximumPortfolioExposure;
        MaximumConcentration = maximumConcentration;
        Currency = currency;
        PositionEvidenceCurrent = positionEvidenceCurrent;
        Result = result;
        EvaluatedAt = evaluatedAt;
        ContentDigest = contentDigest;
    }

    public PostFillEvaluationId Id { get; }
    public FillId FillId { get; }
    public OrderId OrderId { get; }
    public GoalVersionId GoalVersionId { get; }
    public SessionId SessionId { get; }
    public VersionReference RiskPolicyVersion { get; }
    public AccountAllocationEntryId AllocationEntryId { get; }
    public PositionSnapshotBatchId PositionBatchId { get; }
    public decimal PositionExposure { get; }
    public decimal OpenOrderExposure { get; }
    public decimal AllocationAmount { get; }
    public decimal TotalExposure => PositionExposure + OpenOrderExposure;
    public decimal LargestInstrumentExposure { get; }
    public decimal MaximumPositionExposure { get; }
    public decimal MaximumPortfolioExposure { get; }
    public decimal MaximumConcentration { get; }
    public decimal ObservedConcentration => TotalExposure == 0m
        ? 0m
        : LargestInstrumentExposure / TotalExposure;
    public bool AllocationLimitPassed => TotalExposure <= AllocationAmount;
    public bool PositionLimitPassed => LargestInstrumentExposure <= MaximumPositionExposure;
    public bool PortfolioLimitPassed => TotalExposure <= MaximumPortfolioExposure;
    public bool ConcentrationLimitPassed => ObservedConcentration <= MaximumConcentration;
    public string Currency { get; }
    public bool PositionEvidenceCurrent { get; }
    public PostFillEvaluationResult Result { get; }
    public DateTimeOffset EvaluatedAt { get; }
    public string ContentDigest { get; }

    public static PostFillEvaluation Create(PostFillEvaluationId id, FillId fillId,
        OrderId orderId, GoalVersionId goalVersionId, SessionId sessionId,
        VersionReference riskPolicyVersion, AccountAllocationEntryId allocationEntryId,
        PositionSnapshotBatch batch, decimal positionExposure, decimal openOrderExposure,
        decimal allocationAmount, decimal largestInstrumentExposure,
        decimal maximumPositionExposure, decimal maximumPortfolioExposure,
        decimal maximumConcentration, string currency, bool positionEvidenceCurrent,
        DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(riskPolicyVersion);
        ArgumentNullException.ThrowIfNull(batch);
        var time = UtcTimestamp.Require(evaluatedAt, nameof(evaluatedAt));
        decimal total;
        try
        {
            total = checked(positionExposure + openOrderExposure);
        }
        catch (OverflowException)
        {
            throw new DomainRuleViolationException("The post-Fill exposure is invalid.");
        }

        if (positionExposure < 0m || openOrderExposure < 0m || allocationAmount <= 0m
            || largestInstrumentExposure < 0m
            || largestInstrumentExposure > total
            || maximumPositionExposure <= 0m
            || maximumPortfolioExposure <= 0m
            || maximumPositionExposure > maximumPortfolioExposure
            || maximumConcentration is <= 0m or > 1m
            || string.IsNullOrWhiteSpace(currency) || currency.Length != 3
            || currency.Any(character => character is < 'A' or > 'Z')
            || time < batch.ReceivedAt)
        {
            throw new DomainRuleViolationException("The post-Fill evaluation is invalid.");
        }

        var concentration = total == 0m ? 0m : largestInstrumentExposure / total;
        var result = positionEvidenceCurrent
            && total <= allocationAmount
            && largestInstrumentExposure <= maximumPositionExposure
            && total <= maximumPortfolioExposure
            && concentration <= maximumConcentration
            ? PostFillEvaluationResult.PASSED
            : PostFillEvaluationResult.VIOLATION_LOCKED;
        var value = new
        {
            Schema = "stockripper-post-fill-evaluation-v2",
            Id = id.Value,
            FillId = fillId.Value,
            OrderId = orderId.Value,
            GoalVersionId = goalVersionId.Value,
            SessionId = sessionId.Value,
            RiskPolicyVersion = new
            {
                riskPolicyVersion.Identifier,
                riskPolicyVersion.Version,
                riskPolicyVersion.Digest
            },
            AllocationEntryId = allocationEntryId.Value,
            PositionBatchId = batch.Id.Value,
            PositionExposure = PositionSnapshotBatch.DecimalValue(positionExposure),
            OpenOrderExposure = PositionSnapshotBatch.DecimalValue(openOrderExposure),
            AllocationAmount = PositionSnapshotBatch.DecimalValue(allocationAmount),
            LargestInstrumentExposure = PositionSnapshotBatch.DecimalValue(largestInstrumentExposure),
            MaximumPositionExposure = PositionSnapshotBatch.DecimalValue(maximumPositionExposure),
            MaximumPortfolioExposure = PositionSnapshotBatch.DecimalValue(maximumPortfolioExposure),
            MaximumConcentration = PositionSnapshotBatch.DecimalValue(maximumConcentration),
            ObservedConcentration = PositionSnapshotBatch.DecimalValue(concentration),
            Currency = currency,
            PositionEvidenceCurrent = positionEvidenceCurrent,
            Result = result.ToString(),
            EvaluatedAt = time.ToString("O")
        };
        return new(id, fillId, orderId, goalVersionId, sessionId, riskPolicyVersion,
            allocationEntryId, batch.Id, positionExposure, openOrderExposure,
            allocationAmount, largestInstrumentExposure, maximumPositionExposure,
            maximumPortfolioExposure, maximumConcentration, currency,
            positionEvidenceCurrent, result, time,
            PositionSnapshotBatch.Digest(value));
    }

    public static PostFillEvaluation Restore(PostFillEvaluationId id, FillId fillId,
        OrderId orderId, GoalVersionId goalVersionId, SessionId sessionId,
        VersionReference riskPolicyVersion, AccountAllocationEntryId allocationEntryId,
        PositionSnapshotBatch batch, decimal positionExposure, decimal openOrderExposure,
        decimal allocationAmount, decimal largestInstrumentExposure,
        decimal maximumPositionExposure, decimal maximumPortfolioExposure,
        decimal maximumConcentration, string currency, bool positionEvidenceCurrent,
        DateTimeOffset evaluatedAt, string storedContentDigest)
    {
        var current = Create(id, fillId, orderId, goalVersionId, sessionId,
            riskPolicyVersion, allocationEntryId, batch, positionExposure,
            openOrderExposure, allocationAmount, largestInstrumentExposure,
            maximumPositionExposure, maximumPortfolioExposure,
            maximumConcentration, currency, positionEvidenceCurrent, evaluatedAt);
        if (string.Equals(current.ContentDigest, storedContentDigest,
                StringComparison.Ordinal))
        {
            return current;
        }

        var usesMigrationDefaults = largestInstrumentExposure == positionExposure
            && maximumPositionExposure == allocationAmount
            && maximumPortfolioExposure == allocationAmount
            && maximumConcentration == 1m;
        var legacyValue = new
        {
            Schema = "stockripper-post-fill-evaluation-v1",
            Id = id.Value,
            FillId = fillId.Value,
            OrderId = orderId.Value,
            GoalVersionId = goalVersionId.Value,
            SessionId = sessionId.Value,
            RiskPolicyVersion = new
            {
                riskPolicyVersion.Identifier,
                riskPolicyVersion.Version,
                riskPolicyVersion.Digest
            },
            AllocationEntryId = allocationEntryId.Value,
            PositionBatchId = batch.Id.Value,
            PositionExposure = PositionSnapshotBatch.DecimalValue(positionExposure),
            OpenOrderExposure = PositionSnapshotBatch.DecimalValue(openOrderExposure),
            AllocationAmount = PositionSnapshotBatch.DecimalValue(allocationAmount),
            Currency = currency,
            PositionEvidenceCurrent = positionEvidenceCurrent,
            Result = current.Result.ToString(),
            EvaluatedAt = current.EvaluatedAt.ToString("O")
        };
        if (!usesMigrationDefaults
            || !string.Equals(PositionSnapshotBatch.Digest(legacyValue),
                storedContentDigest, StringComparison.Ordinal))
        {
            throw new DomainRuleViolationException(
                "The stored post-Fill evaluation digest is invalid.");
        }

        return new(id, fillId, orderId, goalVersionId, sessionId,
            riskPolicyVersion, allocationEntryId, batch.Id, positionExposure,
            openOrderExposure, allocationAmount, largestInstrumentExposure,
            maximumPositionExposure, maximumPortfolioExposure,
            maximumConcentration, currency, positionEvidenceCurrent,
            current.Result, current.EvaluatedAt, storedContentDigest);
    }
}

public enum PostFillReconciliationState
{
    REQUESTED
}

public enum OperatorNotificationState
{
    PENDING
}

public sealed record PostFillSafetyResponse
{
    private PostFillSafetyResponse(PostFillSafetyResponseId id,
        PostFillEvaluationId evaluationId, GoalVersionId goalVersionId, SessionId sessionId,
        IntentAccountBinding accountBinding, PostFillReconciliationState reconciliationState,
        OperatorNotificationState notificationState, string reason, DateTimeOffset createdAt,
        string contentDigest)
    {
        Id = id;
        EvaluationId = evaluationId;
        GoalVersionId = goalVersionId;
        SessionId = sessionId;
        AccountBinding = accountBinding;
        ReconciliationState = reconciliationState;
        NotificationState = notificationState;
        Reason = reason;
        CreatedAt = createdAt;
        ContentDigest = contentDigest;
    }

    public PostFillSafetyResponseId Id { get; }
    public PostFillEvaluationId EvaluationId { get; }
    public GoalVersionId GoalVersionId { get; }
    public SessionId SessionId { get; }
    public IntentAccountBinding AccountBinding { get; }
    public PostFillReconciliationState ReconciliationState { get; }
    public OperatorNotificationState NotificationState { get; }
    public string Reason { get; }
    public DateTimeOffset CreatedAt { get; }
    public string ContentDigest { get; }

    public static PostFillSafetyResponse Create(PostFillSafetyResponseId id,
        PostFillEvaluation evaluation, IntentAccountBinding accountBinding, string reason,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(accountBinding);
        var time = UtcTimestamp.Require(createdAt, nameof(createdAt));
        if (evaluation.Result != PostFillEvaluationResult.VIOLATION_LOCKED
            || accountBinding.Environment != BrokerEnvironment.PAPER
            || string.IsNullOrWhiteSpace(reason) || reason != reason.Trim()
            || time < evaluation.EvaluatedAt)
        {
            throw new DomainRuleViolationException("The post-Fill safety response is invalid.");
        }

        var value = new
        {
            Schema = "stockripper-post-fill-safety-response-v1",
            Id = id.Value,
            EvaluationId = evaluation.Id.Value,
            GoalVersionId = evaluation.GoalVersionId.Value,
            SessionId = evaluation.SessionId.Value,
            Binding = PositionSnapshotBatch.BindingValue(accountBinding),
            ReconciliationState = PostFillReconciliationState.REQUESTED.ToString(),
            NotificationState = OperatorNotificationState.PENDING.ToString(),
            Reason = reason,
            CreatedAt = time.ToString("O")
        };
        return new(id, evaluation.Id, evaluation.GoalVersionId, evaluation.SessionId,
            accountBinding, PostFillReconciliationState.REQUESTED,
            OperatorNotificationState.PENDING, reason, time,
            PositionSnapshotBatch.Digest(value));
    }
}
