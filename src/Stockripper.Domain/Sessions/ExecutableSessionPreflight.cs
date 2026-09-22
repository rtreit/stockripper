using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Sessions;

public enum ExecutablePreflightCheckCode
{
    ACTIVATION_ACTIVE,
    GOAL_DIGEST,
    EXECUTION_SCHEDULE,
    DATA_SNAPSHOT,
    DATA_SOURCES,
    POLICY_BINDINGS,
    KILL_SWITCH,
    BROKER_ENVIRONMENT,
    ENDPOINT_IDENTITY,
    ACCOUNT_IDENTITY,
    ACCOUNT_CAPABILITIES,
    BROKER_CLOCK,
    MARKET_SESSION,
    RECONCILIATION,
    CAPITAL_DATA,
    BUYING_POWER,
    ACCOUNT_ALLOCATION
}

public enum ExecutablePreflightResult
{
    PASSED,
    FAILED
}

public sealed record ExecutablePreflightCheck
{
    public ExecutablePreflightCheck(
        ExecutablePreflightCheckCode code,
        bool passed,
        string detail)
    {
        if (string.IsNullOrWhiteSpace(detail) || detail != detail.Trim())
        {
            throw new DomainRuleViolationException("The preflight check detail is invalid.");
        }

        Code = code;
        Passed = passed;
        Detail = detail;
    }

    public ExecutablePreflightCheckCode Code { get; }
    public bool Passed { get; }
    public string Detail { get; }
}

public sealed record ExecutableSessionPreflight
{
    private ExecutableSessionPreflight(
        SessionPreflightId id,
        SessionId sessionId,
        GoalVersionId goalVersionId,
        ActivationId activationId,
        VersionReference brokerConnectionVersion,
        ReconciliationId? reconciliationId,
        BrokerEnvironment? observedEnvironment,
        string? observedEndpointIdentity,
        string? observedAccountIdentifier,
        ImmutableArray<string> observedCapabilities,
        DateTimeOffset? brokerObservedAt,
        DateTimeOffset? brokerTime,
        DateTimeOffset? capitalObservedAt,
        DateTimeOffset? reconciliationObservedAt,
        string currency,
        decimal? positionExposure,
        decimal? openOrderExposure,
        decimal? buyingPower,
        ImmutableArray<ExecutablePreflightCheck> checks,
        ExecutablePreflightResult result,
        DateTimeOffset completedAt,
        string contentDigest)
    {
        Id = id;
        SessionId = sessionId;
        GoalVersionId = goalVersionId;
        ActivationId = activationId;
        BrokerConnectionVersion = brokerConnectionVersion;
        ReconciliationId = reconciliationId;
        ObservedEnvironment = observedEnvironment;
        ObservedEndpointIdentity = observedEndpointIdentity;
        ObservedAccountIdentifier = observedAccountIdentifier;
        ObservedCapabilities = observedCapabilities;
        BrokerObservedAt = brokerObservedAt;
        BrokerTime = brokerTime;
        CapitalObservedAt = capitalObservedAt;
        ReconciliationObservedAt = reconciliationObservedAt;
        Currency = currency;
        PositionExposure = positionExposure;
        OpenOrderExposure = openOrderExposure;
        BuyingPower = buyingPower;
        Checks = checks;
        Result = result;
        CompletedAt = completedAt;
        ContentDigest = contentDigest;
    }

    public SessionPreflightId Id { get; }
    public SessionId SessionId { get; }
    public GoalVersionId GoalVersionId { get; }
    public ActivationId ActivationId { get; }
    public VersionReference BrokerConnectionVersion { get; }
    public ReconciliationId? ReconciliationId { get; }
    public BrokerEnvironment? ObservedEnvironment { get; }
    public string? ObservedEndpointIdentity { get; }
    public string? ObservedAccountIdentifier { get; }
    public ImmutableArray<string> ObservedCapabilities { get; }
    public DateTimeOffset? BrokerObservedAt { get; }
    public DateTimeOffset? BrokerTime { get; }
    public DateTimeOffset? CapitalObservedAt { get; }
    public DateTimeOffset? ReconciliationObservedAt { get; }
    public string Currency { get; }
    public decimal? PositionExposure { get; }
    public decimal? OpenOrderExposure { get; }
    public decimal? BuyingPower { get; }
    public ImmutableArray<ExecutablePreflightCheck> Checks { get; }
    public ExecutablePreflightResult Result { get; }
    public DateTimeOffset CompletedAt { get; }
    public string ContentDigest { get; }

    public static ExecutableSessionPreflight Create(
        SessionPreflightId id,
        Session session,
        VersionReference brokerConnectionVersion,
        ReconciliationId? reconciliationId,
        BrokerEnvironment? observedEnvironment,
        string? observedEndpointIdentity,
        string? observedAccountIdentifier,
        IEnumerable<string> observedCapabilities,
        DateTimeOffset? brokerObservedAt,
        DateTimeOffset? brokerTime,
        DateTimeOffset? capitalObservedAt,
        DateTimeOffset? reconciliationObservedAt,
        string currency,
        decimal? positionExposure,
        decimal? openOrderExposure,
        decimal? buyingPower,
        IEnumerable<ExecutablePreflightCheck> checks,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(brokerConnectionVersion);
        ArgumentNullException.ThrowIfNull(observedCapabilities);
        ArgumentNullException.ThrowIfNull(checks);
        if (!session.Mode.IsExecutable() || session.State != SessionState.PREFLIGHT)
        {
            throw new DomainRuleViolationException("Executable preflight needs a PREFLIGHT Session.");
        }

        var checkedCapabilities = observedCapabilities
            .Select(RequireText)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var checkedResults = checks.ToImmutableArray();
        if (checkedResults.IsEmpty
            || checkedResults.Select(check => check.Code).Distinct().Count() != checkedResults.Length)
        {
            throw new DomainRuleViolationException("Executable preflight checks are incomplete.");
        }

        var result = checkedResults.All(check => check.Passed)
            ? ExecutablePreflightResult.PASSED
            : ExecutablePreflightResult.FAILED;
        var checkedCompletedAt = UtcTimestamp.Require(completedAt, nameof(completedAt));
        var checkedBrokerObservedAt = Normalize(brokerObservedAt, nameof(brokerObservedAt));
        var checkedBrokerTime = Normalize(brokerTime, nameof(brokerTime));
        var checkedCapitalObservedAt = Normalize(capitalObservedAt, nameof(capitalObservedAt));
        var checkedReconciliationObservedAt = Normalize(
            reconciliationObservedAt,
            nameof(reconciliationObservedAt));
        if (positionExposure < 0m || openOrderExposure < 0m || buyingPower < 0m)
        {
            throw new DomainRuleViolationException("Executable preflight money values cannot be negative.");
        }

        var checkedCurrency = RequireText(currency);
        if (checkedCurrency.Length != 3
            || checkedCurrency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new DomainRuleViolationException("Executable preflight currency is invalid.");
        }

        var digest = ComputeDigest(
            id,
            session.Id,
            session.GoalVersionId,
            session.ActivationId,
            brokerConnectionVersion,
            reconciliationId,
            observedEnvironment,
            observedEndpointIdentity,
            observedAccountIdentifier,
            checkedCapabilities,
            checkedBrokerObservedAt,
            checkedBrokerTime,
            checkedCapitalObservedAt,
            checkedReconciliationObservedAt,
            checkedCurrency,
            positionExposure,
            openOrderExposure,
            buyingPower,
            checkedResults,
            result,
            checkedCompletedAt);
        if (result == ExecutablePreflightResult.PASSED && reconciliationId is null)
        {
            throw new DomainRuleViolationException(
                "Passed executable preflight needs durable reconciliation evidence.");
        }

        return new ExecutableSessionPreflight(
            id,
            session.Id,
            session.GoalVersionId,
            session.ActivationId,
            brokerConnectionVersion,
            reconciliationId,
            observedEnvironment,
            observedEndpointIdentity,
            observedAccountIdentifier,
            checkedCapabilities,
            checkedBrokerObservedAt,
            checkedBrokerTime,
            checkedCapitalObservedAt,
            checkedReconciliationObservedAt,
            checkedCurrency,
            positionExposure,
            openOrderExposure,
            buyingPower,
            checkedResults,
            result,
            checkedCompletedAt,
            digest);
    }

    public bool HasValidContentDigest() => string.Equals(
        ContentDigest,
        ComputeDigest(
            Id,
            SessionId,
            GoalVersionId,
            ActivationId,
            BrokerConnectionVersion,
            ReconciliationId,
            ObservedEnvironment,
            ObservedEndpointIdentity,
            ObservedAccountIdentifier,
            ObservedCapabilities,
            BrokerObservedAt,
            BrokerTime,
            CapitalObservedAt,
            ReconciliationObservedAt,
            Currency,
            PositionExposure,
            OpenOrderExposure,
            BuyingPower,
            Checks,
            Result,
            CompletedAt),
        StringComparison.Ordinal);

    public static ExecutableSessionPreflight Restore(
        SessionPreflightId id,
        SessionId sessionId,
        GoalVersionId goalVersionId,
        ActivationId activationId,
        VersionReference brokerConnectionVersion,
        ReconciliationId? reconciliationId,
        BrokerEnvironment? observedEnvironment,
        string? observedEndpointIdentity,
        string? observedAccountIdentifier,
        IEnumerable<string> observedCapabilities,
        DateTimeOffset? brokerObservedAt,
        DateTimeOffset? brokerTime,
        DateTimeOffset? capitalObservedAt,
        DateTimeOffset? reconciliationObservedAt,
        string currency,
        decimal? positionExposure,
        decimal? openOrderExposure,
        decimal? buyingPower,
        IEnumerable<ExecutablePreflightCheck> checks,
        ExecutablePreflightResult result,
        DateTimeOffset completedAt,
        string contentDigest)
    {
        var capabilities = observedCapabilities
            .Select(RequireText)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var results = checks.ToImmutableArray();
        var completed = UtcTimestamp.Require(completedAt, nameof(completedAt));
        var restored = new ExecutableSessionPreflight(
            id,
            sessionId,
            goalVersionId,
            activationId,
            brokerConnectionVersion,
            reconciliationId,
            observedEnvironment,
            observedEndpointIdentity,
            observedAccountIdentifier,
            capabilities,
            Normalize(brokerObservedAt, nameof(brokerObservedAt)),
            Normalize(brokerTime, nameof(brokerTime)),
            Normalize(capitalObservedAt, nameof(capitalObservedAt)),
            Normalize(reconciliationObservedAt, nameof(reconciliationObservedAt)),
            RequireText(currency),
            positionExposure,
            openOrderExposure,
            buyingPower,
            results,
            result,
            completed,
            contentDigest);
        if (results.IsEmpty
            || results.Select(check => check.Code).Distinct().Count() != results.Length
            || (results.All(check => check.Passed)
                ? ExecutablePreflightResult.PASSED
                : ExecutablePreflightResult.FAILED) != result
            || positionExposure < 0m
            || openOrderExposure < 0m
            || buyingPower < 0m
            || (result == ExecutablePreflightResult.PASSED && reconciliationId is null)
            || !restored.HasValidContentDigest())
        {
            throw new InvalidDataException("The stored executable preflight evidence is invalid.");
        }

        return restored;
    }

    private static string ComputeDigest(
        SessionPreflightId id,
        SessionId sessionId,
        GoalVersionId goalVersionId,
        ActivationId activationId,
        VersionReference brokerConnectionVersion,
        ReconciliationId? reconciliationId,
        BrokerEnvironment? observedEnvironment,
        string? observedEndpointIdentity,
        string? observedAccountIdentifier,
        IEnumerable<string> observedCapabilities,
        DateTimeOffset? brokerObservedAt,
        DateTimeOffset? brokerTime,
        DateTimeOffset? capitalObservedAt,
        DateTimeOffset? reconciliationObservedAt,
        string currency,
        decimal? positionExposure,
        decimal? openOrderExposure,
        decimal? buyingPower,
        IEnumerable<ExecutablePreflightCheck> checks,
        ExecutablePreflightResult result,
        DateTimeOffset completedAt)
    {
        var value = new
        {
            Schema = "stockripper-executable-session-preflight-v1",
            Id = id.Value,
            SessionId = sessionId.Value,
            GoalVersionId = goalVersionId.Value,
            ActivationId = activationId.Value,
            BrokerConnectionVersion = VersionValue(brokerConnectionVersion),
            ReconciliationId = reconciliationId?.Value,
            Environment = observedEnvironment?.ToString(),
            EndpointIdentity = observedEndpointIdentity,
            AccountIdentifier = observedAccountIdentifier,
            Capabilities = observedCapabilities.Order(StringComparer.Ordinal),
            BrokerObservedAt = brokerObservedAt?.ToString("O"),
            BrokerTime = brokerTime?.ToString("O"),
            CapitalObservedAt = capitalObservedAt?.ToString("O"),
            ReconciliationObservedAt = reconciliationObservedAt?.ToString("O"),
            Currency = currency,
            PositionExposure = DecimalValue(positionExposure),
            OpenOrderExposure = DecimalValue(openOrderExposure),
            BuyingPower = DecimalValue(buyingPower),
            Checks = checks.OrderBy(check => check.Code).Select(check => new
            {
                Code = check.Code.ToString(),
                check.Passed,
                check.Detail
            }),
            Result = result.ToString(),
            CompletedAt = completedAt.ToString("O")
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    }

    private static object VersionValue(VersionReference version) => new
    {
        version.Identifier,
        version.Version,
        version.Digest
    };

    private static string? DecimalValue(decimal? value) => value?.ToString(
        "G29",
        CultureInfo.InvariantCulture);

    private static DateTimeOffset? Normalize(DateTimeOffset? value, string parameterName) => value is null
        ? null
        : UtcTimestamp.Require(value.Value, parameterName);

    private static string RequireText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException("Executable preflight text is invalid.");
        }

        return value;
    }
}
