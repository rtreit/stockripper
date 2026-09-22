using Stockripper.Application.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Risk;

namespace Stockripper.Infrastructure.Brokers;

public sealed class StaticPaperRiskPolicyProvider(PaperRiskPolicy policy)
    : IPaperRiskPolicyProvider
{
    public ValueTask<PaperRiskPolicy?> FindAsync(
        VersionReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PaperRiskPolicy?>(
            policy.IsBoundTo(reference) ? policy : null);
    }
}
