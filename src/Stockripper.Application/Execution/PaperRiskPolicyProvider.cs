using Stockripper.Domain.Goals;
using Stockripper.Domain.Risk;

namespace Stockripper.Application.Execution;

public interface IPaperRiskPolicyProvider
{
    ValueTask<PaperRiskPolicy?> FindAsync(
        VersionReference reference,
        CancellationToken cancellationToken);
}
