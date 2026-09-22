using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Execution;

public enum RiskEvaluationResult
{
    ACCEPTED,
    REJECTED
}

public enum RiskCheckCode
{
    AUTHORITY,
    PREFLIGHT,
    RISK_POLICY,
    EXECUTION_SCHEDULE,
    KILL_SWITCH,
    RECONCILIATION,
    ACCOUNT_BINDING,
    INSTRUMENT,
    ORDER_TYPE,
    PRICE_PROTECTION,
    CAPITAL_ALLOCATION,
    BUYING_POWER,
    DATA_FRESHNESS
}

public sealed record RiskCheck
{
    public RiskCheck(RiskCheckCode code, bool passed, string detail)
    {
        if (string.IsNullOrWhiteSpace(detail) || detail != detail.Trim())
        {
            throw new DomainRuleViolationException("The risk check detail is required.");
        }

        Code = code;
        Passed = passed;
        Detail = detail;
    }

    public RiskCheckCode Code { get; }
    public bool Passed { get; }
    public string Detail { get; }
}

public sealed record RiskEvaluation
{
    private const string DigestSchema = "stockripper-risk-evaluation-v1";

    private RiskEvaluation(
        RiskEvaluationId id,
        ExecutionIntentId intentId,
        DecisionId decisionId,
        GoalVersionId goalVersionId,
        SessionId sessionId,
        VersionReference riskPolicyVersion,
        AccountAllocationEntryId allocationEntryId,
        ReconciliationId reconciliationId,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal requestedExposure,
        decimal buyingPower,
        string currency,
        ImmutableArray<RiskCheck> checks,
        RiskEvaluationResult result,
        DateTimeOffset evaluatedAt,
        string contentDigest)
    {
        Id = id;
        IntentId = intentId;
        DecisionId = decisionId;
        GoalVersionId = goalVersionId;
        SessionId = sessionId;
        RiskPolicyVersion = riskPolicyVersion;
        AllocationEntryId = allocationEntryId;
        ReconciliationId = reconciliationId;
        PositionExposure = positionExposure;
        OpenOrderExposure = openOrderExposure;
        RequestedExposure = requestedExposure;
        BuyingPower = buyingPower;
        Currency = currency;
        Checks = checks;
        Result = result;
        EvaluatedAt = evaluatedAt;
        ContentDigest = contentDigest;
    }

    public RiskEvaluationId Id { get; }
    public ExecutionIntentId IntentId { get; }
    public DecisionId DecisionId { get; }
    public GoalVersionId GoalVersionId { get; }
    public SessionId SessionId { get; }
    public VersionReference RiskPolicyVersion { get; }
    public AccountAllocationEntryId AllocationEntryId { get; }
    public ReconciliationId ReconciliationId { get; }
    public decimal PositionExposure { get; }
    public decimal OpenOrderExposure { get; }
    public decimal RequestedExposure { get; }
    public decimal BuyingPower { get; }
    public string Currency { get; }
    public ImmutableArray<RiskCheck> Checks { get; }
    public RiskEvaluationResult Result { get; }
    public DateTimeOffset EvaluatedAt { get; }
    public string ContentDigest { get; }

    public static RiskEvaluation Create(
        RiskEvaluationId id,
        ExecutionIntentId intentId,
        DecisionId decisionId,
        GoalVersionId goalVersionId,
        SessionId sessionId,
        VersionReference riskPolicyVersion,
        AccountAllocationEntryId allocationEntryId,
        ReconciliationId reconciliationId,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal requestedExposure,
        decimal buyingPower,
        string currency,
        IEnumerable<RiskCheck> checks,
        DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(riskPolicyVersion);
        ArgumentNullException.ThrowIfNull(checks);
        ValidateMoney(positionExposure, openOrderExposure, requestedExposure, buyingPower, currency);
        var checkedChecks = checks.ToImmutableArray();
        if (checkedChecks.IsEmpty
            || checkedChecks.Select(check => check.Code).Distinct().Count() != checkedChecks.Length)
        {
            throw new DomainRuleViolationException("Risk checks must contain unique results.");
        }

        var result = checkedChecks.All(check => check.Passed)
            ? RiskEvaluationResult.ACCEPTED
            : RiskEvaluationResult.REJECTED;
        var time = UtcTimestamp.Require(evaluatedAt, nameof(evaluatedAt));
        var digest = ComputeDigest(
            id,
            intentId,
            decisionId,
            goalVersionId,
            sessionId,
            riskPolicyVersion,
            allocationEntryId,
            reconciliationId,
            positionExposure,
            openOrderExposure,
            requestedExposure,
            buyingPower,
            currency,
            checkedChecks,
            result,
            time);
        return new RiskEvaluation(
            id,
            intentId,
            decisionId,
            goalVersionId,
            sessionId,
            riskPolicyVersion,
            allocationEntryId,
            reconciliationId,
            positionExposure,
            openOrderExposure,
            requestedExposure,
            buyingPower,
            currency,
            checkedChecks,
            result,
            time,
            digest);
    }

    public static RiskEvaluation Restore(
        RiskEvaluationId id,
        ExecutionIntentId intentId,
        DecisionId decisionId,
        GoalVersionId goalVersionId,
        SessionId sessionId,
        VersionReference riskPolicyVersion,
        AccountAllocationEntryId allocationEntryId,
        ReconciliationId reconciliationId,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal requestedExposure,
        decimal buyingPower,
        string currency,
        IEnumerable<RiskCheck> checks,
        RiskEvaluationResult result,
        DateTimeOffset evaluatedAt,
        string contentDigest)
    {
        var restored = Create(
            id,
            intentId,
            decisionId,
            goalVersionId,
            sessionId,
            riskPolicyVersion,
            allocationEntryId,
            reconciliationId,
            positionExposure,
            openOrderExposure,
            requestedExposure,
            buyingPower,
            currency,
            checks,
            evaluatedAt);
        if (restored.Result != result
            || !string.Equals(restored.ContentDigest, contentDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored risk evaluation integrity check failed.");
        }

        return restored;
    }

    public bool HasValidContentDigest() => string.Equals(
        ContentDigest,
        ComputeDigest(
            Id,
            IntentId,
            DecisionId,
            GoalVersionId,
            SessionId,
            RiskPolicyVersion,
            AllocationEntryId,
            ReconciliationId,
            PositionExposure,
            OpenOrderExposure,
            RequestedExposure,
            BuyingPower,
            Currency,
            Checks,
            Result,
            EvaluatedAt),
        StringComparison.Ordinal);

    private static void ValidateMoney(
        decimal positionExposure,
        decimal openOrderExposure,
        decimal requestedExposure,
        decimal buyingPower,
        string currency)
    {
        if (positionExposure < 0m || openOrderExposure < 0m || requestedExposure <= 0m || buyingPower < 0m)
        {
            throw new DomainRuleViolationException("Risk exposure values are invalid.");
        }

        if (string.IsNullOrWhiteSpace(currency)
            || currency.Length != 3
            || currency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new DomainRuleViolationException("The risk currency must be a three-letter uppercase code.");
        }
    }

    private static string ComputeDigest(
        RiskEvaluationId id,
        ExecutionIntentId intentId,
        DecisionId decisionId,
        GoalVersionId goalVersionId,
        SessionId sessionId,
        VersionReference riskPolicyVersion,
        AccountAllocationEntryId allocationEntryId,
        ReconciliationId reconciliationId,
        decimal positionExposure,
        decimal openOrderExposure,
        decimal requestedExposure,
        decimal buyingPower,
        string currency,
        ImmutableArray<RiskCheck> checks,
        RiskEvaluationResult result,
        DateTimeOffset evaluatedAt)
    {
        var value = new
        {
            Schema = DigestSchema,
            Id = id.Value,
            IntentId = intentId.Value,
            DecisionId = decisionId.Value,
            GoalVersionId = goalVersionId.Value,
            SessionId = sessionId.Value,
            RiskPolicyVersion = new
            {
                riskPolicyVersion.Identifier,
                riskPolicyVersion.Version,
                riskPolicyVersion.Digest
            },
            AllocationEntryId = allocationEntryId.Value,
            ReconciliationId = reconciliationId.Value,
            PositionExposure = CanonicalDecimal(positionExposure),
            OpenOrderExposure = CanonicalDecimal(openOrderExposure),
            RequestedExposure = CanonicalDecimal(requestedExposure),
            BuyingPower = CanonicalDecimal(buyingPower),
            Currency = currency,
            Checks = checks.Select(check => new
            {
                Code = check.Code.ToString(),
                check.Passed,
                check.Detail
            }),
            Result = result.ToString(),
            EvaluatedAt = evaluatedAt.ToString("O")
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    private static string CanonicalDecimal(decimal value) => value.ToString(
        "G29",
        CultureInfo.InvariantCulture);
}
