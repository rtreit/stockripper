using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Decisions;

public enum DecisionAction
{
    UNKNOWN,
    NO_ACTION,
    BUY,
    SELL
}

public enum DecisionState
{
    PROPOSED,
    GUARD_REJECTED,
    INTENT_CREATED,
    NO_ACTION,
    FAILED
}

public sealed record DecisionGuardResult
{
    public DecisionGuardResult(string code, bool passed, string detail)
    {
        Code = RequireText(code, "The Decision guard code is required.");
        Passed = passed;
        Detail = RequireText(detail, "The Decision guard detail is required.");
    }

    public string Code { get; }

    public bool Passed { get; }

    public string Detail { get; }

    private static string RequireText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException(message);
        }

        return value;
    }
}

public sealed record Decision
{
    private const string DigestSchema = "stockripper-decision-v1";

    private Decision(
        DecisionId id,
        SessionId sessionId,
        GoalVersionId goalVersionId,
        VersionReference strategyVersion,
        DataSnapshotId inputSnapshotId,
        DateTimeOffset decisionTime,
        DecisionAction proposedAction,
        string? instrumentIdentifier,
        decimal? quantity,
        decimal? limitPrice,
        string explanation,
        decimal? confidence,
        ImmutableArray<DecisionGuardResult> guardResults,
        DecisionState state,
        string contentDigest)
    {
        Id = id;
        SessionId = sessionId;
        GoalVersionId = goalVersionId;
        StrategyVersion = strategyVersion;
        InputSnapshotId = inputSnapshotId;
        DecisionTime = decisionTime;
        ProposedAction = proposedAction;
        InstrumentIdentifier = instrumentIdentifier;
        Quantity = quantity;
        LimitPrice = limitPrice;
        Explanation = explanation;
        Confidence = confidence;
        GuardResults = guardResults;
        State = state;
        ContentDigest = contentDigest;
    }

    public DecisionId Id { get; }
    public SessionId SessionId { get; }
    public GoalVersionId GoalVersionId { get; }
    public VersionReference StrategyVersion { get; }
    public DataSnapshotId InputSnapshotId { get; }
    public DateTimeOffset DecisionTime { get; }
    public DecisionAction ProposedAction { get; }
    public string? InstrumentIdentifier { get; }
    public decimal? Quantity { get; }
    public decimal? LimitPrice { get; }
    public string Explanation { get; }
    public decimal? Confidence { get; }
    public ImmutableArray<DecisionGuardResult> GuardResults { get; }
    public DecisionState State { get; }
    public string ContentDigest { get; }

    public static Decision Create(
        DecisionId id,
        SessionId sessionId,
        GoalVersionId goalVersionId,
        VersionReference strategyVersion,
        DataSnapshotId inputSnapshotId,
        DateTimeOffset decisionTime,
        DecisionAction proposedAction,
        string? instrumentIdentifier,
        decimal? quantity,
        decimal? limitPrice,
        string explanation,
        decimal? confidence,
        IEnumerable<DecisionGuardResult> guardResults)
    {
        ArgumentNullException.ThrowIfNull(strategyVersion);
        ArgumentNullException.ThrowIfNull(guardResults);
        var checkedTime = UtcTimestamp.Require(decisionTime, nameof(decisionTime));
        var checkedExplanation = RequireText(explanation, "Decision explanation is required.");
        var checkedInstrument = string.IsNullOrWhiteSpace(instrumentIdentifier)
            ? null
            : instrumentIdentifier.Trim();
        var checkedGuards = guardResults.ToImmutableArray();
        if (checkedGuards.IsEmpty)
        {
            throw new DomainRuleViolationException("A Decision needs deterministic guard results.");
        }

        if (confidence is < 0m or > 1m)
        {
            throw new DomainRuleViolationException("Decision confidence must be from zero through one.");
        }

        var state = checkedGuards.Any(result => !result.Passed)
            ? DecisionState.GUARD_REJECTED
            : proposedAction == DecisionAction.NO_ACTION
                ? DecisionState.NO_ACTION
                : DecisionState.PROPOSED;
        var digest = ComputeDigest(
            id,
            sessionId,
            goalVersionId,
            strategyVersion,
            inputSnapshotId,
            checkedTime,
            proposedAction,
            checkedInstrument,
            quantity,
            limitPrice,
            checkedExplanation,
            confidence,
            checkedGuards,
            state);
        return new Decision(
            id,
            sessionId,
            goalVersionId,
            strategyVersion,
            inputSnapshotId,
            checkedTime,
            proposedAction,
            checkedInstrument,
            quantity,
            limitPrice,
            checkedExplanation,
            confidence,
            checkedGuards,
            state,
            digest);
    }

    public bool HasValidContentDigest() => string.Equals(
        ContentDigest,
        ComputeDigest(
            Id,
            SessionId,
            GoalVersionId,
            StrategyVersion,
            InputSnapshotId,
            DecisionTime,
            ProposedAction,
            InstrumentIdentifier,
            Quantity,
            LimitPrice,
            Explanation,
            Confidence,
            GuardResults,
            State),
        StringComparison.Ordinal);

    private static string ComputeDigest(
        DecisionId id,
        SessionId sessionId,
        GoalVersionId goalVersionId,
        VersionReference strategyVersion,
        DataSnapshotId inputSnapshotId,
        DateTimeOffset decisionTime,
        DecisionAction proposedAction,
        string? instrumentIdentifier,
        decimal? quantity,
        decimal? limitPrice,
        string explanation,
        decimal? confidence,
        ImmutableArray<DecisionGuardResult> guardResults,
        DecisionState state)
    {
        var value = new
        {
            Schema = DigestSchema,
            Id = id.Value,
            SessionId = sessionId.Value,
            GoalVersionId = goalVersionId.Value,
            StrategyVersion = new
            {
                strategyVersion.Identifier,
                strategyVersion.Version,
                strategyVersion.Digest
            },
            InputSnapshotId = inputSnapshotId.Value,
            DecisionTime = decisionTime.ToString("O"),
            ProposedAction = proposedAction.ToString(),
            InstrumentIdentifier = instrumentIdentifier,
            Quantity = CanonicalDecimal(quantity),
            LimitPrice = CanonicalDecimal(limitPrice),
            Explanation = explanation,
            Confidence = CanonicalDecimal(confidence),
            GuardResults = guardResults.Select(result => new
            {
                result.Code,
                result.Passed,
                result.Detail
            }),
            State = state.ToString()
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    private static string? CanonicalDecimal(decimal? value) => value?.ToString(
        "G29",
        CultureInfo.InvariantCulture);

    private static string RequireText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException(message);
        }

        return value;
    }
}
