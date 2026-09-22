using System.Collections.Immutable;
using System.Globalization;
using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Risk;

public sealed record PaperRiskPolicy
{
    private PaperRiskPolicy(
        RiskPolicyVersion version,
        ImmutableHashSet<string> allowedInstruments,
        decimal maximumOrderExposure,
        decimal maximumPositionExposure,
        decimal maximumPortfolioExposure,
        decimal maximumConcentration,
        decimal minimumBuyingPowerReserve,
        TimeSpan maximumMarketDataAge,
        int maximumOpenOrders,
        int retryLimit,
        TimeSpan reviewApprovalLifetime)
    {
        Version = version;
        AllowedInstruments = allowedInstruments;
        MaximumOrderExposure = maximumOrderExposure;
        MaximumPositionExposure = maximumPositionExposure;
        MaximumPortfolioExposure = maximumPortfolioExposure;
        MaximumConcentration = maximumConcentration;
        MinimumBuyingPowerReserve = minimumBuyingPowerReserve;
        MaximumMarketDataAge = maximumMarketDataAge;
        MaximumOpenOrders = maximumOpenOrders;
        RetryLimit = retryLimit;
        ReviewApprovalLifetime = reviewApprovalLifetime;
    }

    public RiskPolicyVersion Version { get; }
    public ImmutableHashSet<string> AllowedInstruments { get; }
    public decimal MaximumOrderExposure { get; }
    public decimal MaximumPositionExposure { get; }
    public decimal MaximumPortfolioExposure { get; }
    public decimal MaximumConcentration { get; }
    public decimal MinimumBuyingPowerReserve { get; }
    public TimeSpan MaximumMarketDataAge { get; }
    public int MaximumOpenOrders { get; }
    public int RetryLimit { get; }
    public TimeSpan ReviewApprovalLifetime { get; }

    public static PaperRiskPolicy Create(
        string identifier,
        string version,
        IEnumerable<string> allowedInstruments,
        decimal maximumOrderExposure,
        decimal maximumPositionExposure,
        decimal maximumPortfolioExposure,
        decimal maximumConcentration,
        decimal minimumBuyingPowerReserve,
        TimeSpan maximumMarketDataAge,
        int maximumOpenOrders,
        int retryLimit,
        TimeSpan reviewApprovalLifetime,
        OperatorId creatorId,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(allowedInstruments);
        var instruments = allowedInstruments
            .Select(value => value.Trim().ToUpperInvariant())
            .Where(value => value.Length != 0)
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (instruments.Count == 0
            || maximumOrderExposure <= 0m
            || maximumPositionExposure <= 0m
            || maximumPortfolioExposure <= 0m
            || maximumOrderExposure > maximumPositionExposure
            || maximumPositionExposure > maximumPortfolioExposure
            || maximumConcentration is <= 0m or > 1m
            || minimumBuyingPowerReserve < 0m
            || maximumMarketDataAge <= TimeSpan.Zero
            || maximumOpenOrders < 1
            || retryLimit < 0
            || reviewApprovalLifetime <= TimeSpan.Zero)
        {
            throw new DomainRuleViolationException("The paper Risk policy limits are invalid.");
        }

        var invariant = CultureInfo.InvariantCulture;
        var controls = new[]
        {
            RiskControlDefinition.Enabled(RiskControlKind.ALLOWED_INSTRUMENTS,
                string.Join(',', instruments.Order(StringComparer.Ordinal))),
            RiskControlDefinition.Enabled(RiskControlKind.ALLOWED_ORDER_TYPES, "LIMIT_DAY"),
            RiskControlDefinition.Enabled(RiskControlKind.ALLOWED_POSITION_DIRECTION, "LONG_ONLY"),
            RiskControlDefinition.Enabled(RiskControlKind.CAPITAL_ALLOCATION_CALCULATION,
                "WORST_CASE_LIMIT_NOTIONAL"),
            RiskControlDefinition.Enabled(RiskControlKind.MAXIMUM_ORDER_EXPOSURE,
                maximumOrderExposure.ToString(invariant)),
            RiskControlDefinition.Enabled(RiskControlKind.MAXIMUM_POSITION_EXPOSURE,
                maximumPositionExposure.ToString(invariant)),
            RiskControlDefinition.Enabled(RiskControlKind.MAXIMUM_PORTFOLIO_EXPOSURE,
                maximumPortfolioExposure.ToString(invariant)),
            RiskControlDefinition.Enabled(RiskControlKind.CONCENTRATION_LIMIT,
                maximumConcentration.ToString(invariant)),
            RiskControlDefinition.Enabled(RiskControlKind.BUYING_POWER_RESERVE,
                minimumBuyingPowerReserve.ToString(invariant)),
            RiskControlDefinition.Enabled(RiskControlKind.PRICE_PROTECTION, "LIMIT_REQUIRED"),
            RiskControlDefinition.Enabled(RiskControlKind.MARKET_DATA_FRESHNESS,
                ((long)maximumMarketDataAge.TotalSeconds).ToString(invariant)),
            RiskControlDefinition.Enabled(RiskControlKind.TRADING_SCHEDULE,
                "GOAL_EXECUTION_SCHEDULE"),
            RiskControlDefinition.Disabled(RiskControlKind.DAILY_LOSS_RESPONSE),
            RiskControlDefinition.Enabled(RiskControlKind.OPEN_ORDER_LIMIT,
                maximumOpenOrders.ToString(invariant)),
            RiskControlDefinition.Enabled(RiskControlKind.RETRY_LIMIT,
                retryLimit.ToString(invariant)),
            RiskControlDefinition.Enabled(RiskControlKind.REVIEW_APPROVAL_EXPIRY,
                ((long)reviewApprovalLifetime.TotalSeconds).ToString(invariant))
        };
        var policyVersion = RiskPolicyVersion.Create(
            identifier, version, controls, creatorId, createdAt);
        return new PaperRiskPolicy(policyVersion, instruments, maximumOrderExposure,
            maximumPositionExposure, maximumPortfolioExposure, maximumConcentration,
            minimumBuyingPowerReserve, maximumMarketDataAge, maximumOpenOrders,
            retryLimit, reviewApprovalLifetime);
    }

    public bool IsBoundTo(VersionReference reference) =>
        Version.Reference == reference && Version.HasValidContentDigest();
}
