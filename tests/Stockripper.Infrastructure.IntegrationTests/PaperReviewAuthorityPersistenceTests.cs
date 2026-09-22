using Npgsql;
using Stockripper.Application.Allocations;
using Stockripper.Application.Brokers;
using Stockripper.Application.Goals;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Infrastructure.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class PaperReviewAuthorityPersistenceTests(PostgresServerFixture server)
{
    [Fact]
    public async Task Concurrent_activation_cannot_overallocate_one_paper_account()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var firstGoal = await PublishPaperGoalAsync(database, clock);
        var secondGoal = await PublishPaperGoalAsync(database, clock);
        var limit = CreateLimit(firstGoal, 15_000m, clock.GetUtcNow());
        var actor = OperatorId.New();
        await CreateService(database, clock, firstGoal).ConfigureAccountLimitAsync(limit, actor);

        var outcomes = await Task.WhenAll(
            TryActivateAsync(database, clock, firstGoal, limit, actor),
            TryActivateAsync(database, clock, secondGoal, limit, actor));

        Assert.Single(outcomes, static outcome => outcome is PaperReviewAuthority);
        var failure = Assert.Single(
            outcomes,
            static outcome => outcome is AccountAllocationConflictException);
        Assert.Equal(
            AccountAllocationFailure.AccountLimitExceeded,
            ((AccountAllocationConflictException)failure).Failure);
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Allocation_is_atomic_recoverable_and_released_with_revocation()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var firstGoal = await PublishPaperGoalAsync(database, clock);
        var secondGoal = await PublishPaperGoalAsync(database, clock);
        var limit = CreateLimit(firstGoal, 15_000m, clock.GetUtcNow());
        var actor = OperatorId.New();
        var service = CreateService(database, clock, firstGoal);
        await service.ConfigureAccountLimitAsync(limit, actor);

        var first = await service.ActivateAsync(
            firstGoal.Id,
            limit.Reference,
            actor,
            "Operator approved the exact PAPER_REVIEW authority summary.");

        var exception = await Assert.ThrowsAsync<AccountAllocationConflictException>(() =>
            CreateService(database, clock, secondGoal).ActivateAsync(
                secondGoal.Id,
                limit.Reference,
                actor,
                "Operator approved the exact PAPER_REVIEW authority summary.").AsTask());
        Assert.Equal(AccountAllocationFailure.AccountLimitExceeded, exception.Failure);

        await database.ReopenApplicationAsync();
        var restoredLimit = await database.AccountAllocationPersistence.FindLimitAsync(
            limit.Reference,
            default);
        var restoredActivation = await database.ActivationPersistence.FindAsync(
            first.Activation.Id,
            default);
        var restoredAllocation = await database.AccountAllocationPersistence.FindByActivationAsync(
            first.Activation.Id,
            default);
        Assert.Equal(limit, restoredLimit);
        Assert.Equal(first.Activation, restoredActivation);
        Assert.Equal(first.Allocation, restoredAllocation);

        clock.Advance(TimeSpan.FromMinutes(1));
        var activationService = new GoalActivationService(
            database.GoalPersistence,
            database.ActivationPersistence,
            clock);
        _ = await activationService.PauseAsync(first.Activation.Id, actor);
        var pausedAllocation = await database.AccountAllocationPersistence.FindByActivationAsync(
            first.Activation.Id,
            default);
        Assert.NotNull(pausedAllocation);
        Assert.Equal(AccountAllocationState.ACTIVE, pausedAllocation.State);
        await Assert.ThrowsAsync<AccountAllocationConflictException>(() =>
            CreateService(database, clock, secondGoal).ActivateAsync(
                secondGoal.Id,
                limit.Reference,
                actor,
                "Evidence cannot reuse a paused Goal allocation.").AsTask());

        clock.Advance(TimeSpan.FromMinutes(1));
        await activationService.RevokeAsync(
            first.Activation.Id,
            actor,
            "Operator revoked paper authority.");
        var released = await database.AccountAllocationPersistence.FindByActivationAsync(
            first.Activation.Id,
            default);
        Assert.NotNull(released);
        Assert.Equal(AccountAllocationState.RELEASED, released.State);
        Assert.Equal(clock.GetUtcNow(), released.EffectiveEnd);

        var second = await CreateService(database, clock, secondGoal).ActivateAsync(
            secondGoal.Id,
            limit.Reference,
            actor,
            "Operator approved the exact PAPER_REVIEW authority summary.");
        Assert.Equal(AccountAllocationState.ACTIVE, second.Allocation.State);
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Database_rejects_changes_to_paper_authority_bindings()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var goal = await PublishPaperGoalAsync(database, clock);
        var limit = CreateLimit(goal, 15_000m, clock.GetUtcNow());
        var actor = OperatorId.New();
        var service = CreateService(database, clock, goal);
        await service.ConfigureAccountLimitAsync(limit, actor);
        _ = await service.ActivateAsync(
            goal.Id,
            limit.Reference,
            actor,
            "Operator approved the exact PAPER_REVIEW authority summary.");

        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "UPDATE account_allocation_limits SET maximum_amount = maximum_amount + 1"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "UPDATE account_allocation_entries SET account_identifier = 'changed'"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "DELETE FROM account_allocation_entries"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "DELETE FROM account_allocation_limits"));
    }

    private static PaperReviewActivationService CreateService(
        PostgresTestDatabase database,
        ManualTimeProvider clock,
        GoalVersion goal) => new(
        database.GoalPersistence,
        new BrokerPreflightService(new FakeBrokerConnectionProbe(CreateObservation(goal, clock.GetUtcNow()))),
        database.AccountAllocationPersistence,
        clock);

    private static BrokerConnectionObservation CreateObservation(
        GoalVersion goal,
        DateTimeOffset observedAt)
    {
        var binding = goal.ExecutionBinding!;
        return new BrokerConnectionObservation(
            binding.ExpectedEnvironment,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            binding.RequiredCapabilities,
            observedAt);
    }

    private static async Task<object> TryActivateAsync(
        PostgresTestDatabase database,
        ManualTimeProvider clock,
        GoalVersion goal,
        AccountAllocationLimit limit,
        OperatorId actor)
    {
        try
        {
            return await CreateService(database, clock, goal).ActivateAsync(
                goal.Id,
                limit.Reference,
                actor,
                "Operator approved the exact PAPER_REVIEW authority summary.");
        }
        catch (AccountAllocationConflictException exception)
        {
            return exception;
        }
    }

    private static async ValueTask<GoalVersion> PublishPaperGoalAsync(
        PostgresTestDatabase database,
        ManualTimeProvider clock)
    {
        var service = new GoalService(database.GoalPersistence, clock);
        var draft = await service.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.PAPER_REVIEW),
            OperatorId.New());
        clock.Advance(TimeSpan.FromSeconds(1));
        return await service.PublishDraftAsync(draft.Id, draft.Revision, OperatorId.New());
    }

    private static AccountAllocationLimit CreateLimit(
        GoalVersion goal,
        decimal amount,
        DateTimeOffset createdAt)
    {
        var binding = goal.ExecutionBinding!;
        return AccountAllocationLimit.Create(
            new VersionReference(
                "paper-account-limit",
                "1",
                $"digest-paper-account-limit-{Guid.NewGuid():N}"),
            binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            new Money(amount, "USD"),
            OperatorId.New(),
            createdAt);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        _ = await command.ExecuteNonQueryAsync();
    }
}
