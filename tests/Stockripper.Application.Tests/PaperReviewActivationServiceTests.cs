using Stockripper.Application.Allocations;
using Stockripper.Application.Brokers;
using Stockripper.Application.Goals;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class PaperReviewActivationServiceTests
{
    [Theory]
    [InlineData(ExecutionMode.PAPER_REVIEW)]
    [InlineData(ExecutionMode.PAPER_AUTO)]
    public async Task Exact_paper_binding_reserves_allocation_and_activates_goal(
        ExecutionMode mode)
    {
        var goal = GoalTestData.CreateGoalVersion(mode);
        var observation = GoalTestData.CreateObservation(goal);
        var persistence = new MemoryAccountAllocationPersistence(CreateLimit(goal, 25_000m));
        var service = CreateService(goal, observation, persistence);

        var authority = await service.ActivateAsync(
            goal.Id,
            persistence.Limit.Reference,
            OperatorId.New(),
            "Operator approved the exact PAPER_REVIEW authority summary.");

        Assert.Equal(GoalActivationState.ACTIVE, authority.Activation.State);
        Assert.Equal(AccountAllocationState.ACTIVE, authority.Allocation.State);
        Assert.Equal(goal.CapitalAllocation, authority.Allocation.Allocation);
        Assert.Equal(1, persistence.ReservationCount);
    }

    [Fact]
    public async Task Wrong_account_prevents_allocation_and_activation()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var binding = goal.ExecutionBinding!;
        var observation = new BrokerConnectionObservation(
            BrokerEnvironment.PAPER,
            binding.EndpointIdentity,
            "wrong-paper-account",
            binding.RequiredCapabilities,
            GoalTestData.ReferenceTime);
        var persistence = new MemoryAccountAllocationPersistence(CreateLimit(goal, 25_000m));
        var service = CreateService(goal, observation, persistence);

        var exception = await Assert.ThrowsAsync<BrokerPreflightException>(() => service.ActivateAsync(
            goal.Id,
            persistence.Limit.Reference,
            OperatorId.New(),
            "Evidence that cannot grant authority.").AsTask());

        Assert.Equal(BrokerPreflightFailure.AccountMismatch, exception.Failure);
        Assert.Equal(0, persistence.ReservationCount);
    }

    [Theory]
    [InlineData(ExecutionMode.REPLAY)]
    [InlineData(ExecutionMode.RESEARCH)]
    [InlineData(ExecutionMode.LIVE_REVIEW)]
    [InlineData(ExecutionMode.LIVE_AUTO)]
    public async Task Only_paper_modes_can_use_this_activation_boundary(ExecutionMode mode)
    {
        var goal = GoalTestData.CreateGoalVersion(mode);
        var paperGoal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var persistence = new MemoryAccountAllocationPersistence(CreateLimit(paperGoal, 25_000m));
        var service = CreateService(goal, GoalTestData.CreateObservation(paperGoal), persistence);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActivateAsync(
            goal.Id,
            persistence.Limit.Reference,
            OperatorId.New(),
            "Evidence that cannot widen the mode boundary.").AsTask());

        Assert.Equal(0, persistence.ReservationCount);
    }

    [Fact]
    public async Task Allocation_above_account_limit_fails_before_authority_is_stored()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var persistence = new MemoryAccountAllocationPersistence(CreateLimit(goal, 5_000m));
        var service = CreateService(goal, GoalTestData.CreateObservation(goal), persistence);

        var exception = await Assert.ThrowsAsync<AccountAllocationConflictException>(
            () => service.ActivateAsync(
                goal.Id,
                persistence.Limit.Reference,
                OperatorId.New(),
                "Evidence that cannot exceed the allocation limit.").AsTask());

        Assert.Equal(AccountAllocationFailure.AccountLimitExceeded, exception.Failure);
        Assert.Equal(0, persistence.ReservationCount);
    }

    private static PaperReviewActivationService CreateService(
        GoalVersion goal,
        BrokerConnectionObservation observation,
        MemoryAccountAllocationPersistence persistence) => new(
        new MemoryGoalPersistence(goal),
        new BrokerPreflightService(new FakeBrokerConnectionProbe(observation)),
        persistence,
        new ManualTimeProvider(GoalTestData.ReferenceTime));

    private static AccountAllocationLimit CreateLimit(GoalVersion goal, decimal amount)
    {
        var binding = goal.ExecutionBinding!;
        return AccountAllocationLimit.Create(
            new VersionReference("paper-account-limit", "1", "digest-paper-account-limit-1"),
            binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            new Money(amount, "USD"),
            OperatorId.New(),
            GoalTestData.ReferenceTime);
    }

    private sealed class MemoryAccountAllocationPersistence(AccountAllocationLimit limit)
        : IAccountAllocationPersistence
    {
        public AccountAllocationLimit Limit { get; } = limit;
        public int ReservationCount { get; private set; }

        public ValueTask InsertLimitAsync(
            AccountAllocationLimit accountLimit,
            OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<AccountAllocationLimit?> FindLimitAsync(
            VersionReference limitVersion,
            CancellationToken cancellationToken) => ValueTask.FromResult<AccountAllocationLimit?>(
            limitVersion == Limit.Reference ? Limit : null);

        public ValueTask ReserveAndActivateAsync(
            GoalId goalId,
            GoalActivation activation,
            AccountAllocationEntry allocation,
            BrokerConnectionObservation observation,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            if (allocation.Allocation.Amount > Limit.MaximumAllocation.Amount)
            {
                throw new AccountAllocationConflictException(
                    AccountAllocationFailure.AccountLimitExceeded);
            }

            ReservationCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<AccountAllocationEntry?> FindByActivationAsync(
            ActivationId activationId,
            CancellationToken cancellationToken) => ValueTask.FromResult<AccountAllocationEntry?>(null);
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
