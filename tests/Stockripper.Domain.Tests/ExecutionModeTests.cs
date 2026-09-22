using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Domain.Tests;

public sealed class ExecutionModeTests
{
    public static TheoryData<string, ExecutionMode, bool, bool, BrokerEnvironment?> Modes => new()
    {
        { "REPLAY", ExecutionMode.REPLAY, false, false, null },
        { "RESEARCH", ExecutionMode.RESEARCH, false, false, null },
        { "PAPER_REVIEW", ExecutionMode.PAPER_REVIEW, true, true, BrokerEnvironment.PAPER },
        { "PAPER_AUTO", ExecutionMode.PAPER_AUTO, true, false, BrokerEnvironment.PAPER },
        { "LIVE_REVIEW", ExecutionMode.LIVE_REVIEW, true, true, BrokerEnvironment.LIVE },
        { "LIVE_AUTO", ExecutionMode.LIVE_AUTO, true, false, BrokerEnvironment.LIVE }
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void Exact_mode_values_have_expected_rules(
        string wireValue,
        ExecutionMode expectedMode,
        bool executable,
        bool requiresReview,
        BrokerEnvironment? requiredEnvironment)
    {
        var mode = ExecutionModeRules.Parse(wireValue);

        Assert.Equal(expectedMode, mode);
        Assert.Equal(wireValue, mode.ToWireValue());
        Assert.Equal(executable, mode.IsExecutable());
        Assert.Equal(requiresReview, mode.RequiresIntentReview());
        Assert.Equal(requiredEnvironment, mode.RequiredBrokerEnvironment());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("paper_auto")]
    [InlineData("PAPER")]
    [InlineData("1")]
    [InlineData("LIVE_AUTO ")]
    public void Missing_or_unknown_mode_is_rejected(string? value)
    {
        Assert.Throws<DomainRuleViolationException>(() => ExecutionModeRules.Parse(value));
    }

    [Fact]
    public void Undefined_enum_value_is_rejected_by_every_rule()
    {
        var mode = (ExecutionMode)999;

        Assert.Throws<DomainRuleViolationException>(() => mode.ToWireValue());
        Assert.Throws<DomainRuleViolationException>(() => mode.IsExecutable());
        Assert.Throws<DomainRuleViolationException>(() => mode.RequiresIntentReview());
        Assert.Throws<DomainRuleViolationException>(() => mode.RequiredBrokerEnvironment());
    }
}
