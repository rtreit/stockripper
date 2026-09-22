using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Risk;

public enum RiskControlKind
{
    ALLOWED_INSTRUMENTS,
    ALLOWED_ORDER_TYPES,
    ALLOWED_POSITION_DIRECTION,
    CAPITAL_ALLOCATION_CALCULATION,
    MAXIMUM_ORDER_EXPOSURE,
    MAXIMUM_POSITION_EXPOSURE,
    MAXIMUM_PORTFOLIO_EXPOSURE,
    CONCENTRATION_LIMIT,
    BUYING_POWER_RESERVE,
    PRICE_PROTECTION,
    MARKET_DATA_FRESHNESS,
    TRADING_SCHEDULE,
    DAILY_LOSS_RESPONSE,
    OPEN_ORDER_LIMIT,
    RETRY_LIMIT,
    REVIEW_APPROVAL_EXPIRY
}

public enum RiskControlState
{
    ENABLED,
    DISABLED
}

public sealed record RiskControlDefinition
{
    private RiskControlDefinition(
        RiskControlKind kind,
        RiskControlState state,
        string? configuration)
    {
        Kind = kind;
        State = state;
        Configuration = configuration;
    }

    public RiskControlKind Kind { get; }
    public RiskControlState State { get; }
    public string? Configuration { get; }

    public static RiskControlDefinition Enabled(RiskControlKind kind, string configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration) || configuration != configuration.Trim())
        {
            throw new DomainRuleViolationException("An enabled Risk control needs configuration.");
        }

        return new RiskControlDefinition(kind, RiskControlState.ENABLED, configuration);
    }

    public static RiskControlDefinition Disabled(RiskControlKind kind) => new(
        kind,
        RiskControlState.DISABLED,
        null);
}

public sealed record RiskPolicyVersion
{
    private const string DigestSchema = "stockripper-risk-policy-v1";

    private RiskPolicyVersion(
        VersionReference reference,
        ImmutableSortedDictionary<RiskControlKind, RiskControlDefinition> controls,
        OperatorId creatorId,
        DateTimeOffset createdAt)
    {
        Reference = reference;
        Controls = controls;
        CreatorId = creatorId;
        CreatedAt = createdAt;
    }

    public VersionReference Reference { get; }
    public ImmutableSortedDictionary<RiskControlKind, RiskControlDefinition> Controls { get; }
    public OperatorId CreatorId { get; }
    public DateTimeOffset CreatedAt { get; }

    public static RiskPolicyVersion Create(
        string identifier,
        string version,
        IEnumerable<RiskControlDefinition> controls,
        OperatorId creatorId,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(controls);
        if (string.IsNullOrWhiteSpace(identifier)
            || string.IsNullOrWhiteSpace(version)
            || identifier != identifier.Trim()
            || version != version.Trim())
        {
            throw new DomainRuleViolationException("The Risk policy identity is invalid.");
        }

        var checkedTime = UtcTimestamp.Require(createdAt, nameof(createdAt));
        var values = controls.ToArray();
        if (values.Length != Enum.GetValues<RiskControlKind>().Length
            || values.Select(control => control.Kind).Distinct().Count() != values.Length)
        {
            throw new DomainRuleViolationException(
                "A Risk policy must define or disable every required control exactly once.");
        }

        var sorted = values.ToImmutableSortedDictionary(
            control => control.Kind,
            control => control);
        var digest = ComputeDigest(identifier, version, sorted, creatorId, checkedTime);
        return new RiskPolicyVersion(
            new VersionReference(identifier, version, digest),
            sorted,
            creatorId,
            checkedTime);
    }

    public bool HasValidContentDigest() => string.Equals(
        Reference.Digest,
        ComputeDigest(Reference.Identifier, Reference.Version, Controls, CreatorId, CreatedAt),
        StringComparison.Ordinal);

    private static string ComputeDigest(
        string identifier,
        string version,
        ImmutableSortedDictionary<RiskControlKind, RiskControlDefinition> controls,
        OperatorId creatorId,
        DateTimeOffset createdAt)
    {
        var digestValue = new
        {
            Schema = DigestSchema,
            Identifier = identifier,
            Version = version,
            Controls = controls.Values.Select(control => new
            {
                Kind = control.Kind.ToString(),
                State = control.State.ToString(),
                control.Configuration
            }),
            CreatorId = creatorId.Value,
            CreatedAt = createdAt.ToString("O")
        };
        return Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(digestValue)));
    }
}
