using System.Text.Json;
using Stockripper.Domain.Data;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;

namespace Stockripper.Infrastructure.Persistence;

internal static class PersistenceJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string SerializeVersion(VersionReference version) => JsonSerializer.Serialize(
        new VersionValue(version.Identifier, version.Version, version.Digest),
        Options);

    public static VersionReference DeserializeVersion(string json)
    {
        var value = JsonSerializer.Deserialize<VersionValue>(json, Options)
            ?? throw new InvalidDataException("The stored version reference is invalid.");
        return new VersionReference(value.Identifier, value.Version, value.Digest);
    }

    public static string SerializeSources(IReadOnlyList<DataSourceObservation> sources) =>
        JsonSerializer.Serialize(
            sources.Select(source => new SourceValue(
                source.SourceIdentifier,
                source.SourceVersion,
                source.ObservedAt)),
            Options);

    public static IReadOnlyList<DataSourceObservation> DeserializeSources(string json)
    {
        var values = JsonSerializer.Deserialize<List<SourceValue>>(json, Options)
            ?? throw new InvalidDataException("The stored data source observations are invalid.");
        return values
            .Select(value => new DataSourceObservation(
                value.SourceIdentifier,
                value.SourceVersion,
                value.ObservedAt))
            .ToArray();
    }

    public static string SerializeMetrics(IReadOnlyDictionary<string, decimal> metrics)
    {
        var sorted = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var metric in metrics)
        {
            sorted.Add(metric.Key, metric.Value);
        }

        return JsonSerializer.Serialize(sorted, Options);
    }

    public static IReadOnlyDictionary<string, decimal> DeserializeMetrics(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, decimal>>(json, Options)
        ?? throw new InvalidDataException("The stored Replay metrics are invalid.");

    public static string SerializeGuards(IReadOnlyList<DecisionGuardResult> guards) =>
        JsonSerializer.Serialize(
            guards.Select(guard => new GuardValue(guard.Code, guard.Passed, guard.Detail)),
            Options);

    public static IReadOnlyList<DecisionGuardResult> DeserializeGuards(string json)
    {
        var values = JsonSerializer.Deserialize<List<GuardValue>>(json, Options)
            ?? throw new InvalidDataException("The stored Decision guard results are invalid.");
        return values.Select(value => new DecisionGuardResult(
            value.Code,
            value.Passed,
            value.Detail)).ToArray();
    }

    public static string SerializePreflightChecks(
        IReadOnlyList<ExecutablePreflightCheck> checks) => JsonSerializer.Serialize(
        checks.Select(check => new PreflightCheckValue(
            check.Code.ToString(),
            check.Passed,
            check.Detail)),
        Options);

    public static IReadOnlyList<ExecutablePreflightCheck> DeserializePreflightChecks(string json)
    {
        var values = JsonSerializer.Deserialize<List<PreflightCheckValue>>(json, Options)
            ?? throw new InvalidDataException("The stored preflight checks are invalid.");
        return values.Select(value => new ExecutablePreflightCheck(
            Enum.Parse<ExecutablePreflightCheckCode>(value.Code, ignoreCase: false),
            value.Passed,
            value.Detail)).ToArray();
    }

    public static string SerializeRiskChecks(IReadOnlyList<RiskCheck> checks) => JsonSerializer.Serialize(
        checks.Select(check => new RiskCheckValue(
            check.Code.ToString(),
            check.Passed,
            check.Detail)),
        Options);

    public static IReadOnlyList<RiskCheck> DeserializeRiskChecks(string json)
    {
        var values = JsonSerializer.Deserialize<List<RiskCheckValue>>(json, Options)
            ?? throw new InvalidDataException("The stored risk checks are invalid.");
        return values.Select(value => new RiskCheck(
            Enum.Parse<RiskCheckCode>(value.Code, ignoreCase: false),
            value.Passed,
            value.Detail)).ToArray();
    }

    public static string SerializePreSubmitChecks(IReadOnlyList<PreSubmitCheck> checks) =>
        JsonSerializer.Serialize(
            checks.Select(check => new PreSubmitCheckValue(
                check.Code.ToString(), check.Passed, check.Reason)),
            Options);

    public static IReadOnlyList<PreSubmitCheck> DeserializePreSubmitChecks(string json)
    {
        var values = JsonSerializer.Deserialize<List<PreSubmitCheckValue>>(json, Options)
            ?? throw new InvalidDataException("The stored pre-submit checks are invalid.");
        return values.Select(value => new PreSubmitCheck(
            Enum.Parse<PreSubmitCheckCode>(value.Code, ignoreCase: false),
            value.Passed,
            value.Reason)).ToArray();
    }

    public static string SerializeStrings(IEnumerable<string> values) => JsonSerializer.Serialize(
        values.Order(StringComparer.Ordinal),
        Options);

    public static IReadOnlyList<string> DeserializeStrings(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, Options)
        ?? throw new InvalidDataException("The stored string list is invalid.");

    private sealed record VersionValue(string Identifier, string Version, string Digest);

    private sealed record SourceValue(
        string SourceIdentifier,
        string SourceVersion,
        DateTimeOffset ObservedAt);

    private sealed record GuardValue(string Code, bool Passed, string Detail);

    private sealed record PreflightCheckValue(string Code, bool Passed, string Detail);

    private sealed record RiskCheckValue(string Code, bool Passed, string Detail);

    private sealed record PreSubmitCheckValue(string Code, bool Passed, string Reason);
}
