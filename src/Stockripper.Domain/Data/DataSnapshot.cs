using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Data;

public sealed record DataSourceObservation
{
    public DataSourceObservation(string sourceIdentifier, string sourceVersion, DateTimeOffset observedAt)
    {
        SourceIdentifier = RequireText(sourceIdentifier, "The data source identifier is required.");
        SourceVersion = RequireText(sourceVersion, "The data source version is required.");
        ObservedAt = UtcTimestamp.Require(observedAt, nameof(observedAt));
    }

    public string SourceIdentifier { get; }

    public string SourceVersion { get; }

    public DateTimeOffset ObservedAt { get; }

    private static string RequireText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException(message);
        }

        return value;
    }
}

public sealed record DataSnapshot
{
    private const string DigestSchema = "stockripper-data-snapshot-v1";

    private DataSnapshot(
        DataSnapshotId id,
        VersionReference dataSetVersion,
        VersionReference dataPolicyVersion,
        string schemaVersion,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateTimeOffset capturedAt,
        ImmutableArray<DataSourceObservation> sourceObservations,
        ImmutableArray<byte> payload,
        string payloadDigest,
        string contentDigest)
    {
        Id = id;
        DataSetVersion = dataSetVersion;
        DataPolicyVersion = dataPolicyVersion;
        SchemaVersion = schemaVersion;
        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
        CapturedAt = capturedAt;
        SourceObservations = sourceObservations;
        Payload = payload;
        PayloadDigest = payloadDigest;
        ContentDigest = contentDigest;
    }

    public DataSnapshotId Id { get; }

    public VersionReference DataSetVersion { get; }

    public VersionReference DataPolicyVersion { get; }

    public string SchemaVersion { get; }

    public DateTimeOffset RangeStart { get; }

    public DateTimeOffset RangeEnd { get; }

    public DateTimeOffset CapturedAt { get; }

    public ImmutableArray<DataSourceObservation> SourceObservations { get; }

    public ImmutableArray<byte> Payload { get; }

    public string PayloadDigest { get; }

    public string ContentDigest { get; }

    public static DataSnapshot Create(
        DataSnapshotId id,
        VersionReference dataSetVersion,
        VersionReference dataPolicyVersion,
        string schemaVersion,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateTimeOffset capturedAt,
        IEnumerable<DataSourceObservation> sourceObservations,
        ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(dataSetVersion);
        ArgumentNullException.ThrowIfNull(dataPolicyVersion);
        ArgumentNullException.ThrowIfNull(sourceObservations);
        if (id.Value == Guid.Empty)
        {
            throw new DomainRuleViolationException("The data snapshot identifier is empty.");
        }

        var checkedRangeStart = UtcTimestamp.Require(rangeStart, nameof(rangeStart));
        var checkedRangeEnd = UtcTimestamp.Require(rangeEnd, nameof(rangeEnd));
        var checkedCapturedAt = UtcTimestamp.Require(capturedAt, nameof(capturedAt));
        if (checkedRangeEnd < checkedRangeStart)
        {
            throw new DomainRuleViolationException("The data snapshot range end cannot precede its start.");
        }

        if (checkedCapturedAt < checkedRangeEnd)
        {
            throw new DomainRuleViolationException("The data snapshot capture time cannot precede its range end.");
        }

        var checkedSchemaVersion = RequireText(schemaVersion, "The data snapshot schema version is required.");
        var checkedSources = sourceObservations
            .OrderBy(source => source.SourceIdentifier, StringComparer.Ordinal)
            .ThenBy(source => source.SourceVersion, StringComparer.Ordinal)
            .ToImmutableArray();
        if (checkedSources.IsEmpty
            || checkedSources.Select(source => source.SourceIdentifier).Distinct(StringComparer.Ordinal).Count()
                != checkedSources.Length)
        {
            throw new DomainRuleViolationException("The data snapshot needs unique source identifiers.");
        }

        if (checkedSources.Any(source => source.ObservedAt > checkedCapturedAt))
        {
            throw new DomainRuleViolationException("A data observation cannot follow the snapshot capture time.");
        }

        if (payload.IsEmpty)
        {
            throw new DomainRuleViolationException("The data snapshot payload is required.");
        }

        var checkedPayload = ImmutableArray.CreateRange(payload.ToArray());
        var payloadDigest = Convert.ToHexStringLower(SHA256.HashData(checkedPayload.AsSpan()));
        var contentDigest = ComputeContentDigest(
            id,
            dataSetVersion,
            dataPolicyVersion,
            checkedSchemaVersion,
            checkedRangeStart,
            checkedRangeEnd,
            checkedCapturedAt,
            checkedSources,
            payloadDigest);

        return new DataSnapshot(
            id,
            dataSetVersion,
            dataPolicyVersion,
            checkedSchemaVersion,
            checkedRangeStart,
            checkedRangeEnd,
            checkedCapturedAt,
            checkedSources,
            checkedPayload,
            payloadDigest,
            contentDigest);
    }

    public bool HasValidContentDigest() => string.Equals(
        ContentDigest,
        ComputeContentDigest(
            Id,
            DataSetVersion,
            DataPolicyVersion,
            SchemaVersion,
            RangeStart,
            RangeEnd,
            CapturedAt,
            SourceObservations,
            PayloadDigest),
        StringComparison.Ordinal)
        && string.Equals(
            PayloadDigest,
            Convert.ToHexStringLower(SHA256.HashData(Payload.AsSpan())),
            StringComparison.Ordinal);

    private static string ComputeContentDigest(
        DataSnapshotId id,
        VersionReference dataSetVersion,
        VersionReference dataPolicyVersion,
        string schemaVersion,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateTimeOffset capturedAt,
        ImmutableArray<DataSourceObservation> sourceObservations,
        string payloadDigest)
    {
        var value = new
        {
            Schema = DigestSchema,
            Id = id.Value,
            DataSetVersion = VersionValue(dataSetVersion),
            DataPolicyVersion = VersionValue(dataPolicyVersion),
            SchemaVersion = schemaVersion,
            RangeStart = rangeStart.ToString("O"),
            RangeEnd = rangeEnd.ToString("O"),
            CapturedAt = capturedAt.ToString("O"),
            Sources = sourceObservations.Select(source => new
            {
                source.SourceIdentifier,
                source.SourceVersion,
                ObservedAt = source.ObservedAt.ToString("O")
            }),
            PayloadDigest = payloadDigest
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    private static object VersionValue(VersionReference value) => new
    {
        value.Identifier,
        value.Version,
        value.Digest
    };

    private static string RequireText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException(message);
        }

        return value;
    }
}
