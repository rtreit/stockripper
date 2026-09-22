using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Execution;

public enum KillSwitchScopeType
{
    SYSTEM,
    CONNECTION,
    ACCOUNT,
    GOAL
}

public enum KillSwitchState
{
    CLEAR,
    TRIGGERED,
    RECOVERY_REQUIRED
}

public sealed record KillSwitch
{
    private KillSwitch(
        KillSwitchId id,
        KillSwitchScopeType scopeType,
        string scopeIdentifier,
        KillSwitchState state,
        int revision,
        DateTimeOffset configuredAt,
        DateTimeOffset? triggeredAt,
        string? triggerReason,
        ReconciliationId? recoveryReconciliationId,
        DateTimeOffset? recoveryRequiredAt,
        DateTimeOffset? clearedAt)
    {
        Id = id;
        ScopeType = scopeType;
        ScopeIdentifier = scopeIdentifier;
        State = state;
        Revision = revision;
        ConfiguredAt = configuredAt;
        TriggeredAt = triggeredAt;
        TriggerReason = triggerReason;
        RecoveryReconciliationId = recoveryReconciliationId;
        RecoveryRequiredAt = recoveryRequiredAt;
        ClearedAt = clearedAt;
    }

    public KillSwitchId Id { get; }
    public KillSwitchScopeType ScopeType { get; }
    public string ScopeIdentifier { get; }
    public KillSwitchState State { get; }
    public int Revision { get; }
    public DateTimeOffset ConfiguredAt { get; }
    public DateTimeOffset? TriggeredAt { get; }
    public string? TriggerReason { get; }
    public ReconciliationId? RecoveryReconciliationId { get; }
    public DateTimeOffset? RecoveryRequiredAt { get; }
    public DateTimeOffset? ClearedAt { get; }

    public static KillSwitch Configure(
        KillSwitchId id,
        KillSwitchScopeType scopeType,
        string scopeIdentifier,
        DateTimeOffset configuredAt) => new(
            id,
            scopeType,
            RequireText(scopeIdentifier),
            KillSwitchState.CLEAR,
            0,
            UtcTimestamp.Require(configuredAt, nameof(configuredAt)),
            null,
            null,
            null,
            null,
            null);

    public KillSwitch Trigger(DateTimeOffset at, string reason)
    {
        if (State == KillSwitchState.TRIGGERED)
        {
            throw new DomainRuleViolationException("The kill switch is already triggered.");
        }

        return new KillSwitch(
            Id,
            ScopeType,
            ScopeIdentifier,
            KillSwitchState.TRIGGERED,
            checked(Revision + 1),
            ConfiguredAt,
            UtcTimestamp.Require(at, nameof(at)),
            RequireText(reason),
            null,
            null,
            null);
    }

    public KillSwitch RequireRecovery(ReconciliationRecord reconciliation, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        var changedAt = UtcTimestamp.Require(at, nameof(at));
        if (State != KillSwitchState.TRIGGERED
            || TriggeredAt is null
            || reconciliation.Result != ReconciliationResult.CURRENT
            || reconciliation.CompletedAt < TriggeredAt
            || reconciliation.CompletedAt > changedAt)
        {
            throw new DomainRuleViolationException(
                "A current reconciliation after the trigger is required for kill-switch recovery.");
        }

        return new KillSwitch(
            Id,
            ScopeType,
            ScopeIdentifier,
            KillSwitchState.RECOVERY_REQUIRED,
            checked(Revision + 1),
            ConfiguredAt,
            TriggeredAt,
            TriggerReason,
            reconciliation.Id,
            changedAt,
            null);
    }

    public KillSwitch Clear(DateTimeOffset at)
    {
        var changedAt = UtcTimestamp.Require(at, nameof(at));
        if (State != KillSwitchState.RECOVERY_REQUIRED
            || RecoveryReconciliationId is null
            || RecoveryRequiredAt is null
            || changedAt < RecoveryRequiredAt)
        {
            throw new DomainRuleViolationException(
                "The kill switch needs reconciled recovery evidence before it can clear.");
        }

        return new KillSwitch(
            Id,
            ScopeType,
            ScopeIdentifier,
            KillSwitchState.CLEAR,
            checked(Revision + 1),
            ConfiguredAt,
            TriggeredAt,
            TriggerReason,
            RecoveryReconciliationId,
            RecoveryRequiredAt,
            changedAt);
    }

    public static KillSwitch Restore(
        KillSwitchId id,
        KillSwitchScopeType scopeType,
        string scopeIdentifier,
        KillSwitchState state,
        int revision,
        DateTimeOffset configuredAt,
        DateTimeOffset? triggeredAt,
        string? triggerReason,
        ReconciliationId? recoveryReconciliationId,
        DateTimeOffset? recoveryRequiredAt,
        DateTimeOffset? clearedAt)
    {
        var restored = new KillSwitch(
            id,
            scopeType,
            RequireText(scopeIdentifier),
            state,
            revision,
            UtcTimestamp.Require(configuredAt, nameof(configuredAt)),
            Normalize(triggeredAt, nameof(triggeredAt)),
            triggerReason,
            recoveryReconciliationId,
            Normalize(recoveryRequiredAt, nameof(recoveryRequiredAt)),
            Normalize(clearedAt, nameof(clearedAt)));
        if (revision < 0
            || (state == KillSwitchState.CLEAR && revision == 0
                && (triggeredAt is not null || triggerReason is not null
                    || recoveryReconciliationId is not null || recoveryRequiredAt is not null
                    || clearedAt is not null))
            || (state == KillSwitchState.CLEAR && revision > 0
                && (triggeredAt is null || string.IsNullOrWhiteSpace(triggerReason)
                    || recoveryReconciliationId is null || recoveryRequiredAt is null
                    || clearedAt is null))
            || (state == KillSwitchState.TRIGGERED
                && (triggeredAt is null || string.IsNullOrWhiteSpace(triggerReason)))
            || (state == KillSwitchState.RECOVERY_REQUIRED
                && (triggeredAt is null || recoveryReconciliationId is null || recoveryRequiredAt is null)))
        {
            throw new InvalidDataException("The stored kill-switch state is invalid.");
        }

        return restored;
    }

    private static DateTimeOffset? Normalize(DateTimeOffset? value, string name) => value is null
        ? null
        : UtcTimestamp.Require(value.Value, name);

    private static string RequireText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException("Kill-switch text is invalid.");
        }

        return value;
    }
}

public enum ReconciliationResult
{
    CURRENT,
    DIFFERENCE_LOCKED,
    FAILED
}

public sealed record ReconciliationRecord
{
    private const string DigestSchema = "stockripper-reconciliation-v2";

    private ReconciliationRecord(
        ReconciliationId id,
        GoalVersionId goalVersionId,
        IntentAccountBinding accountBinding,
        VersionReference reconciliationPolicyVersion,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        DateTimeOffset observedAt,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency,
        string comparedOrderRange,
        string comparedFillRange,
        string comparedPositionSnapshot,
        ImmutableArray<string> differences,
        ImmutableArray<string> corrections,
        string? operatorAction,
        ReconciliationResult result,
        string contentDigest)
    {
        Id = id;
        GoalVersionId = goalVersionId;
        AccountBinding = accountBinding;
        ReconciliationPolicyVersion = reconciliationPolicyVersion;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        ObservedAt = observedAt;
        PositionExposure = positionExposure;
        OpenOrderExposure = openOrderExposure;
        BuyingPower = buyingPower;
        Currency = currency;
        ComparedOrderRange = comparedOrderRange;
        ComparedFillRange = comparedFillRange;
        ComparedPositionSnapshot = comparedPositionSnapshot;
        Differences = differences;
        Corrections = corrections;
        OperatorAction = operatorAction;
        Result = result;
        ContentDigest = contentDigest;
    }

    public ReconciliationId Id { get; }
    public GoalVersionId GoalVersionId { get; }
    public IntentAccountBinding AccountBinding { get; }
    public VersionReference ReconciliationPolicyVersion { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset CompletedAt { get; }
    public DateTimeOffset ObservedAt { get; }
    public decimal PositionExposure { get; }
    public decimal OpenOrderExposure { get; }
    public decimal BuyingPower { get; }
    public string Currency { get; }
    public string ComparedOrderRange { get; }
    public string ComparedFillRange { get; }
    public string ComparedPositionSnapshot { get; }
    public ImmutableArray<string> Differences { get; }
    public ImmutableArray<string> Corrections { get; }
    public string? OperatorAction { get; }
    public ReconciliationResult Result { get; }
    public string ContentDigest { get; }

    public static ReconciliationRecord Create(
        ReconciliationId id,
        GoalVersionId goalVersionId,
        IntentAccountBinding accountBinding,
        VersionReference reconciliationPolicyVersion,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        DateTimeOffset observedAt,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency,
        string comparedOrderRange,
        string comparedFillRange,
        string comparedPositionSnapshot,
        IEnumerable<string> differences,
        IEnumerable<string> corrections,
        string? operatorAction,
        bool observationSucceeded)
    {
        ArgumentNullException.ThrowIfNull(accountBinding);
        ArgumentNullException.ThrowIfNull(reconciliationPolicyVersion);
        var started = UtcTimestamp.Require(startedAt, nameof(startedAt));
        var completed = UtcTimestamp.Require(completedAt, nameof(completedAt));
        var observed = UtcTimestamp.Require(observedAt, nameof(observedAt));
        if (completed < started || observed > completed)
        {
            throw new DomainRuleViolationException("Reconciliation times are invalid.");
        }

        ValidateMoney(positionExposure, openOrderExposure, buyingPower, currency);
        var orderRange = RequireEvidenceReference(comparedOrderRange, "orders");
        var fillRange = RequireEvidenceReference(comparedFillRange, "fills");
        var positionSnapshot = RequireEvidenceReference(
            comparedPositionSnapshot, "positions");
        var checkedDifferences = NormalizeText(differences);
        var checkedCorrections = NormalizeText(corrections);
        var result = !observationSucceeded
            ? ReconciliationResult.FAILED
            : checkedDifferences.IsEmpty
                ? ReconciliationResult.CURRENT
                : ReconciliationResult.DIFFERENCE_LOCKED;
        var action = string.IsNullOrWhiteSpace(operatorAction)
            ? null
            : operatorAction.Trim();
        if ((result == ReconciliationResult.CURRENT) != (action is null))
        {
            throw new DomainRuleViolationException(
                "Only a noncurrent reconciliation requires Operator action.");
        }
        var digest = ComputeDigest(
            id,
            goalVersionId,
            accountBinding,
            reconciliationPolicyVersion,
            started,
            completed,
            observed,
            positionExposure,
            openOrderExposure,
            buyingPower,
            currency,
            orderRange,
            fillRange,
            positionSnapshot,
            checkedDifferences,
            checkedCorrections,
            action,
            result);
        return new ReconciliationRecord(
            id,
            goalVersionId,
            accountBinding,
            reconciliationPolicyVersion,
            started,
            completed,
            observed,
            positionExposure,
            openOrderExposure,
            buyingPower,
            currency,
            orderRange,
            fillRange,
            positionSnapshot,
            checkedDifferences,
            checkedCorrections,
            action,
            result,
            digest);
    }

    public static ReconciliationRecord Restore(
        ReconciliationId id,
        GoalVersionId goalVersionId,
        IntentAccountBinding accountBinding,
        VersionReference reconciliationPolicyVersion,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        DateTimeOffset observedAt,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency,
        string comparedOrderRange,
        string comparedFillRange,
        string comparedPositionSnapshot,
        IEnumerable<string> differences,
        IEnumerable<string> corrections,
        string? operatorAction,
        ReconciliationResult result,
        string contentDigest)
    {
        var restored = Create(
            id,
            goalVersionId,
            accountBinding,
            reconciliationPolicyVersion,
            startedAt,
            completedAt,
            observedAt,
            positionExposure,
            openOrderExposure,
            buyingPower,
            currency,
            comparedOrderRange,
            comparedFillRange,
            comparedPositionSnapshot,
            differences,
            corrections,
            operatorAction,
            result != ReconciliationResult.FAILED);
        if (restored.Result != result
            || !string.Equals(restored.ContentDigest, contentDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored reconciliation record is invalid.");
        }

        return restored;
    }

    public bool HasValidContentDigest() => string.Equals(
        ContentDigest,
        ComputeDigest(
            Id,
            GoalVersionId,
            AccountBinding,
            ReconciliationPolicyVersion,
            StartedAt,
            CompletedAt,
            ObservedAt,
            PositionExposure,
            OpenOrderExposure,
            BuyingPower,
            Currency,
            ComparedOrderRange,
            ComparedFillRange,
            ComparedPositionSnapshot,
            Differences,
            Corrections,
            OperatorAction,
            Result),
        StringComparison.Ordinal);

    private static ImmutableArray<string> NormalizeText(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Select(value =>
            {
                if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
                {
                    throw new DomainRuleViolationException("Reconciliation detail is invalid.");
                }

                return value;
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static string RequireEvidenceReference(string value, string kind)
    {
        var prefix = $"{kind}:sha256:";
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Length != prefix.Length + 64
            || value[prefix.Length..].Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new DomainRuleViolationException(
                "The reconciliation evidence reference is invalid.");
        }

        return value;
    }

    private static void ValidateMoney(
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency)
    {
        if (positionExposure < 0m || openOrderExposure < 0m || buyingPower < 0m
            || string.IsNullOrWhiteSpace(currency) || currency.Length != 3
            || currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new DomainRuleViolationException("Reconciliation money values are invalid.");
        }
    }

    private static string ComputeDigest(
        ReconciliationId id,
        GoalVersionId goalVersionId,
        IntentAccountBinding binding,
        VersionReference policy,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        DateTimeOffset observedAt,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal buyingPower,
        string currency,
        string comparedOrderRange,
        string comparedFillRange,
        string comparedPositionSnapshot,
        IEnumerable<string> differences,
        IEnumerable<string> corrections,
        string? operatorAction,
        ReconciliationResult result)
    {
        var value = new
        {
            Schema = DigestSchema,
            Id = id.Value,
            GoalVersionId = goalVersionId.Value,
            BrokerConnectionVersion = VersionValue(binding.BrokerConnectionVersion),
            Environment = binding.Environment.ToString(),
            binding.EndpointIdentity,
            binding.AccountIdentifier,
            ReconciliationPolicyVersion = VersionValue(policy),
            StartedAt = startedAt.ToString("O"),
            CompletedAt = completedAt.ToString("O"),
            ObservedAt = observedAt.ToString("O"),
            PositionExposure = DecimalValue(positionExposure),
            OpenOrderExposure = DecimalValue(openOrderExposure),
            BuyingPower = DecimalValue(buyingPower),
            Currency = currency,
            ComparedOrderRange = comparedOrderRange,
            ComparedFillRange = comparedFillRange,
            ComparedPositionSnapshot = comparedPositionSnapshot,
            Differences = differences.Order(StringComparer.Ordinal),
            Corrections = corrections.Order(StringComparer.Ordinal),
            OperatorAction = operatorAction,
            Result = result.ToString()
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    private static object VersionValue(VersionReference version) => new
    {
        version.Identifier,
        version.Version,
        version.Digest
    };

    private static string DecimalValue(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
}

public enum ExposureReservationState
{
    RESERVED,
    RELEASED
}

public sealed record ExposureReservation
{
    private const string DigestSchema = "stockripper-exposure-reservation-v1";

    private ExposureReservation(
        ExposureReservationId id,
        ExecutionIntentId intentId,
        GoalVersionId goalVersionId,
        AccountAllocationEntryId allocationEntryId,
        IntentAccountBinding accountBinding,
        decimal amount,
        decimal remainingAmount,
        string currency,
        DateTimeOffset createdAt,
        ExposureReservationState state,
        DateTimeOffset? releasedAt,
        string? releaseReason,
        int revision,
        string contentDigest)
    {
        Id = id;
        IntentId = intentId;
        GoalVersionId = goalVersionId;
        AllocationEntryId = allocationEntryId;
        AccountBinding = accountBinding;
        Amount = amount;
        RemainingAmount = remainingAmount;
        Currency = currency;
        CreatedAt = createdAt;
        State = state;
        ReleasedAt = releasedAt;
        ReleaseReason = releaseReason;
        Revision = revision;
        ContentDigest = contentDigest;
    }

    public ExposureReservationId Id { get; }
    public ExecutionIntentId IntentId { get; }
    public GoalVersionId GoalVersionId { get; }
    public AccountAllocationEntryId AllocationEntryId { get; }
    public IntentAccountBinding AccountBinding { get; }
    public decimal Amount { get; }
    public decimal RemainingAmount { get; }
    public string Currency { get; }
    public DateTimeOffset CreatedAt { get; }
    public ExposureReservationState State { get; }
    public DateTimeOffset? ReleasedAt { get; }
    public string? ReleaseReason { get; }
    public int Revision { get; }
    public string ContentDigest { get; }

    public static ExposureReservation Reserve(
        ExposureReservationId id,
        ExecutionIntent intent,
        AccountAllocationEntryId allocationEntryId,
        string currency,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(intent);
        decimal amount;
        try
        {
            amount = checked(intent.Quantity * intent.LimitPrice);
        }
        catch (OverflowException)
        {
            throw new DomainRuleViolationException("The reservation amount is invalid.");
        }

        Validate(amount, currency);
        var time = UtcTimestamp.Require(createdAt, nameof(createdAt));
        var digest = ComputeDigest(
            id,
            intent.Id,
            intent.GoalVersionId,
            allocationEntryId,
            intent.AccountBinding,
            amount,
            currency,
            time);
        return new ExposureReservation(
            id,
            intent.Id,
            intent.GoalVersionId,
            allocationEntryId,
            intent.AccountBinding,
            amount,
            amount,
            currency,
            time,
            ExposureReservationState.RESERVED,
            null,
            null,
            0,
            digest);
    }

    public ExposureReservation Release(DateTimeOffset at, string reason)
    {
        var time = UtcTimestamp.Require(at, nameof(at));
        if (State != ExposureReservationState.RESERVED || time < CreatedAt)
        {
            throw new DomainRuleViolationException("Only an active exposure reservation can be released.");
        }

        if (string.IsNullOrWhiteSpace(reason) || reason != reason.Trim())
        {
            throw new DomainRuleViolationException("The reservation release reason is invalid.");
        }

        return new ExposureReservation(
            Id,
            IntentId,
            GoalVersionId,
            AllocationEntryId,
            AccountBinding,
            Amount,
            0m,
            Currency,
            CreatedAt,
            ExposureReservationState.RELEASED,
            time,
            reason,
            checked(Revision + 1),
            ContentDigest);
    }

    public ExposureReservation AdjustRemaining(decimal remainingAmount)
    {
        if (State != ExposureReservationState.RESERVED
            || remainingAmount <= 0m || remainingAmount >= RemainingAmount)
        {
            throw new DomainRuleViolationException("The reservation adjustment is invalid.");
        }

        return new ExposureReservation(Id, IntentId, GoalVersionId, AllocationEntryId,
            AccountBinding, Amount, remainingAmount, Currency, CreatedAt,
            ExposureReservationState.RESERVED, null, null, checked(Revision + 1), ContentDigest);
    }

    public static ExposureReservation Restore(
        ExposureReservationId id,
        ExecutionIntentId intentId,
        GoalVersionId goalVersionId,
        AccountAllocationEntryId allocationEntryId,
        IntentAccountBinding accountBinding,
        decimal amount,
        decimal remainingAmount,
        string currency,
        DateTimeOffset createdAt,
        ExposureReservationState state,
        DateTimeOffset? releasedAt,
        string? releaseReason,
        int revision,
        string contentDigest)
    {
        Validate(amount, currency);
        if (remainingAmount < 0m || remainingAmount > amount)
        {
            throw new InvalidDataException("The stored reservation remainder is invalid.");
        }
        var created = UtcTimestamp.Require(createdAt, nameof(createdAt));
        var restored = new ExposureReservation(
            id,
            intentId,
            goalVersionId,
            allocationEntryId,
            accountBinding,
            amount,
            remainingAmount,
            currency,
            created,
            state,
            releasedAt is null ? null : UtcTimestamp.Require(releasedAt.Value, nameof(releasedAt)),
            releaseReason,
            revision,
            contentDigest);
        if (revision < 0
            || (state == ExposureReservationState.RESERVED
                && (remainingAmount <= 0m || releasedAt is not null || releaseReason is not null))
            || (state == ExposureReservationState.RELEASED
                && (remainingAmount != 0m || releasedAt is null
                    || string.IsNullOrWhiteSpace(releaseReason) || revision == 0))
            || !restored.HasValidContentDigest())
        {
            throw new InvalidDataException("The stored exposure reservation is invalid.");
        }

        return restored;
    }

    public bool HasValidContentDigest() => string.Equals(
        ContentDigest,
        ComputeDigest(
            Id,
            IntentId,
            GoalVersionId,
            AllocationEntryId,
            AccountBinding,
            Amount,
            Currency,
            CreatedAt),
        StringComparison.Ordinal);

    private static void Validate(decimal amount, string currency)
    {
        if (amount <= 0m || string.IsNullOrWhiteSpace(currency) || currency.Length != 3
            || currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new DomainRuleViolationException("The exposure reservation amount is invalid.");
        }
    }

    private static string ComputeDigest(
        ExposureReservationId id,
        ExecutionIntentId intentId,
        GoalVersionId goalVersionId,
        AccountAllocationEntryId allocationEntryId,
        IntentAccountBinding binding,
        decimal amount,
        string currency,
        DateTimeOffset createdAt)
    {
        var value = new
        {
            Schema = DigestSchema,
            Id = id.Value,
            IntentId = intentId.Value,
            GoalVersionId = goalVersionId.Value,
            AllocationEntryId = allocationEntryId.Value,
            BrokerConnectionVersion = new
            {
                binding.BrokerConnectionVersion.Identifier,
                binding.BrokerConnectionVersion.Version,
                binding.BrokerConnectionVersion.Digest
            },
            Environment = binding.Environment.ToString(),
            binding.EndpointIdentity,
            binding.AccountIdentifier,
            Amount = amount.ToString("G29", CultureInfo.InvariantCulture),
            Currency = currency,
            CreatedAt = createdAt.ToString("O")
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }
}
