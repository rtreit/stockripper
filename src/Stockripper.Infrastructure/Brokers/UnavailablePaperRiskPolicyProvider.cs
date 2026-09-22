using Stockripper.Application.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Risk;

namespace Stockripper.Infrastructure.Brokers;

public sealed class UnavailablePaperRiskPolicyProvider : IPaperRiskPolicyProvider
{
    public ValueTask<PaperRiskPolicy?> FindAsync(
        VersionReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PaperRiskPolicy?>(null);
    }
}
