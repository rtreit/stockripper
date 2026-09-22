using Stockripper.Domain.Common;

namespace Stockripper.Domain.Goals;

public enum GoalDraftState
{
    DRAFT,
    PUBLISHED
}

public sealed record GoalDraft
{
    private GoalDraft(
        GoalDraftId id,
        GoalId goalId,
        GoalVersionId proposedGoalVersionId,
        int versionNumber,
        int revision,
        GoalVersionDefinition definition,
        OperatorId creatorId,
        OperatorId lastEditorId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        GoalDraftState state,
        DateTimeOffset? publishedAt)
    {
        Id = id;
        GoalId = goalId;
        ProposedGoalVersionId = proposedGoalVersionId;
        VersionNumber = versionNumber;
        Revision = revision;
        Definition = definition;
        CreatorId = creatorId;
        LastEditorId = lastEditorId;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        State = state;
        PublishedAt = publishedAt;
    }

    public GoalDraftId Id { get; }

    public GoalId GoalId { get; }

    public GoalVersionId ProposedGoalVersionId { get; }

    public int VersionNumber { get; }

    public int Revision { get; }

    public GoalVersionDefinition Definition { get; }

    public OperatorId CreatorId { get; }

    public OperatorId LastEditorId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private init; }

    public GoalDraftState State { get; private init; }

    public DateTimeOffset? PublishedAt { get; private init; }

    public static GoalDraft Create(
        GoalDraftId id,
        GoalId goalId,
        GoalVersionId proposedGoalVersionId,
        int versionNumber,
        GoalVersionDefinition definition,
        OperatorId creatorId,
        DateTimeOffset createdAt)
    {
        return Restore(
            id,
            goalId,
            proposedGoalVersionId,
            versionNumber,
            1,
            definition,
            creatorId,
            creatorId,
            createdAt,
            createdAt,
            GoalDraftState.DRAFT,
            null);
    }

    public static GoalDraft Restore(
        GoalDraftId id,
        GoalId goalId,
        GoalVersionId proposedGoalVersionId,
        int versionNumber,
        int revision,
        GoalVersionDefinition definition,
        OperatorId creatorId,
        OperatorId lastEditorId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        GoalDraftState state,
        DateTimeOffset? publishedAt)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (id.Value == Guid.Empty
            || goalId.Value == Guid.Empty
            || proposedGoalVersionId.Value == Guid.Empty
            || creatorId.Value == Guid.Empty
            || lastEditorId.Value == Guid.Empty)
        {
            throw new DomainRuleViolationException("The Goal draft contains an empty stable identifier.");
        }

        if (versionNumber <= 0 || revision <= 0)
        {
            throw new DomainRuleViolationException("The Goal draft version and revision must be positive.");
        }

        var checkedCreatedAt = UtcTimestamp.Require(createdAt, nameof(createdAt));
        var checkedUpdatedAt = UtcTimestamp.Require(updatedAt, nameof(updatedAt));
        var checkedPublishedAt = publishedAt is null
            ? (DateTimeOffset?)null
            : UtcTimestamp.Require(publishedAt.Value, nameof(publishedAt));

        if (checkedUpdatedAt < checkedCreatedAt)
        {
            throw new DomainRuleViolationException("The Goal draft update cannot precede creation.");
        }

        if (state == GoalDraftState.PUBLISHED && checkedPublishedAt is null)
        {
            throw new DomainRuleViolationException("A published Goal draft needs a publication time.");
        }

        if (state == GoalDraftState.DRAFT && checkedPublishedAt is not null)
        {
            throw new DomainRuleViolationException("An unpublished Goal draft cannot have a publication time.");
        }

        ValidateCompleteDefinition(
            proposedGoalVersionId,
            goalId,
            versionNumber,
            definition,
            lastEditorId,
            checkedUpdatedAt);

        return new GoalDraft(
            id,
            goalId,
            proposedGoalVersionId,
            versionNumber,
            revision,
            definition,
            creatorId,
            lastEditorId,
            checkedCreatedAt,
            checkedUpdatedAt,
            state,
            checkedPublishedAt);
    }

    public GoalDraft Revise(
        GoalVersionDefinition definition,
        OperatorId editorId,
        DateTimeOffset updatedAt)
    {
        if (State != GoalDraftState.DRAFT)
        {
            throw new DomainRuleViolationException("A published Goal draft cannot change.");
        }

        return Restore(
            Id,
            GoalId,
            ProposedGoalVersionId,
            VersionNumber,
            checked(Revision + 1),
            definition,
            CreatorId,
            editorId,
            CreatedAt,
            updatedAt,
            GoalDraftState.DRAFT,
            null);
    }

    public GoalVersion Publish(OperatorId publisherId, DateTimeOffset publishedAt)
    {
        if (State != GoalDraftState.DRAFT)
        {
            throw new DomainRuleViolationException("The Goal draft is already published.");
        }

        return GoalVersion.Publish(
            ProposedGoalVersionId,
            GoalId,
            VersionNumber,
            Definition,
            publisherId,
            publishedAt);
    }

    public GoalDraft MarkPublished(DateTimeOffset publishedAt)
    {
        if (State != GoalDraftState.DRAFT)
        {
            throw new DomainRuleViolationException("The Goal draft is already published.");
        }

        var checkedPublishedAt = UtcTimestamp.Require(publishedAt, nameof(publishedAt));
        if (checkedPublishedAt < UpdatedAt)
        {
            throw new DomainRuleViolationException("Goal publication cannot precede the last draft update.");
        }

        return this with
        {
            State = GoalDraftState.PUBLISHED,
            PublishedAt = checkedPublishedAt,
            UpdatedAt = checkedPublishedAt
        };
    }

    private static void ValidateCompleteDefinition(
        GoalVersionId proposedGoalVersionId,
        GoalId goalId,
        int versionNumber,
        GoalVersionDefinition definition,
        OperatorId editorId,
        DateTimeOffset updatedAt)
    {
        _ = GoalVersion.Publish(
            proposedGoalVersionId,
            goalId,
            versionNumber,
            definition,
            editorId,
            updatedAt);
    }
}
