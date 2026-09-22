using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class GoalDraftTests
{
    [Fact]
    public void Revision_creates_a_new_draft_revision_without_changing_the_original()
    {
        var creator = OperatorId.New();
        var draft = GoalDraft.Create(
            GoalDraftId.New(),
            GoalId.New(),
            GoalVersionId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.REPLAY),
            creator,
            GoalTestData.ReferenceTime);
        var editor = OperatorId.New();

        var revised = draft.Revise(
            GoalTestData.CreateDefinition(ExecutionMode.REPLAY, "Revised objective."),
            editor,
            GoalTestData.ReferenceTime.AddMinutes(1));

        Assert.Equal(1, draft.Revision);
        Assert.Equal(2, revised.Revision);
        Assert.Equal(creator, draft.LastEditorId);
        Assert.Equal(editor, revised.LastEditorId);
        Assert.NotEqual(draft.Definition.Objective, revised.Definition.Objective);
    }

    [Fact]
    public void Draft_publication_creates_the_proposed_immutable_version()
    {
        var draft = GoalDraft.Create(
            GoalDraftId.New(),
            GoalId.New(),
            GoalVersionId.New(),
            3,
            GoalTestData.CreateDefinition(ExecutionMode.PAPER_REVIEW),
            OperatorId.New(),
            GoalTestData.ReferenceTime);
        var publisher = OperatorId.New();

        var version = draft.Publish(publisher, GoalTestData.ReferenceTime.AddMinutes(1));

        Assert.Equal(draft.ProposedGoalVersionId, version.Id);
        Assert.Equal(draft.GoalId, version.GoalId);
        Assert.Equal(draft.VersionNumber, version.VersionNumber);
        Assert.Equal(publisher, version.CreatorId);
        Assert.True(version.HasValidContentDigest());
    }

    [Fact]
    public void Published_draft_cannot_change_or_publish_again()
    {
        var draft = GoalDraft.Create(
            GoalDraftId.New(),
            GoalId.New(),
            GoalVersionId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.RESEARCH),
            OperatorId.New(),
            GoalTestData.ReferenceTime);
        var published = draft.MarkPublished(GoalTestData.ReferenceTime.AddMinutes(1));

        Assert.Throws<DomainRuleViolationException>(() => published.Revise(
            GoalTestData.CreateDefinition(ExecutionMode.RESEARCH),
            OperatorId.New(),
            GoalTestData.ReferenceTime.AddMinutes(2)));
        Assert.Throws<DomainRuleViolationException>(() => published.Publish(
            OperatorId.New(),
            GoalTestData.ReferenceTime.AddMinutes(2)));
    }
}
