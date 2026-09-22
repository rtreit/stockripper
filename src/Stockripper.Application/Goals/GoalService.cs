using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Application.Goals;

public sealed class GoalService(IGoalPersistence persistence, TimeProvider timeProvider)
{
    public async ValueTask<GoalDraft> CreateDraftAsync(
        GoalId goalId,
        int versionNumber,
        GoalVersionDefinition definition,
        OperatorId creatorId,
        CancellationToken cancellationToken = default)
    {
        var draft = GoalDraft.Create(
            GoalDraftId.New(),
            goalId,
            GoalVersionId.New(),
            versionNumber,
            definition,
            creatorId,
            timeProvider.GetUtcNow());

        await persistence.InsertDraftAsync(draft, cancellationToken).ConfigureAwait(false);
        return draft;
    }

    public async ValueTask<GoalDraft> ReviseDraftAsync(
        GoalDraftId draftId,
        int expectedRevision,
        GoalVersionDefinition definition,
        OperatorId editorId,
        CancellationToken cancellationToken = default)
    {
        var current = await persistence.FindDraftAsync(draftId, cancellationToken).ConfigureAwait(false)
            ?? throw new GoalDraftNotFoundException();

        if (current.Revision != expectedRevision)
        {
            throw new GoalDraftConcurrencyException();
        }

        var revised = current.Revise(definition, editorId, timeProvider.GetUtcNow());
        await persistence.UpdateDraftAsync(revised, expectedRevision, cancellationToken).ConfigureAwait(false);
        return revised;
    }

    public ValueTask<GoalVersion> PublishDraftAsync(
        GoalDraftId draftId,
        int expectedRevision,
        OperatorId publisherId,
        CancellationToken cancellationToken = default) => persistence.PublishDraftAsync(
        draftId,
        expectedRevision,
        publisherId,
        timeProvider.GetUtcNow(),
        cancellationToken);
}
