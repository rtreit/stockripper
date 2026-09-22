using Stockripper.Domain.Common;

namespace Stockripper.Domain.Goals;

public sealed record VersionReference
{
    public VersionReference(string identifier, string version, string digest)
    {
        Identifier = RequireText(identifier, "The version identifier is required.");
        Version = RequireText(version, "The version value is required.");
        Digest = RequireText(digest, "The version digest is required.");
    }

    public string Identifier { get; }

    public string Version { get; }

    public string Digest { get; }

    private static string RequireText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException(message);
        }

        return value;
    }
}
