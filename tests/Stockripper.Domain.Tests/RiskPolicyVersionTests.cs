using Stockripper.Domain.Common;
using Stockripper.Domain.Risk;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class RiskPolicyVersionTests
{
    [Fact]
    public void Every_control_can_be_explicitly_disabled()
    {
        var controls = Enum.GetValues<RiskControlKind>()
            .Select(RiskControlDefinition.Disabled)
            .ToArray();

        var policy = RiskPolicyVersion.Create(
            "risk-policy",
            "1",
            controls,
            OperatorId.New(),
            GoalTestData.ReferenceTime);

        Assert.All(policy.Controls.Values, control => Assert.Equal(RiskControlState.DISABLED, control.State));
        Assert.Equal(64, policy.Reference.Digest.Length);
    }

    [Fact]
    public void Missing_control_is_not_treated_as_disabled()
    {
        var controls = Enum.GetValues<RiskControlKind>()
            .Skip(1)
            .Select(RiskControlDefinition.Disabled);

        Assert.Throws<DomainRuleViolationException>(() => RiskPolicyVersion.Create(
            "risk-policy",
            "1",
            controls,
            OperatorId.New(),
            GoalTestData.ReferenceTime));
    }

    [Fact]
    public void Paper_policy_defines_every_control_and_preserves_its_digest()
    {
        var policy = PaperRiskPolicy.Create(
            "paper-risk-policy",
            "1",
            ["AAPL", "MSFT"],
            5_000m,
            8_000m,
            10_000m,
            0.75m,
            1_000m,
            TimeSpan.FromMinutes(1),
            5,
            0,
            TimeSpan.FromMinutes(2),
            OperatorId.New(),
            GoalTestData.ReferenceTime);

        Assert.Equal(Enum.GetValues<RiskControlKind>().Length,
            policy.Version.Controls.Count);
        Assert.Equal(RiskControlState.DISABLED,
            policy.Version.Controls[RiskControlKind.DAILY_LOSS_RESPONSE].State);
        Assert.All(policy.Version.Controls.Where(item =>
                item.Key != RiskControlKind.DAILY_LOSS_RESPONSE),
            item => Assert.Equal(RiskControlState.ENABLED, item.Value.State));
        Assert.True(policy.Version.HasValidContentDigest());
        Assert.True(policy.IsBoundTo(policy.Version.Reference));
    }
}
