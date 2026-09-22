using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class GoalVersionTests
{
    public static TheoryData<ExecutionMode> AllModes => new()
    {
        ExecutionMode.REPLAY,
        ExecutionMode.RESEARCH,
        ExecutionMode.PAPER_REVIEW,
        ExecutionMode.PAPER_AUTO,
        ExecutionMode.LIVE_REVIEW,
        ExecutionMode.LIVE_AUTO
    };

    public static TheoryData<ExecutionMode> ExecutableModes => new()
    {
        ExecutionMode.PAPER_REVIEW,
        ExecutionMode.PAPER_AUTO,
        ExecutionMode.LIVE_REVIEW,
        ExecutionMode.LIVE_AUTO
    };

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Goal_version_is_immutable_and_has_a_repeatable_digest(ExecutionMode mode)
    {
        var first = GoalTestData.CreateGoalVersion(mode);
        var second = GoalTestData.CreateGoalVersion(mode);

        Assert.Equal(64, first.ContentDigest.Length);
        Assert.Equal(first.ContentDigest, second.ContentDigest);
        Assert.True(first.HasValidContentDigest());
    }

    [Theory]
    [MemberData(nameof(ExecutableModes))]
    public void Executable_mode_requires_an_execution_binding(ExecutionMode mode)
    {
        var exception = Assert.Throws<DomainRuleViolationException>(
            () => GoalTestData.CreateGoalVersion(mode, includeDefaultBinding: false));

        Assert.Contains("execution binding", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExecutionMode.PAPER_REVIEW, BrokerEnvironment.LIVE)]
    [InlineData(ExecutionMode.PAPER_AUTO, BrokerEnvironment.LIVE)]
    [InlineData(ExecutionMode.LIVE_REVIEW, BrokerEnvironment.PAPER)]
    [InlineData(ExecutionMode.LIVE_AUTO, BrokerEnvironment.PAPER)]
    public void Executable_mode_rejects_the_wrong_environment(
        ExecutionMode mode,
        BrokerEnvironment environment)
    {
        var binding = GoalTestData.CreateExecutionBinding(environment);

        Assert.Throws<DomainRuleViolationException>(
            () => GoalTestData.CreateGoalVersion(mode, executionBinding: binding));
    }

    [Theory]
    [InlineData(ExecutionMode.REPLAY)]
    [InlineData(ExecutionMode.RESEARCH)]
    public void Read_only_mode_rejects_an_execution_binding(ExecutionMode mode)
    {
        var binding = GoalTestData.CreateExecutionBinding(BrokerEnvironment.PAPER);

        Assert.Throws<DomainRuleViolationException>(
            () => GoalTestData.CreateGoalVersion(mode, executionBinding: binding));
    }

    [Fact]
    public void Material_change_creates_a_different_digest()
    {
        var first = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY, "First objective.");
        var second = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY, "Second objective.");

        Assert.NotEqual(first.ContentDigest, second.ContentDigest);
    }

    [Fact]
    public void Goal_version_normalizes_time_to_durable_microsecond_precision()
    {
        var createdAt = GoalTestData.ReferenceTime.AddTicks(7);

        var version = GoalVersion.Publish(
            GoalVersionId.New(),
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.REPLAY),
            OperatorId.New(),
            createdAt);

        Assert.Equal(0, version.CreatedAt.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.True(version.HasValidContentDigest());
    }
}
