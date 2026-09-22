using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Replay;

public sealed record ReplayResult
{
    private const string OutputDigestSchema = "stockripper-replay-output-v1";

    private ReplayResult(
        ReplayResultId id,
        SessionId sessionId,
        ActivationId activationId,
        GoalVersionId goalVersionId,
        DataSnapshotId dataSnapshotId,
        string dataSnapshotDigest,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        VersionReference strategyVersion,
        VersionReference dataPolicyVersion,
        VersionReference riskPolicyVersion,
        long seed,
        ImmutableSortedDictionary<string, decimal> outputMetrics,
        string evidence,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string outputDigest)
    {
        Id = id;
        SessionId = sessionId;
        ActivationId = activationId;
        GoalVersionId = goalVersionId;
        DataSnapshotId = dataSnapshotId;
        DataSnapshotDigest = dataSnapshotDigest;
        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
        StrategyVersion = strategyVersion;
        DataPolicyVersion = dataPolicyVersion;
        RiskPolicyVersion = riskPolicyVersion;
        Seed = seed;
        OutputMetrics = outputMetrics;
        Evidence = evidence;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        OutputDigest = outputDigest;
    }

    public ReplayResultId Id { get; }

    public SessionId SessionId { get; }

    public ActivationId ActivationId { get; }

    public GoalVersionId GoalVersionId { get; }

    public DataSnapshotId DataSnapshotId { get; }

    public string DataSnapshotDigest { get; }

    public DateTimeOffset RangeStart { get; }

    public DateTimeOffset RangeEnd { get; }

    public VersionReference StrategyVersion { get; }

    public VersionReference DataPolicyVersion { get; }

    public VersionReference RiskPolicyVersion { get; }

    public long Seed { get; }

    public ImmutableSortedDictionary<string, decimal> OutputMetrics { get; }

    public string Evidence { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset CompletedAt { get; }

    public string OutputDigest { get; }

    public static ReplayResult Create(
        ReplayResultId id,
        SessionId sessionId,
        ActivationId activationId,
        GoalVersion goalVersion,
        DataSnapshotId dataSnapshotId,
        string dataSnapshotDigest,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        long seed,
        IReadOnlyDictionary<string, decimal> outputMetrics,
        string evidence,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(goalVersion);
        ArgumentNullException.ThrowIfNull(outputMetrics);
        if (id.Value == Guid.Empty
            || sessionId.Value == Guid.Empty
            || activationId.Value == Guid.Empty
            || dataSnapshotId.Value == Guid.Empty)
        {
            throw new DomainRuleViolationException("The Replay result contains an empty stable identifier.");
        }

        if (goalVersion.Mode != Execution.ExecutionMode.REPLAY)
        {
            throw new DomainRuleViolationException("A Replay result needs a REPLAY Goal version.");
        }

        var checkedStartedAt = UtcTimestamp.Require(startedAt, nameof(startedAt));
        var checkedCompletedAt = UtcTimestamp.Require(completedAt, nameof(completedAt));
        var checkedRangeStart = UtcTimestamp.Require(rangeStart, nameof(rangeStart));
        var checkedRangeEnd = UtcTimestamp.Require(rangeEnd, nameof(rangeEnd));
        if (checkedCompletedAt < checkedStartedAt)
        {
            throw new DomainRuleViolationException("Replay completion cannot precede its start.");
        }

        if (checkedRangeEnd < checkedRangeStart)
        {
            throw new DomainRuleViolationException("The Replay range end cannot precede its start.");
        }

        var checkedSnapshotDigest = RequireHash(dataSnapshotDigest);
        var checkedEvidence = RequireText(evidence, "Replay evidence is required.");
        var checkedMetrics = outputMetrics.ToImmutableSortedDictionary(StringComparer.Ordinal);
        if (checkedMetrics.IsEmpty
            || checkedMetrics.Keys.Any(key => string.IsNullOrWhiteSpace(key) || key != key.Trim()))
        {
            throw new DomainRuleViolationException("Replay output metrics are required.");
        }

        var outputDigest = ComputeOutputDigest(
            goalVersion,
            dataSnapshotId,
            checkedSnapshotDigest,
            checkedRangeStart,
            checkedRangeEnd,
            seed,
            checkedMetrics,
            checkedEvidence);

        return new ReplayResult(
            id,
            sessionId,
            activationId,
            goalVersion.Id,
            dataSnapshotId,
            checkedSnapshotDigest,
            checkedRangeStart,
            checkedRangeEnd,
            goalVersion.StrategyVersion,
            goalVersion.DataPolicyVersion,
            goalVersion.RiskPolicyVersion,
            seed,
            checkedMetrics,
            checkedEvidence,
            checkedStartedAt,
            checkedCompletedAt,
            outputDigest);
    }

    private static string ComputeOutputDigest(
        GoalVersion goalVersion,
        DataSnapshotId dataSnapshotId,
        string dataSnapshotDigest,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        long seed,
        ImmutableSortedDictionary<string, decimal> outputMetrics,
        string evidence)
    {
        var value = new
        {
            Schema = OutputDigestSchema,
            GoalVersionId = goalVersion.Id.Value,
            GoalVersionDigest = goalVersion.ContentDigest,
            DataSnapshotId = dataSnapshotId.Value,
            DataSnapshotDigest = dataSnapshotDigest,
            RangeStart = rangeStart.ToString("O"),
            RangeEnd = rangeEnd.ToString("O"),
            StrategyVersion = VersionValue(goalVersion.StrategyVersion),
            DataPolicyVersion = VersionValue(goalVersion.DataPolicyVersion),
            RiskPolicyVersion = VersionValue(goalVersion.RiskPolicyVersion),
            Seed = seed,
            OutputMetrics = outputMetrics,
            Evidence = evidence
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    private static object VersionValue(VersionReference value) => new
    {
        value.Identifier,
        value.Version,
        value.Digest
    };

    private static string RequireHash(string value)
    {
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new DomainRuleViolationException("The data snapshot digest is invalid.");
        }

        return value.ToLowerInvariant();
    }

    private static string RequireText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException(message);
        }

        return value;
    }
}
