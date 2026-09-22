using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Goals;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresGoalActivationPersistence(NpgsqlDataSource dataSource)
    : IGoalActivationPersistence
{
    public async ValueTask InsertAsync(
        GoalId goalId,
        GoalActivation activation,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO goal_activations (
                    activation_id, goal_id, goal_version_id, operator_id, state,
                    activation_evidence, authority_summary, activation_time, expiry_time,
                    revocation_time, revocation_reason, revision)
                VALUES (
                    @activation_id, @goal_id, @goal_version_id, @operator_id, @state,
                    @activation_evidence, @authority_summary, @activation_time, @expiry_time,
                    NULL, NULL, @revision)
                """,
                connection,
                transaction);
            AddParameters(command, goalId, activation);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _ = await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.GOAL_ACTIVATED,
                activation.ActivationTime,
                actorId,
                activation.GoalVersionId,
                null,
                RelatedIdentifiers(activation),
                GoalActivationState.INACTIVE.ToString(),
                activation.State.ToString(),
                "Goal activated.",
                "goal-activation-service",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new GoalActivationConflictException();
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<GoalActivation?> FindAsync(
        ActivationId activationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT goal_version_id, operator_id, state, activation_evidence,
                   authority_summary, activation_time, expiry_time, revocation_time,
                   revocation_reason, revision
            FROM goal_activations
            WHERE activation_id = @activation_id
            """,
            connection);
        command.Parameters.AddWithValue("activation_id", activationId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? GoalActivation.Restore(
                activationId,
                new GoalVersionId(reader.GetGuid(0)),
                new OperatorId(reader.GetGuid(1)),
                Enum.Parse<GoalActivationState>(reader.GetString(2), ignoreCase: false),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt32(9))
            : null;
    }

    public async ValueTask UpdateAsync(
        GoalActivation activation,
        GoalActivationState expectedState,
        int expectedRevision,
        OperatorId actorId,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE goal_activations
                SET state = @state, revocation_time = @revocation_time,
                    revocation_reason = @revocation_reason, revision = @revision
                WHERE activation_id = @activation_id
                  AND state = @expected_state
                  AND revision = @expected_revision
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("state", activation.State.ToString());
            command.Parameters.AddWithValue(
                "revocation_time",
                activation.RevocationTime is null ? DBNull.Value : activation.RevocationTime.Value);
            command.Parameters.AddWithValue(
                "revocation_reason",
                activation.RevocationReason is null ? DBNull.Value : activation.RevocationReason);
            command.Parameters.AddWithValue("revision", activation.Revision);
            command.Parameters.AddWithValue("activation_id", activation.Id.Value);
            command.Parameters.AddWithValue("expected_state", expectedState.ToString());
            command.Parameters.AddWithValue("expected_revision", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new GoalActivationConcurrencyException();
            }

            var releasedAllocationId = activation.State is GoalActivationState.REVOKED
                or GoalActivationState.EXPIRED
                ? await ReleaseAllocationAsync(
                    connection,
                    transaction,
                    activation.Id,
                    changedAt,
                    cancellationToken).ConfigureAwait(false)
                : null;
            if (releasedAllocationId is not null)
            {
                _ = await PostgresAuditWriter.AppendAsync(
                    connection,
                    transaction,
                    AuditEventType.ACCOUNT_ALLOCATION_RELEASED,
                    changedAt,
                    actorId,
                    activation.GoalVersionId,
                    null,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["activationId"] = activation.Id.Value.ToString("D"),
                        ["allocationEntryId"] = releasedAllocationId.Value.ToString("D")
                    },
                    AccountAllocationState.ACTIVE.ToString(),
                    AccountAllocationState.RELEASED.ToString(),
                    "Paper account allocation released.",
                    "account-allocation-service",
                    cancellationToken).ConfigureAwait(false);
            }

            _ = await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.GOAL_ACTIVATION_STATE_CHANGED,
                changedAt,
                actorId,
                activation.GoalVersionId,
                null,
                RelatedIdentifiers(activation),
                expectedState.ToString(),
                activation.State.ToString(),
                activation.RevocationReason ?? "Goal activation state changed.",
                "goal-activation-service",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<Guid?> ReleaseAllocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ActivationId activationId,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE account_allocation_entries AS allocation
            SET state = 'RELEASED', effective_end = @effective_end
            WHERE allocation.activation_id = @activation_id
              AND allocation.state = 'ACTIVE'
              AND NOT EXISTS (
                  SELECT 1
                  FROM exposure_reservations AS reservation
                  WHERE reservation.allocation_entry_id = allocation.allocation_entry_id
                    AND reservation.state = 'RESERVED'
                    AND reservation.remaining_amount > 0)
              AND NOT EXISTS (
                  SELECT 1
                  FROM orders AS order_record
                  JOIN execution_intents AS intent
                    ON intent.intent_id = order_record.intent_id
                  WHERE intent.goal_version_id = allocation.goal_version_id
                    AND order_record.broker_connection_version = allocation.broker_connection_version
                    AND order_record.environment = allocation.environment
                    AND order_record.endpoint_identity = allocation.endpoint_identity
                    AND order_record.account_identifier = allocation.account_identifier
                    AND order_record.filled_quantity > 0)
            RETURNING allocation_entry_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("effective_end", changedAt);
        command.Parameters.AddWithValue("activation_id", activationId.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid id
            ? id
            : null;
    }

    private static void AddParameters(NpgsqlCommand command, GoalId goalId, GoalActivation activation)
    {
        command.Parameters.AddWithValue("activation_id", activation.Id.Value);
        command.Parameters.AddWithValue("goal_id", goalId.Value);
        command.Parameters.AddWithValue("goal_version_id", activation.GoalVersionId.Value);
        command.Parameters.AddWithValue("operator_id", activation.OperatorId.Value);
        command.Parameters.AddWithValue("state", activation.State.ToString());
        command.Parameters.AddWithValue("activation_evidence", activation.ActivationEvidence);
        command.Parameters.AddWithValue("authority_summary", NpgsqlDbType.Text, activation.AuthoritySummary);
        command.Parameters.AddWithValue("activation_time", activation.ActivationTime);
        command.Parameters.AddWithValue(
            "expiry_time",
            activation.ExpiryTime is null ? DBNull.Value : activation.ExpiryTime.Value);
        command.Parameters.AddWithValue("revision", activation.Revision);
    }

    private static Dictionary<string, string> RelatedIdentifiers(GoalActivation activation) => new(
        StringComparer.Ordinal)
    {
        ["activationId"] = activation.Id.Value.ToString("D")
    };
}
