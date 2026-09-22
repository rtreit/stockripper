using System.Collections.Immutable;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Application.Brokers;

public sealed record BrokerConnectionObservation
{
    public BrokerConnectionObservation(
        BrokerEnvironment environment,
        string endpointIdentity,
        string accountIdentifier,
        IEnumerable<string> capabilities,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        Environment = environment;
        EndpointIdentity = RequireText(endpointIdentity);
        AccountIdentifier = RequireText(accountIdentifier);
        Capabilities = capabilities
            .Select(RequireText)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableHashSet(StringComparer.Ordinal);
        ObservedAt = observedAt.Offset == TimeSpan.Zero
            ? observedAt
            : throw new DomainRuleViolationException("The Broker observation time must use UTC.");
    }

    public BrokerEnvironment Environment { get; }

    public string EndpointIdentity { get; }

    public string AccountIdentifier { get; }

    public ImmutableHashSet<string> Capabilities { get; }

    public DateTimeOffset ObservedAt { get; }

    private static string RequireText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException("The Broker observation contains invalid text.");
        }

        return value;
    }
}
