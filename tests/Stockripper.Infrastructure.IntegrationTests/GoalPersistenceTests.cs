using Npgsql;
using Stockripper.Application.Audit;
using Stockripper.Application.Goals;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Infrastructure.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class GoalPersistenceTests(PostgresServerFixture server)
{
    public static TheoryData<ExecutionMode> AllModes => new()
    {
        ExecutionMode.REPLAY,
        ExecutionMode.RESEARCH,
        ExecutionMode.PAPER_REVIEW,
        ExecutionMode.PAPER_AUTO,
        ExecutionMode.LIVE_REVIEW,
        ExecutionMode.LIVE_AUTO
    };

    [Theory]
    [MemberData(nameof(AllModes))]
    public async Task Publication_and_audit_continuity_survive_application_restart(ExecutionMode mode)
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var service = new GoalService(database.GoalPersistence, clock);
        var draft = await service.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(mode),
            OperatorId.New());
        clock.Advance(TimeSpan.FromMinutes(1));

        var published = await service.PublishDraftAsync(draft.Id, draft.Revision, OperatorId.New());
        await database.ReopenApplicationAsync();
        var restored = await database.GoalPersistence.FindGoalVersionAsync(published.Id, default);
        var integrity = await database.AuditVerifier.VerifyAsync();

        Assert.NotNull(restored);
        Assert.Equal(mode, restored.Mode);
        Assert.Equal(published.ContentDigest, restored.ContentDigest);
        Assert.True(restored.HasValidContentDigest());
        Assert.True(integrity.IsValid);
        Assert.Equal(2, integrity.VerifiedEventCount);
    }

    [Fact]
    public async Task Publication_and_audit_continuity_survive_postgresql_restart()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var service = new GoalService(database.GoalPersistence, clock);
        var draft = await service.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.REPLAY),
            OperatorId.New());
        clock.Advance(TimeSpan.FromMinutes(1));
        var published = await service.PublishDraftAsync(draft.Id, draft.Revision, OperatorId.New());

        await server.RestartServerAsync();
        await database.ReopenApplicationAsync();

        var restored = await database.GoalPersistence.FindGoalVersionAsync(published.Id, default);
        var integrity = await database.AuditVerifier.VerifyAsync();
        Assert.NotNull(restored);
        Assert.Equal(published.ContentDigest, restored.ContentDigest);
        Assert.True(integrity.IsValid);
        Assert.Equal(2, integrity.VerifiedEventCount);
    }

    [Fact]
    public async Task Stale_draft_revision_creates_no_audit_event()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var service = new GoalService(database.GoalPersistence, clock);
        var draft = await service.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.RESEARCH),
            OperatorId.New());
        clock.Advance(TimeSpan.FromMinutes(1));
        var revised = await service.ReviseDraftAsync(
            draft.Id,
            draft.Revision,
            GoalTestData.CreateDefinition(ExecutionMode.RESEARCH, "Revised research objective."),
            OperatorId.New());

        await Assert.ThrowsAsync<GoalDraftConcurrencyException>(() => service.ReviseDraftAsync(
            draft.Id,
            draft.Revision,
            GoalTestData.CreateDefinition(ExecutionMode.RESEARCH, "Stale revision objective."),
            OperatorId.New()).AsTask());

        var integrity = await database.AuditVerifier.VerifyAsync();
        Assert.Equal(2, revised.Revision);
        Assert.True(integrity.IsValid);
        Assert.Equal(2, integrity.VerifiedEventCount);
    }

    [Fact]
    public async Task Concurrent_publication_creates_one_version_and_one_publication_event()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var service = new GoalService(database.GoalPersistence, clock);
        var draft = await service.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.PAPER_AUTO),
            OperatorId.New());
        clock.Advance(TimeSpan.FromMinutes(1));

        var attempts = await Task.WhenAll(
            TryPublishAsync(service, draft, OperatorId.New()),
            TryPublishAsync(service, draft, OperatorId.New()));

        var integrity = await database.AuditVerifier.VerifyAsync();
        Assert.Single(attempts, result => result is not null);
        Assert.Equal(1, await ScalarLongAsync(database.DataSource, "SELECT count(*) FROM goal_versions"));
        Assert.True(integrity.IsValid);
        Assert.Equal(2, integrity.VerifiedEventCount);
    }

    [Fact]
    public async Task Missing_audit_head_rolls_back_goal_publication()
    {
        await using var database = await server.CreateDatabaseAsync();
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var service = new GoalService(database.GoalPersistence, clock);
        var draft = await service.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.LIVE_REVIEW),
            OperatorId.New());
        await ExecuteAsync(database.DataSource, "DELETE FROM audit_chain_heads");
        clock.Advance(TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PublishDraftAsync(
            draft.Id,
            draft.Revision,
            OperatorId.New()).AsTask());

        var restoredDraft = await database.GoalPersistence.FindDraftAsync(draft.Id, default);
        var restoredVersion = await database.GoalPersistence.FindGoalVersionAsync(
            draft.ProposedGoalVersionId,
            default);
        var integrity = await database.AuditVerifier.VerifyAsync();

        Assert.NotNull(restoredDraft);
        Assert.Equal(GoalDraftState.DRAFT, restoredDraft.State);
        Assert.Null(restoredVersion);
        Assert.Equal(0, await ScalarLongAsync(database.DataSource, "SELECT count(*) FROM goal_versions"));
        Assert.False(integrity.IsValid);
        Assert.Equal("HEAD_MISSING", integrity.FailureCode);
    }

    [Fact]
    public async Task Audit_events_reject_update_delete_and_truncate()
    {
        await using var database = await server.CreateDatabaseAsync();
        var service = new GoalService(
            database.GoalPersistence,
            new ManualTimeProvider(GoalTestData.ReferenceTime));
        _ = await service.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.REPLAY),
            OperatorId.New());

        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database.DataSource, "UPDATE audit_events SET reason = 'changed'"));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database.DataSource, "DELETE FROM audit_events"));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database.DataSource, "TRUNCATE audit_events"));

        var integrity = await database.AuditVerifier.VerifyAsync();
        Assert.True(integrity.IsValid);
        Assert.Equal(1, integrity.VerifiedEventCount);
    }

    [Fact]
    public async Task Goal_versions_reject_update_delete_and_truncate()
    {
        await using var database = await server.CreateDatabaseAsync();
        var service = new GoalService(
            database.GoalPersistence,
            new ManualTimeProvider(GoalTestData.ReferenceTime));
        var draft = await service.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.LIVE_AUTO),
            OperatorId.New());
        var published = await service.PublishDraftAsync(
            draft.Id,
            draft.Revision,
            OperatorId.New());

        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database.DataSource, "UPDATE goal_versions SET mode = 'REPLAY'"));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database.DataSource, "DELETE FROM goal_versions"));
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(database.DataSource, "TRUNCATE goal_versions"));

        var restored = await database.GoalPersistence.FindGoalVersionAsync(published.Id, default);
        var integrity = await database.AuditVerifier.VerifyAsync();
        Assert.NotNull(restored);
        Assert.Equal(ExecutionMode.LIVE_AUTO, restored.Mode);
        Assert.Equal(published.ContentDigest, restored.ContentDigest);
        Assert.True(integrity.IsValid);
    }

    [Fact]
    public async Task Privileged_tamper_is_detected_and_blocks_the_integrity_guard()
    {
        await using var database = await server.CreateDatabaseAsync();
        var service = new GoalService(
            database.GoalPersistence,
            new ManualTimeProvider(GoalTestData.ReferenceTime));
        _ = await service.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(ExecutionMode.REPLAY),
            OperatorId.New());
        await ExecuteAsync(
            database.DataSource,
            """
            ALTER TABLE audit_events DISABLE TRIGGER USER;
            UPDATE audit_events SET reason = 'changed';
            ALTER TABLE audit_events ENABLE TRIGGER USER;
            """);

        var integrity = await database.AuditVerifier.VerifyAsync();
        var guard = new AuditIntegrityGuard(database.AuditVerifier);

        Assert.False(integrity.IsValid);
        Assert.Equal("PAYLOAD_MISMATCH", integrity.FailureCode);
        var exception = await Assert.ThrowsAsync<AuditIntegrityException>(
            () => guard.VerifyOrThrowAsync().AsTask());
        Assert.Equal("PAYLOAD_MISMATCH", exception.FailureCode);
    }

    private static async Task<GoalVersion?> TryPublishAsync(
        GoalService service,
        GoalDraft draft,
        OperatorId publisherId)
    {
        try
        {
            return await service.PublishDraftAsync(draft.Id, draft.Revision, publisherId);
        }
        catch (GoalDraftAlreadyPublishedException)
        {
            return null;
        }
        catch (GoalDraftConcurrencyException)
        {
            return null;
        }
    }

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
