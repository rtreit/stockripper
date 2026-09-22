using Stockripper.Application.Brokers;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class BrokerPreflightServiceTests
{
    public static TheoryData<ExecutionMode> ExecutableModes => new()
    {
        ExecutionMode.PAPER_REVIEW,
        ExecutionMode.PAPER_AUTO,
        ExecutionMode.LIVE_REVIEW,
        ExecutionMode.LIVE_AUTO
    };

    [Theory]
    [MemberData(nameof(ExecutableModes))]
    public async Task Exact_binding_passes_preflight(ExecutionMode mode)
    {
        var goalVersion = GoalTestData.CreateGoalVersion(mode);
        var expected = GoalTestData.CreateObservation(goalVersion);
        var fake = new FakeBrokerConnectionProbe(expected);
        var service = new BrokerPreflightService(fake);

        var observed = await service.VerifyAsync(goalVersion);

        Assert.Same(expected, observed);
        Assert.Equal(1, fake.ObservationCount);
    }

    [Theory]
    [MemberData(nameof(ExecutableModes))]
    public async Task Wrong_account_fails_closed(ExecutionMode mode)
    {
        var goalVersion = GoalTestData.CreateGoalVersion(mode);
        var binding = goalVersion.ExecutionBinding!;
        var observation = new BrokerConnectionObservation(
            binding.ExpectedEnvironment,
            binding.EndpointIdentity,
            "wrong-account",
            binding.RequiredCapabilities,
            GoalTestData.ReferenceTime);

        var exception = await Assert.ThrowsAsync<BrokerPreflightException>(
            () => VerifyAsync(goalVersion, observation));

        Assert.Equal(BrokerPreflightFailure.AccountMismatch, exception.Failure);
        Assert.DoesNotContain("wrong-account", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ExecutableModes))]
    public async Task Wrong_endpoint_fails_closed(ExecutionMode mode)
    {
        var goalVersion = GoalTestData.CreateGoalVersion(mode);
        var binding = goalVersion.ExecutionBinding!;
        var observation = new BrokerConnectionObservation(
            binding.ExpectedEnvironment,
            "wrong-endpoint",
            binding.ExpectedAccountIdentifier,
            binding.RequiredCapabilities,
            GoalTestData.ReferenceTime);

        var exception = await Assert.ThrowsAsync<BrokerPreflightException>(
            () => VerifyAsync(goalVersion, observation));

        Assert.Equal(BrokerPreflightFailure.EndpointMismatch, exception.Failure);
    }

    [Theory]
    [MemberData(nameof(ExecutableModes))]
    public async Task Wrong_environment_fails_closed(ExecutionMode mode)
    {
        var goalVersion = GoalTestData.CreateGoalVersion(mode);
        var binding = goalVersion.ExecutionBinding!;
        var wrongEnvironment = binding.ExpectedEnvironment == BrokerEnvironment.PAPER
            ? BrokerEnvironment.LIVE
            : BrokerEnvironment.PAPER;
        var observation = new BrokerConnectionObservation(
            wrongEnvironment,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            binding.RequiredCapabilities,
            GoalTestData.ReferenceTime);

        var exception = await Assert.ThrowsAsync<BrokerPreflightException>(
            () => VerifyAsync(goalVersion, observation));

        Assert.Equal(BrokerPreflightFailure.EnvironmentMismatch, exception.Failure);
    }

    [Theory]
    [MemberData(nameof(ExecutableModes))]
    public async Task Missing_capability_fails_closed(ExecutionMode mode)
    {
        var goalVersion = GoalTestData.CreateGoalVersion(mode);
        var binding = goalVersion.ExecutionBinding!;
        var observation = new BrokerConnectionObservation(
            binding.ExpectedEnvironment,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            ["EQUITY_LIMIT_DAY"],
            GoalTestData.ReferenceTime);

        var exception = await Assert.ThrowsAsync<BrokerPreflightException>(
            () => VerifyAsync(goalVersion, observation));

        Assert.Equal(BrokerPreflightFailure.MissingCapability, exception.Failure);
    }

    [Theory]
    [InlineData(ExecutionMode.REPLAY)]
    [InlineData(ExecutionMode.RESEARCH)]
    public async Task Read_only_mode_cannot_probe_a_broker(ExecutionMode mode)
    {
        var executable = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var fake = new FakeBrokerConnectionProbe(GoalTestData.CreateObservation(executable));
        var service = new BrokerPreflightService(fake);

        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => service.VerifyAsync(GoalTestData.CreateGoalVersion(mode)).AsTask());

        Assert.Equal(0, fake.ObservationCount);
    }

    private static Task<BrokerConnectionObservation> VerifyAsync(
        Stockripper.Domain.Goals.GoalVersion goalVersion,
        BrokerConnectionObservation observation)
    {
        var service = new BrokerPreflightService(new FakeBrokerConnectionProbe(observation));
        return service.VerifyAsync(goalVersion).AsTask();
    }
}
