using Npgsql;
using Stockripper.Application.Data;
using Stockripper.Application.Decisions;
using Stockripper.Application.Goals;
using Stockripper.Application.Replay;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Infrastructure.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class ReplayPersistenceTests(PostgresServerFixture server)
{
    [Fact]
    public async Task Same_snapshot_and_seed_produce_the_same_output_across_application_restart()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var goal = await PublishReplayGoalAsync(database, clock);
        var snapshot = await CreateSnapshotAsync(database, clock, goal);
        var activation = await ActivateAsync(database, clock, goal);

        var first = await CreateReplayService(database, clock).RunAsync(
            goal.Id,
            activation.Id,
            snapshot.Id,
            42,
            OperatorId.New());
        await database.ReopenApplicationAsync();
        var second = await CreateReplayService(database, clock).RunAsync(
            goal.Id,
            activation.Id,
            snapshot.Id,
            42,
            OperatorId.New());

        Assert.Equal(first.OutputDigest, second.OutputDigest);
        Assert.Equal(first.OutputMetrics, second.OutputMetrics);
        var restoredSnapshot = await database.SnapshotPersistence.FindAsync(snapshot.Id, default);
        var restoredResult = await database.ReplayPersistence.FindAsync(first.Id, default);
        var integrity = await database.AuditVerifier.VerifyAsync();
        Assert.NotNull(restoredSnapshot);
        Assert.True(restoredSnapshot.HasValidContentDigest());
        Assert.NotNull(restoredResult);
        Assert.Equal(first.OutputDigest, restoredResult.OutputDigest);
        Assert.True(integrity.IsValid);
        Assert.Equal(16, integrity.VerifiedEventCount);
    }

    [Fact]
    public async Task Snapshot_and_replay_survive_postgresql_restart()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var goal = await PublishReplayGoalAsync(database, clock);
        var snapshot = await CreateSnapshotAsync(database, clock, goal);
        var activation = await ActivateAsync(database, clock, goal);
        var result = await CreateReplayService(database, clock).RunAsync(
            goal.Id,
            activation.Id,
            snapshot.Id,
            7,
            OperatorId.New());

        await server.RestartServerAsync();
        await database.ReopenApplicationAsync();

        var restoredSnapshot = await database.SnapshotPersistence.FindAsync(snapshot.Id, default);
        var restoredResult = await database.ReplayPersistence.FindAsync(result.Id, default);
        var integrity = await database.AuditVerifier.VerifyAsync();
        Assert.NotNull(restoredSnapshot);
        Assert.NotNull(restoredResult);
        Assert.Equal(result.OutputDigest, restoredResult.OutputDigest);
        Assert.True(integrity.IsValid);
        Assert.Equal(10, integrity.VerifiedEventCount);
    }

    [Fact]
    public async Task Missing_audit_head_rolls_back_replay_result()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var goal = await PublishReplayGoalAsync(database, clock);
        var snapshot = await CreateSnapshotAsync(database, clock, goal);
        var activation = await ActivateAsync(database, clock, goal);
        await ExecuteAsync(database.DataSource, "DELETE FROM audit_chain_heads");

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateReplayService(database, clock)
            .RunAsync(
                goal.Id,
                activation.Id,
                snapshot.Id,
                42,
                OperatorId.New())
            .AsTask());

        Assert.Equal(0, await ScalarLongAsync(database.DataSource, "SELECT count(*) FROM replay_results"));
    }

    [Fact]
    public async Task Snapshot_and_replay_records_reject_mutation()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var goal = await PublishReplayGoalAsync(database, clock);
        var snapshot = await CreateSnapshotAsync(database, clock, goal);
        var activation = await ActivateAsync(database, clock, goal);
        _ = await CreateReplayService(database, clock).RunAsync(
            goal.Id,
            activation.Id,
            snapshot.Id,
            42,
            OperatorId.New());

        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database.DataSource, "UPDATE data_snapshots SET schema_version = 'changed'"));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database.DataSource, "DELETE FROM data_snapshots"));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database.DataSource, "TRUNCATE data_snapshots"));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database.DataSource, "UPDATE replay_results SET evidence = 'changed'"));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database.DataSource, "DELETE FROM replay_results"));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database.DataSource, "TRUNCATE replay_results"));

        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Privileged_snapshot_tamper_is_detected_on_read()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var goal = await PublishReplayGoalAsync(database, clock);
        var snapshot = await CreateSnapshotAsync(database, clock, goal);
        await ExecuteAsync(
            database.DataSource,
            """
            ALTER TABLE data_snapshots DISABLE TRIGGER USER;
            UPDATE data_snapshots SET payload = decode('00', 'hex');
            ALTER TABLE data_snapshots ENABLE TRIGGER USER;
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => database.SnapshotPersistence
            .FindAsync(snapshot.Id, default)
            .AsTask());
    }

    private static async ValueTask<GoalVersion> PublishReplayGoalAsync(
        PostgresTestDatabase database,
        ManualTimeProvider clock)
    {
        var service = new GoalService(database.GoalPersistence, clock);
        var draft = await service.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.REPLAY),
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
        var service = new DataSnapshotService(database.SnapshotPersistence, clock);
        return await service.CreateAsync(
            expected.DataSetVersion,
            goal.DataPolicyVersion,
            expected.SchemaVersion,
            expected.RangeStart,
            expected.RangeEnd,
            expected.SourceObservations,
            expected.Payload.ToArray(),
            OperatorId.New());
    }

    private static ReplayService CreateReplayService(
        PostgresTestDatabase database,
        ManualTimeProvider clock) => new(
        database.GoalPersistence,
        database.ActivationPersistence,
        database.SnapshotPersistence,
        new DeterministicReplayStrategyRuntime(),
        database.DecisionPersistence,
        new DecisionGuard(),
        database.ReplayPersistence,
        database.SessionPersistence,
        clock);

    private static ValueTask<GoalActivation> ActivateAsync(
        PostgresTestDatabase database,
        ManualTimeProvider clock,
        GoalVersion goal) => new GoalActivationService(
        database.GoalPersistence,
        database.ActivationPersistence,
        clock).ActivateAsync(
        goal.Id,
        OperatorId.New(),
        "Operator approved the REPLAY Goal authority summary.");

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The scalar query returned no value."));
    }
}
