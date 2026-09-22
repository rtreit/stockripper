using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Application.Goals;

public interface IGoalPersistence
{
    ValueTask InsertDraftAsync(GoalDraft draft, CancellationToken cancellationToken);

    ValueTask<GoalDraft?> FindDraftAsync(
        GoalDraftId draftId,
        CancellationToken cancellationToken);

    ValueTask UpdateDraftAsync(
        GoalDraft revisedDraft,
        int expectedRevision,
        CancellationToken cancellationToken);

    ValueTask<GoalVersion> PublishDraftAsync(
        GoalDraftId draftId,
        int expectedRevision,
        OperatorId publisherId,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken);

    ValueTask<GoalVersion?> FindGoalVersionAsync(
        GoalVersionId goalVersionId,
        CancellationToken cancellationToken);
}
