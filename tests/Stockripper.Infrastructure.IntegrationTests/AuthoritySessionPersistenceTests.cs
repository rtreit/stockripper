using Npgsql;
using Stockripper.Application.Data;
using Stockripper.Application.Goals;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;
using Stockripper.Testing;

namespace Stockripper.Infrastructure.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class AuthoritySessionPersistenceTests(PostgresServerFixture server)
{
    [Fact]
    public async Task Concurrent_activation_allows_only_one_active_authority_for_a_goal()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var goal = await PublishGoalAsync(database, clock, ExecutionMode.REPLAY);
        var service = new GoalActivationService(
            database.GoalPersistence,
            database.ActivationPersistence,
            clock);

        var attempts = new[]
        {
            TryActivateAsync(service, goal.Id),
            TryActivateAsync(service, goal.Id)
        };
        var outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes, static outcome => outcome is GoalActivation);
        Assert.Single(outcomes, static outcome => outcome is GoalActivationConflictException);
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Interrupted_replay_session_becomes_failed_after_application_restart()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var goal = await PublishGoalAsync(database, clock, ExecutionMode.REPLAY);
        var snapshot = await CreateSnapshotAsync(database, clock, goal);
        var activation = await ActivateAsync(database, clock, goal);
        var actor = OperatorId.New();
        var created = Session.Create(
            SessionId.New(),
            goal,
            activation,
            clock.GetUtcNow(),
            snapshot.Id,
            77);
        await database.SessionPersistence.InsertAsync(created, actor, default);
        var preflight = created.TransitionTo(SessionState.PREFLIGHT, clock.GetUtcNow());
        await database.SessionPersistence.UpdateAsync(
            preflight,
            created.State,
            created.Revision,
            actor,
            clock.GetUtcNow(),
            default);
        var ready = preflight.TransitionTo(SessionState.READY, clock.GetUtcNow());
        await database.SessionPersistence.UpdateAsync(
            ready,
            preflight.State,
            preflight.Revision,
            actor,
            clock.GetUtcNow(),
            default);
        var running = ready.TransitionTo(SessionState.RUNNING, clock.GetUtcNow());
        await database.SessionPersistence.UpdateAsync(
            running,
            ready.State,
            ready.Revision,
            actor,
            clock.GetUtcNow(),
            default);

        await database.ReopenApplicationAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await new ReadOnlySessionRecoveryService(database.SessionPersistence, clock)
            .RecoverAsync(actor);
        var restored = await database.SessionPersistence.FindAsync(created.Id, default);

        Assert.Equal(1, recovered);
        Assert.NotNull(restored);
        Assert.Equal(SessionState.FAILED, restored.State);
        Assert.Equal(clock.GetUtcNow(), restored.EndTime);
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Database_rejects_changes_to_authority_and_session_bindings()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var goal = await PublishGoalAsync(database, clock, ExecutionMode.REPLAY);
        var snapshot = await CreateSnapshotAsync(database, clock, goal);
        var activation = await ActivateAsync(database, clock, goal);
        var session = Session.Create(
            SessionId.New(),
            goal,
            activation,
            clock.GetUtcNow(),
            snapshot.Id,
            91);
        await database.SessionPersistence.InsertAsync(session, OperatorId.New(), default);

        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "UPDATE goal_activations SET activation_evidence = 'changed'"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "UPDATE sessions SET mode = 'RESEARCH', revision = revision + 1"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "DELETE FROM sessions"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "DELETE FROM goal_activations"));
        var mismatched = Session.Restore(
            SessionId.New(),
            goal.Id,
            activation.Id,
            ExecutionMode.RESEARCH,
            clock.GetUtcNow(),
            SessionState.CREATED,
            null,
            null,
            snapshot.Id,
            null,
            1);
        await Assert.ThrowsAsync<PostgresException>(() => database.SessionPersistence
            .InsertAsync(mismatched, OperatorId.New(), default)
            .AsTask());
    }

    private static async Task<object> TryActivateAsync(
        GoalActivationService service,
        GoalVersionId goalVersionId)
    {
        try
        {
            return await service.ActivateAsync(
                goalVersionId,
                OperatorId.New(),
                "Operator approved the REPLAY authority summary.");
        }
        catch (GoalActivationConflictException exception)
        {
            return exception;
        }
    }

    private static async ValueTask<GoalVersion> PublishGoalAsync(
        PostgresTestDatabase database,
        ManualTimeProvider clock,
        ExecutionMode mode)
    {
        var service = new GoalService(database.GoalPersistence, clock);
        var draft = await service.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(mode),
            OperatorId.New());
        clock.Advance(TimeSpan.FromMinutes(1));
        return await service.PublishDraftAsync(draft.Id, draft.Revision, OperatorId.New());
    }

    private static ValueTask<GoalActivation> ActivateAsync(
        PostgresTestDatabase database,
        ManualTimeProvider clock,
        GoalVersion goal) => new GoalActivationService(
        database.GoalPersistence,
        database.ActivationPersistence,
        clock).ActivateAsync(
        goal.Id,
        OperatorId.New(),
        "Operator approved the REPLAY authority summary.");

    private static async ValueTask<DataSnapshot> CreateSnapshotAsync(
        PostgresTestDatabase database,
        ManualTimeProvider clock,
        GoalVersion goal)
    {
        var expected = GoalTestData.CreateDataSnapshot(goal);
        return await new DataSnapshotService(database.SnapshotPersistence, clock).CreateAsync(
            expected.DataSetVersion,
            goal.DataPolicyVersion,
            expected.SchemaVersion,
            expected.RangeStart,
            expected.RangeEnd,
            expected.SourceObservations,
            expected.Payload.ToArray(),
            OperatorId.New());
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        _ = await command.ExecuteNonQueryAsync();
    }
}
