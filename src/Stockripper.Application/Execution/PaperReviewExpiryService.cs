using Stockripper.Domain.Common;

namespace Stockripper.Application.Execution;

public sealed class PaperReviewExpiryService(
    IPaperReviewExpiryPersistence persistence,
    TimeProvider timeProvider)
{
    public ValueTask<int> ExpireDueAsync(
        OperatorId actorId,
        CancellationToken cancellationToken = default) =>
        persistence.ExpireDueAsync(
            timeProvider.GetUtcNow(),
            actorId,
            cancellationToken);
}
