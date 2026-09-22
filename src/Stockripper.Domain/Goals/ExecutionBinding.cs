using System.Collections.Immutable;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Domain.Goals;

public sealed record ExecutionBinding
{
    public ExecutionBinding(
        VersionReference brokerConnectionVersion,
        BrokerEnvironment expectedEnvironment,
        string endpointIdentity,
        string expectedAccountIdentifier,
        IEnumerable<string> requiredCapabilities,
        VersionReference orderPolicyVersion,
        VersionReference reconciliationPolicyVersion)
    {
        ArgumentNullException.ThrowIfNull(brokerConnectionVersion);
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        ArgumentNullException.ThrowIfNull(orderPolicyVersion);
        ArgumentNullException.ThrowIfNull(reconciliationPolicyVersion);

        BrokerConnectionVersion = brokerConnectionVersion;
        ExpectedEnvironment = expectedEnvironment;
        EndpointIdentity = RequireText(endpointIdentity, "The endpoint identity is required.");
        ExpectedAccountIdentifier = RequireText(
            expectedAccountIdentifier,
            "The expected account identifier is required.");
        RequiredCapabilities = requiredCapabilities
            .Select(capability => RequireText(capability, "A Broker capability is invalid."))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        OrderPolicyVersion = orderPolicyVersion;
        ReconciliationPolicyVersion = reconciliationPolicyVersion;

        if (RequiredCapabilities.IsEmpty)
        {
            throw new DomainRuleViolationException("At least one Broker capability is required.");
        }
    }

    public VersionReference BrokerConnectionVersion { get; }

    public BrokerEnvironment ExpectedEnvironment { get; }

    public string EndpointIdentity { get; }

    public string ExpectedAccountIdentifier { get; }

    public ImmutableArray<string> RequiredCapabilities { get; }

    public VersionReference OrderPolicyVersion { get; }

    public VersionReference ReconciliationPolicyVersion { get; }

    private static string RequireText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException(message);
        }

        return value;
    }
}
