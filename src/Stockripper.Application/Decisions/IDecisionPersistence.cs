using Stockripper.Domain.Common;
using Stockripper.Domain.Decisions;

namespace Stockripper.Application.Decisions;

public interface IDecisionPersistence
{
    ValueTask InsertAsync(
        Decision decision,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<Decision?> FindAsync(
        DecisionId decisionId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Decision>> FindBySessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken);
}
