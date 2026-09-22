using Stockripper.Application.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Risk;

namespace Stockripper.Testing;

public sealed class FixedPaperRiskPolicyProvider(PaperRiskPolicy policy)
    : IPaperRiskPolicyProvider
{
    public FixedPaperRiskPolicyProvider() : this(GoalTestData.DefaultPaperRiskPolicy)
    {
    }

    public ValueTask<PaperRiskPolicy?> FindAsync(
        VersionReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PaperRiskPolicy?>(
            policy.IsBoundTo(reference) ? policy : null);
    }
}
