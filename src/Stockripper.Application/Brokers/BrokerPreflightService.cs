using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;

namespace Stockripper.Application.Brokers;

public sealed class BrokerPreflightService(IBrokerConnectionProbe probe)
{
    public async ValueTask<BrokerConnectionObservation> VerifyAsync(
        GoalVersion goalVersion,
        CancellationToken cancellationToken = default)
    {
        if (!goalVersion.Mode.IsExecutable() || goalVersion.ExecutionBinding is null)
        {
            throw new DomainRuleViolationException("Broker preflight requires an executable Goal version.");
        }

        var binding = goalVersion.ExecutionBinding
            ?? throw new DomainRuleViolationException("The execution binding is missing.");
        var observation = await probe.ObserveAsync(cancellationToken).ConfigureAwait(false);

        if (observation.Environment != binding.ExpectedEnvironment)
        {
            throw new BrokerPreflightException(BrokerPreflightFailure.EnvironmentMismatch);
        }

        if (!string.Equals(observation.EndpointIdentity, binding.EndpointIdentity, StringComparison.Ordinal))
        {
            throw new BrokerPreflightException(BrokerPreflightFailure.EndpointMismatch);
        }

        if (!string.Equals(
                observation.AccountIdentifier,
                binding.ExpectedAccountIdentifier,
                StringComparison.Ordinal))
        {
            throw new BrokerPreflightException(BrokerPreflightFailure.AccountMismatch);
        }

        if (binding.RequiredCapabilities.Any(capability => !observation.Capabilities.Contains(capability)))
        {
            throw new BrokerPreflightException(BrokerPreflightFailure.MissingCapability);
        }

        return observation;
    }
}

public enum BrokerPreflightFailure
{
    EnvironmentMismatch,
    EndpointMismatch,
    AccountMismatch,
    MissingCapability
}

public sealed class BrokerPreflightException(BrokerPreflightFailure failure)
    : Exception("Broker preflight failed.")
{
    public BrokerPreflightFailure Failure { get; } = failure;
}
