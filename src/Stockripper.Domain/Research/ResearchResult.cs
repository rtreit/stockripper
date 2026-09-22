using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Research;

public sealed record ResearchResult
{
    private const string DigestSchema = "stockripper-research-result-v1";

    private ResearchResult(
        ResearchResultId id,
        SessionId sessionId,
        ActivationId activationId,
        GoalVersionId goalVersionId,
        DataSnapshotId dataSnapshotId,
        string dataSnapshotDigest,
        VersionReference strategyVersion,
        VersionReference dataPolicyVersion,
        ImmutableArray<DataSourceObservation> sourceObservations,
        ImmutableSortedDictionary<string, decimal> outputMetrics,
        string evidence,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string contentDigest)
    {
        Id = id;
        SessionId = sessionId;
        ActivationId = activationId;
        GoalVersionId = goalVersionId;
        DataSnapshotId = dataSnapshotId;
        DataSnapshotDigest = dataSnapshotDigest;
        StrategyVersion = strategyVersion;
        DataPolicyVersion = dataPolicyVersion;
        SourceObservations = sourceObservations;
        OutputMetrics = outputMetrics;
        Evidence = evidence;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        ContentDigest = contentDigest;
    }

    public ResearchResultId Id { get; }
    public SessionId SessionId { get; }
    public ActivationId ActivationId { get; }
    public GoalVersionId GoalVersionId { get; }
    public DataSnapshotId DataSnapshotId { get; }
    public string DataSnapshotDigest { get; }
    public VersionReference StrategyVersion { get; }
    public VersionReference DataPolicyVersion { get; }
    public ImmutableArray<DataSourceObservation> SourceObservations { get; }
    public ImmutableSortedDictionary<string, decimal> OutputMetrics { get; }
    public string Evidence { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset CompletedAt { get; }
    public string ContentDigest { get; }

    public static ResearchResult Create(
        ResearchResultId id,
        SessionId sessionId,
        ActivationId activationId,
        GoalVersion goalVersion,
        DataSnapshot snapshot,
        IReadOnlyDictionary<string, decimal> outputMetrics,
        string evidence,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(goalVersion);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(outputMetrics);
        if (goalVersion.Mode != Execution.ExecutionMode.RESEARCH)
        {
            throw new DomainRuleViolationException("A Research result needs a RESEARCH Goal version.");
        }

        if (!goalVersion.HasValidContentDigest()
            || !snapshot.HasValidContentDigest()
            || snapshot.DataPolicyVersion != goalVersion.DataPolicyVersion)
        {
            throw new DomainRuleViolationException("The Research result input binding is invalid.");
        }

        var checkedStartedAt = UtcTimestamp.Require(startedAt, nameof(startedAt));
        var checkedCompletedAt = UtcTimestamp.Require(completedAt, nameof(completedAt));
        if (checkedCompletedAt < checkedStartedAt)
        {
            throw new DomainRuleViolationException("Research completion cannot precede its start.");
        }

        if (string.IsNullOrWhiteSpace(evidence) || evidence != evidence.Trim())
        {
            throw new DomainRuleViolationException("Research evidence is required.");
        }

        var metrics = outputMetrics.ToImmutableSortedDictionary(StringComparer.Ordinal);
        if (metrics.IsEmpty)
        {
            throw new DomainRuleViolationException("Research output metrics are required.");
        }

        var digest = ComputeDigest(goalVersion, snapshot, metrics, evidence);
        return new ResearchResult(
            id,
            sessionId,
            activationId,
            goalVersion.Id,
            snapshot.Id,
            snapshot.ContentDigest,
            goalVersion.StrategyVersion,
            goalVersion.DataPolicyVersion,
            snapshot.SourceObservations,
            metrics,
            evidence,
            checkedStartedAt,
            checkedCompletedAt,
            digest);
    }

    private static string ComputeDigest(
        GoalVersion goalVersion,
        DataSnapshot snapshot,
        ImmutableSortedDictionary<string, decimal> metrics,
        string evidence)
    {
        var value = new
        {
            Schema = DigestSchema,
            GoalVersionId = goalVersion.Id.Value,
            GoalVersionDigest = goalVersion.ContentDigest,
            DataSnapshotId = snapshot.Id.Value,
            DataSnapshotDigest = snapshot.ContentDigest,
            StrategyVersion = goalVersion.StrategyVersion,
            DataPolicyVersion = goalVersion.DataPolicyVersion,
            SourceObservations = snapshot.SourceObservations,
            OutputMetrics = metrics,
            Evidence = evidence
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }
}
