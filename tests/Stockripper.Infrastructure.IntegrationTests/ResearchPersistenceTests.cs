using Stockripper.Application.Data;
using Stockripper.Application.Decisions;
using Stockripper.Application.Goals;
using Stockripper.Application.Research;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;
using Stockripper.Testing;

namespace Stockripper.Infrastructure.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class ResearchPersistenceTests(PostgresServerFixture server)
{
    [Fact]
    public async Task Research_output_and_decision_survive_application_restart()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var goal = await PublishResearchGoalAsync(database, clock);
        var snapshot = await CreateSnapshotAsync(database, clock, goal);
        var activation = await new GoalActivationService(
            database.GoalPersistence,
            database.ActivationPersistence,
            clock).ActivateAsync(
            goal.Id,
            OperatorId.New(),
            "Operator approved the RESEARCH authority summary.");
        var result = await CreateService(database, clock).RunAsync(
            goal.Id,
            activation.Id,
            snapshot.Id,
            OperatorId.New());

        await database.ReopenApplicationAsync();
        var restored = await database.ResearchPersistence.FindAsync(result.Id, default);
        var decisions = await database.DecisionPersistence.FindBySessionAsync(result.SessionId, default);
        var session = await database.SessionPersistence.FindAsync(result.SessionId, default);
        var integrity = await database.AuditVerifier.VerifyAsync();

        Assert.NotNull(restored);
        Assert.Equal(result.ContentDigest, restored.ContentDigest);
        Assert.Equal(snapshot.SourceObservations.ToArray(), restored.SourceObservations.ToArray());
        Assert.Single(decisions);
        Assert.Equal(DecisionState.NO_ACTION, decisions[0].State);
        Assert.NotNull(session);
        Assert.Equal(SessionState.COMPLETED, session.State);
        Assert.True(integrity.IsValid);
        Assert.Equal(10, integrity.VerifiedEventCount);
    }

    private static ResearchService CreateService(
        PostgresTestDatabase database,
        ManualTimeProvider clock) => new(
        database.GoalPersistence,
        database.ActivationPersistence,
        database.SnapshotPersistence,
        new DeterministicReplayStrategyRuntime(),
        database.DecisionPersistence,
        new DecisionGuard(),
        database.ResearchPersistence,
        database.SessionPersistence,
        clock);

    private static async ValueTask<GoalVersion> PublishResearchGoalAsync(
        PostgresTestDatabase database,
        ManualTimeProvider clock)
    {
        var service = new GoalService(database.GoalPersistence, clock);
        var draft = await service.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.RESEARCH),
            OperatorId.New());
        clock.Advance(TimeSpan.FromMinutes(1));
        return await service.PublishDraftAsync(draft.Id, draft.Revision, OperatorId.New());
    }

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
}
