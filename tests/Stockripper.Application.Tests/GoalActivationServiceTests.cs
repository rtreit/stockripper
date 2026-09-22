using Stockripper.Application.Goals;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class GoalActivationServiceTests
{
    public static TheoryData<ExecutionMode> ExecutableModes => new()
    {
        ExecutionMode.PAPER_REVIEW,
        ExecutionMode.PAPER_AUTO,
        ExecutionMode.LIVE_REVIEW,
        ExecutionMode.LIVE_AUTO
    };

    [Theory]
    [InlineData(ExecutionMode.REPLAY)]
    [InlineData(ExecutionMode.RESEARCH)]
    public async Task Read_only_goal_activation_is_persisted(ExecutionMode mode)
    {
        var goal = GoalTestData.CreateGoalVersion(mode);
        var persistence = new MemoryActivationPersistence();
        var service = CreateService(goal, persistence);

        var activation = await service.ActivateAsync(
            goal.Id,
            OperatorId.New(),
            "Operator approved the read-only authority summary.");

        Assert.Equal(GoalActivationState.ACTIVE, activation.State);
        Assert.Same(activation, persistence.Current);
    }

    [Theory]
    [MemberData(nameof(ExecutableModes))]
    public async Task Executable_goal_activation_is_blocked_in_phase_zero(ExecutionMode mode)
    {
        var goal = GoalTestData.CreateGoalVersion(mode);
        var persistence = new MemoryActivationPersistence();
        var service = CreateService(goal, persistence);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActivateAsync(
            goal.Id,
            OperatorId.New(),
            "Evidence that must not activate executable authority.").AsTask());

        Assert.Null(persistence.Current);
    }

    [Fact]
    public async Task Paused_activation_cannot_be_resumed_with_a_stale_revision()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var persistence = new MemoryActivationPersistence();
        var service = CreateService(goal, persistence);
        var activation = await service.ActivateAsync(
            goal.Id,
            OperatorId.New(),
            "Operator approved the REPLAY authority summary.");
        _ = await service.PauseAsync(activation.Id, OperatorId.New());
        persistence.ExpectedRevisionOverride = 1;

        await Assert.ThrowsAsync<GoalActivationConcurrencyException>(
            () => service.ResumeAsync(activation.Id, OperatorId.New()).AsTask());
    }

    private static GoalActivationService CreateService(
        GoalVersion goal,
        MemoryActivationPersistence persistence) => new(
        new MemoryGoalPersistence(goal),
        persistence,
        new ManualTimeProvider(GoalTestData.ReferenceTime));

    private sealed class MemoryActivationPersistence : IGoalActivationPersistence
    {
        public GoalActivation? Current { get; private set; }

        public int? ExpectedRevisionOverride { get; set; }

        public ValueTask InsertAsync(
            GoalId goalId,
            GoalActivation activation,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            Current = activation;
            return ValueTask.CompletedTask;
        }

        public ValueTask<GoalActivation?> FindAsync(
            ActivationId activationId,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            Current?.Id == activationId ? Current : null);

        public ValueTask UpdateAsync(
            GoalActivation activation,
            GoalActivationState expectedState,
            int expectedRevision,
            OperatorId actorId,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken)
        {
            var actualRevision = ExpectedRevisionOverride ?? Current?.Revision;
            if (Current is null
                || actualRevision != expectedRevision
                || Current.State != expectedState)
            {
                throw new GoalActivationConcurrencyException();
            }

            Current = activation;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryGoalPersistence(GoalVersion goal) : IGoalPersistence
    {
        public ValueTask<GoalVersion?> FindGoalVersionAsync(
            GoalVersionId goalVersionId,
            CancellationToken cancellationToken) => ValueTask.FromResult<GoalVersion?>(
            goal.Id == goalVersionId ? goal : null);

        public ValueTask InsertDraftAsync(GoalDraft draft, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<GoalDraft?> FindDraftAsync(GoalDraftId draftId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask UpdateDraftAsync(
            GoalDraft revisedDraft,
            int expectedRevision,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<GoalVersion> PublishDraftAsync(
            GoalDraftId draftId,
            int expectedRevision,
            OperatorId publisherId,
            DateTimeOffset publishedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
