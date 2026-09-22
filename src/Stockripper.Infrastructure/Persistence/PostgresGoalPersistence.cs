using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Goals;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresGoalPersistence(NpgsqlDataSource dataSource) : IGoalPersistence
{
    public async ValueTask InsertDraftAsync(
        GoalDraft draft,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            const string sql = """
                INSERT INTO goal_drafts (
                    draft_id, goal_id, proposed_goal_version_id, version_number,
                    revision, state, definition_json, creator_id, last_editor_id,
                    created_at, updated_at, published_at)
                VALUES (
                    @draft_id, @goal_id, @proposed_goal_version_id, @version_number,
                    @revision, @state, @definition_json, @creator_id, @last_editor_id,
                    @created_at, @updated_at, NULL)
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddDraftParameters(command, draft);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            _ = await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.GOAL_DRAFT_CREATED,
                draft.CreatedAt,
                draft.CreatorId,
                null,
                null,
                RelatedIdentifiers(draft),
                null,
                GoalDraftState.DRAFT.ToString(),
                "Goal draft created.",
                "goal-service",
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<GoalDraft?> FindDraftAsync(
        GoalDraftId draftId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT draft_id, goal_id, proposed_goal_version_id, version_number,
                   revision, state, definition_json::text, creator_id, last_editor_id,
                   created_at, updated_at, published_at
            FROM goal_drafts
            WHERE draft_id = @draft_id
            """,
            connection);
        command.Parameters.AddWithValue("draft_id", draftId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDraft(reader)
            : null;
    }

    public async ValueTask UpdateDraftAsync(
        GoalDraft revisedDraft,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            const string sql = """
                UPDATE goal_drafts
                SET revision = @revision,
                    definition_json = @definition_json,
                    last_editor_id = @last_editor_id,
                    updated_at = @updated_at
                WHERE draft_id = @draft_id
                  AND revision = @expected_revision
                  AND state = 'DRAFT'
                """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("revision", revisedDraft.Revision);
            command.Parameters.Add(
                "definition_json",
                NpgsqlDbType.Jsonb).Value = GoalDefinitionSerializer.Serialize(revisedDraft.Definition);
            command.Parameters.AddWithValue("last_editor_id", revisedDraft.LastEditorId.Value);
            command.Parameters.AddWithValue("updated_at", revisedDraft.UpdatedAt);
            command.Parameters.AddWithValue("draft_id", revisedDraft.Id.Value);
            command.Parameters.AddWithValue("expected_revision", expectedRevision);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new GoalDraftConcurrencyException();
            }

            _ = await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.GOAL_DRAFT_UPDATED,
                revisedDraft.UpdatedAt,
                revisedDraft.LastEditorId,
                null,
                null,
                RelatedIdentifiers(revisedDraft),
                GoalDraftState.DRAFT.ToString(),
                GoalDraftState.DRAFT.ToString(),
                "Goal draft updated.",
                "goal-service",
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<GoalVersion> PublishDraftAsync(
        GoalDraftId draftId,
        int expectedRevision,
        OperatorId publisherId,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var draft = await ReadDraftForUpdateAsync(
                connection,
                transaction,
                draftId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new GoalDraftNotFoundException();

            if (draft.State == GoalDraftState.PUBLISHED)
            {
                throw new GoalDraftAlreadyPublishedException();
            }

            if (draft.Revision != expectedRevision)
            {
                throw new GoalDraftConcurrencyException();
            }

            var version = draft.Publish(publisherId, publishedAt);
            await InsertGoalVersionAsync(
                connection,
                transaction,
                version,
                cancellationToken).ConfigureAwait(false);
            await MarkDraftPublishedAsync(
                connection,
                transaction,
                draft,
                publishedAt,
                cancellationToken).ConfigureAwait(false);

            _ = await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.GOAL_VERSION_PUBLISHED,
                publishedAt,
                publisherId,
                version.Id,
                null,
                RelatedIdentifiers(draft),
                GoalDraftState.DRAFT.ToString(),
                GoalDraftState.PUBLISHED.ToString(),
                "Goal version published.",
                "goal-publication-service",
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return version;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<GoalVersion?> FindGoalVersionAsync(
        GoalVersionId goalVersionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT goal_version_id, goal_id, version_number, mode, content_digest,
                   definition_json::text, creator_id, created_at
            FROM goal_versions
            WHERE goal_version_id = @goal_version_id
            """,
            connection);
        command.Parameters.AddWithValue("goal_version_id", goalVersionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadGoalVersion(reader)
            : null;
    }

    private static void AddDraftParameters(NpgsqlCommand command, GoalDraft draft)
    {
        command.Parameters.AddWithValue("draft_id", draft.Id.Value);
        command.Parameters.AddWithValue("goal_id", draft.GoalId.Value);
        command.Parameters.AddWithValue("proposed_goal_version_id", draft.ProposedGoalVersionId.Value);
        command.Parameters.AddWithValue("version_number", draft.VersionNumber);
        command.Parameters.AddWithValue("revision", draft.Revision);
        command.Parameters.AddWithValue("state", draft.State.ToString());
        command.Parameters.Add("definition_json", NpgsqlDbType.Jsonb).Value =
            GoalDefinitionSerializer.Serialize(draft.Definition);
        command.Parameters.AddWithValue("creator_id", draft.CreatorId.Value);
        command.Parameters.AddWithValue("last_editor_id", draft.LastEditorId.Value);
        command.Parameters.AddWithValue("created_at", draft.CreatedAt);
        command.Parameters.AddWithValue("updated_at", draft.UpdatedAt);
    }

    private static async ValueTask<GoalDraft?> ReadDraftForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GoalDraftId draftId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT draft_id, goal_id, proposed_goal_version_id, version_number,
                   revision, state, definition_json::text, creator_id, last_editor_id,
                   created_at, updated_at, published_at
            FROM goal_drafts
            WHERE draft_id = @draft_id
            FOR UPDATE
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("draft_id", draftId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDraft(reader)
            : null;
    }

    private static GoalDraft ReadDraft(NpgsqlDataReader reader) => GoalDraft.Restore(
        new GoalDraftId(reader.GetGuid(0)),
        new GoalId(reader.GetGuid(1)),
        new GoalVersionId(reader.GetGuid(2)),
        reader.GetInt32(3),
        reader.GetInt32(4),
        GoalDefinitionSerializer.Deserialize(reader.GetString(6)),
        new OperatorId(reader.GetGuid(7)),
        new OperatorId(reader.GetGuid(8)),
        reader.GetFieldValue<DateTimeOffset>(9),
        reader.GetFieldValue<DateTimeOffset>(10),
        Enum.Parse<GoalDraftState>(reader.GetString(5), ignoreCase: false),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11));

    private static async ValueTask InsertGoalVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GoalVersion version,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO goal_versions (
                goal_version_id, goal_id, version_number, mode, content_digest,
                definition_json, creator_id, created_at)
            VALUES (
                @goal_version_id, @goal_id, @version_number, @mode, @content_digest,
                @definition_json, @creator_id, @created_at)
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("goal_version_id", version.Id.Value);
        command.Parameters.AddWithValue("goal_id", version.GoalId.Value);
        command.Parameters.AddWithValue("version_number", version.VersionNumber);
        command.Parameters.AddWithValue("mode", version.Mode.ToWireValue());
        command.Parameters.AddWithValue("content_digest", version.ContentDigest);
        command.Parameters.Add("definition_json", NpgsqlDbType.Jsonb).Value =
            GoalDefinitionSerializer.Serialize(ToDefinition(version));
        command.Parameters.AddWithValue("creator_id", version.CreatorId.Value);
        command.Parameters.AddWithValue("created_at", version.CreatedAt);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask MarkDraftPublishedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GoalDraft draft,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE goal_drafts
            SET state = 'PUBLISHED', published_at = @published_at, updated_at = @published_at
            WHERE draft_id = @draft_id AND revision = @revision AND state = 'DRAFT'
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("published_at", publishedAt);
        command.Parameters.AddWithValue("draft_id", draft.Id.Value);
        command.Parameters.AddWithValue("revision", draft.Revision);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new GoalDraftConcurrencyException();
        }
    }

    private static GoalVersion ReadGoalVersion(NpgsqlDataReader reader)
    {
        var definition = GoalDefinitionSerializer.Deserialize(reader.GetString(5));
        var version = GoalVersion.Publish(
            new GoalVersionId(reader.GetGuid(0)),
            new GoalId(reader.GetGuid(1)),
            reader.GetInt32(2),
            definition,
            new OperatorId(reader.GetGuid(6)),
            reader.GetFieldValue<DateTimeOffset>(7));

        if (!string.Equals(reader.GetString(3), version.Mode.ToWireValue(), StringComparison.Ordinal)
            || !string.Equals(reader.GetString(4), version.ContentDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored Goal version integrity check failed.");
        }

        return version;
    }

    private static GoalVersionDefinition ToDefinition(GoalVersion version) => new(
        version.Mode,
        version.Objective,
        version.InstrumentUniverse,
        version.StrategyVersion,
        version.DataPolicyVersion,
        version.RiskPolicyVersion,
        version.CapitalAllocation,
        version.ExecutionSchedule,
        version.DecisionSchedule,
        version.OutputCriteria,
        version.EvaluationCriteria,
        version.ExecutionBinding);

    private static Dictionary<string, string> RelatedIdentifiers(GoalDraft draft) => new(
        StringComparer.Ordinal)
    {
        ["goalDraftId"] = draft.Id.Value.ToString("D"),
        ["goalId"] = draft.GoalId.Value.ToString("D"),
        ["proposedGoalVersionId"] = draft.ProposedGoalVersionId.Value.ToString("D")
    };
}
