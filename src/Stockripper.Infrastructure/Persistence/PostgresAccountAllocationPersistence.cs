using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Allocations;
using Stockripper.Application.Brokers;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresAccountAllocationPersistence(NpgsqlDataSource dataSource)
    : IAccountAllocationPersistence
{
    public async ValueTask InsertLimitAsync(
        AccountAllocationLimit accountLimit,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountLimit);
        if (!accountLimit.HasValidContentDigest())
        {
            throw new InvalidDataException("The account allocation limit digest is invalid.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO account_allocation_limits (
                    reference_digest, reference, broker_connection_version, environment,
                    endpoint_identity, account_identifier, maximum_amount, currency,
                    creator_id, created_at, content_digest)
                VALUES (
                    @reference_digest, @reference, @broker_connection_version, @environment,
                    @endpoint_identity, @account_identifier, @maximum_amount, @currency,
                    @creator_id, @created_at, @content_digest)
                """,
                connection,
                transaction);
            AddLimitParameters(command, accountLimit);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _ = await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.ACCOUNT_ALLOCATION_LIMIT_CONFIGURED,
                accountLimit.CreatedAt,
                actorId,
                null,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["accountLimitDigest"] = accountLimit.Reference.Digest
                },
                null,
                "CONFIGURED",
                "Paper account allocation limit configured.",
                "account-allocation-service",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<AccountAllocationLimit?> FindLimitAsync(
        VersionReference limitVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT reference, broker_connection_version, environment, endpoint_identity,
                   account_identifier, maximum_amount, currency, creator_id, created_at,
                   content_digest
            FROM account_allocation_limits
            WHERE reference_digest = @reference_digest
            """,
            connection);
        command.Parameters.AddWithValue("reference_digest", limitVersion.Digest);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var storedReference = PersistenceJson.DeserializeVersion(reader.GetString(0));
        if (storedReference != limitVersion)
        {
            throw new InvalidDataException("The stored account limit identity is invalid.");
        }

        var limit = AccountAllocationLimit.Create(
            storedReference,
            PersistenceJson.DeserializeVersion(reader.GetString(1)),
            Enum.Parse<BrokerEnvironment>(reader.GetString(2), ignoreCase: false),
            reader.GetString(3),
            reader.GetString(4),
            new Money(reader.GetDecimal(5), reader.GetString(6)),
            new OperatorId(reader.GetGuid(7)),
            reader.GetFieldValue<DateTimeOffset>(8));
        if (!string.Equals(limit.ContentDigest, reader.GetString(9), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored account limit digest is invalid.");
        }

        return limit;
    }

    public async ValueTask ReserveAndActivateAsync(
        GoalId goalId,
        GoalActivation activation,
        AccountAllocationEntry allocation,
        BrokerConnectionObservation observation,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        if (activation.Id != allocation.ActivationId
            || activation.GoalVersionId != allocation.GoalVersionId
            || !allocation.HasValidContentDigest()
            || observation.Environment != allocation.Environment
            || !string.Equals(
                observation.EndpointIdentity,
                allocation.EndpointIdentity,
                StringComparison.Ordinal)
            || !string.Equals(
                observation.AccountIdentifier,
                allocation.AccountIdentifier,
                StringComparison.Ordinal))
        {
            throw new AccountAllocationConflictException(AccountAllocationFailure.LimitBindingMismatch);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await LockAllocationScopeAsync(connection, transaction, allocation, cancellationToken)
                .ConfigureAwait(false);
            var maximum = await ReadMaximumForUpdateAsync(
                connection,
                transaction,
                allocation,
                cancellationToken).ConfigureAwait(false);
            var current = await ReadCurrentAllocationAsync(
                connection,
                transaction,
                allocation,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(maximum.Currency, allocation.Allocation.Currency, StringComparison.Ordinal)
                || current + allocation.Allocation.Amount > maximum.Amount)
            {
                throw new AccountAllocationConflictException(
                    AccountAllocationFailure.AccountLimitExceeded);
            }

            await InsertActivationAsync(
                connection,
                transaction,
                goalId,
                activation,
                cancellationToken).ConfigureAwait(false);
            await InsertAllocationAsync(
                connection,
                transaction,
                allocation,
                observation,
                cancellationToken).ConfigureAwait(false);
            await AppendAuthorityAuditAsync(
                connection,
                transaction,
                activation,
                allocation,
                actorId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new AccountAllocationConflictException(
                AccountAllocationFailure.ConcurrentAuthorityConflict);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<AccountAllocationEntry?> FindByActivationAsync(
        ActivationId activationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT allocation_entry_id, goal_version_id, account_limit_version,
                   broker_connection_version, environment, endpoint_identity,
                   account_identifier, allocation_amount, currency,
                   exposure_method_identifier, effective_start, effective_end,
                   state, content_digest
            FROM account_allocation_entries
            WHERE activation_id = @activation_id
            """,
            connection);
        command.Parameters.AddWithValue("activation_id", activationId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? AccountAllocationEntry.Restore(
                new AccountAllocationEntryId(reader.GetGuid(0)),
                activationId,
                new GoalVersionId(reader.GetGuid(1)),
                PersistenceJson.DeserializeVersion(reader.GetString(2)),
                PersistenceJson.DeserializeVersion(reader.GetString(3)),
                Enum.Parse<BrokerEnvironment>(reader.GetString(4), ignoreCase: false),
                reader.GetString(5),
                reader.GetString(6),
                new Money(reader.GetDecimal(7), reader.GetString(8)),
                reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                Enum.Parse<AccountAllocationState>(reader.GetString(12), ignoreCase: false),
                reader.GetString(13))
            : null;
    }

    private static void AddLimitParameters(
        NpgsqlCommand command,
        AccountAllocationLimit accountLimit)
    {
        command.Parameters.AddWithValue("reference_digest", accountLimit.Reference.Digest);
        command.Parameters.Add("reference", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(accountLimit.Reference);
        command.Parameters.Add("broker_connection_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(accountLimit.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", accountLimit.Environment.ToString());
        command.Parameters.AddWithValue("endpoint_identity", accountLimit.EndpointIdentity);
        command.Parameters.AddWithValue("account_identifier", accountLimit.AccountIdentifier);
        command.Parameters.AddWithValue("maximum_amount", accountLimit.MaximumAllocation.Amount);
        command.Parameters.AddWithValue("currency", accountLimit.MaximumAllocation.Currency);
        command.Parameters.AddWithValue("creator_id", accountLimit.CreatorId.Value);
        command.Parameters.AddWithValue("created_at", accountLimit.CreatedAt);
        command.Parameters.AddWithValue("content_digest", accountLimit.ContentDigest);
    }

    private static async ValueTask LockAllocationScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AccountAllocationEntry allocation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@scope, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "scope",
            $"{allocation.BrokerConnectionVersion.Digest}:{allocation.AccountIdentifier}");
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Money> ReadMaximumForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AccountAllocationEntry allocation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT maximum_amount, currency, broker_connection_version::text,
                   environment, endpoint_identity, account_identifier
            FROM account_allocation_limits
            WHERE reference_digest = @reference_digest
            FOR UPDATE
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("reference_digest", allocation.AccountLimitVersion.Digest);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || PersistenceJson.DeserializeVersion(reader.GetString(2)) != allocation.BrokerConnectionVersion
            || !string.Equals(reader.GetString(3), allocation.Environment.ToString(), StringComparison.Ordinal)
            || !string.Equals(reader.GetString(4), allocation.EndpointIdentity, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(5), allocation.AccountIdentifier, StringComparison.Ordinal))
        {
            throw new AccountAllocationConflictException(AccountAllocationFailure.LimitBindingMismatch);
        }

        return new Money(reader.GetDecimal(0), reader.GetString(1));
    }

    private static async ValueTask<decimal> ReadCurrentAllocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AccountAllocationEntry allocation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(SUM(allocation_amount), 0)
            FROM account_allocation_entries
            WHERE broker_connection_digest = @broker_connection_digest
              AND account_identifier = @account_identifier
              AND currency = @currency
              AND state = 'ACTIVE'
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "broker_connection_digest",
            allocation.BrokerConnectionVersion.Digest);
        command.Parameters.AddWithValue("account_identifier", allocation.AccountIdentifier);
        command.Parameters.AddWithValue("currency", allocation.Allocation.Currency);
        return (decimal)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The current account allocation is unavailable."));
    }

    private static async ValueTask InsertActivationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GoalId goalId,
        GoalActivation activation,
        CancellationToken cancellationToken)
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
        command.Parameters.AddWithValue("activation_id", activation.Id.Value);
        command.Parameters.AddWithValue("goal_id", goalId.Value);
        command.Parameters.AddWithValue("goal_version_id", activation.GoalVersionId.Value);
        command.Parameters.AddWithValue("operator_id", activation.OperatorId.Value);
        command.Parameters.AddWithValue("state", activation.State.ToString());
        command.Parameters.AddWithValue("activation_evidence", activation.ActivationEvidence);
        command.Parameters.Add("authority_summary", NpgsqlDbType.Text).Value = activation.AuthoritySummary;
        command.Parameters.AddWithValue("activation_time", activation.ActivationTime);
        command.Parameters.AddWithValue(
            "expiry_time",
            activation.ExpiryTime is null ? DBNull.Value : activation.ExpiryTime.Value);
        command.Parameters.AddWithValue("revision", activation.Revision);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertAllocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AccountAllocationEntry allocation,
        BrokerConnectionObservation observation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO account_allocation_entries (
                allocation_entry_id, activation_id, goal_version_id,
                account_limit_digest, account_limit_version,
                broker_connection_digest, broker_connection_version,
                environment, endpoint_identity, account_identifier,
                allocation_amount, currency, exposure_method_identifier,
                effective_start, effective_end, state,
                observed_environment, observed_endpoint_identity,
                observed_account_identifier, observed_capabilities,
                observed_at, content_digest)
            VALUES (
                @allocation_entry_id, @activation_id, @goal_version_id,
                @account_limit_digest, @account_limit_version,
                @broker_connection_digest, @broker_connection_version,
                @environment, @endpoint_identity, @account_identifier,
                @allocation_amount, @currency, @exposure_method_identifier,
                @effective_start, NULL, @state,
                @observed_environment, @observed_endpoint_identity,
                @observed_account_identifier, @observed_capabilities,
                @observed_at, @content_digest)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("allocation_entry_id", allocation.Id.Value);
        command.Parameters.AddWithValue("activation_id", allocation.ActivationId.Value);
        command.Parameters.AddWithValue("goal_version_id", allocation.GoalVersionId.Value);
        command.Parameters.AddWithValue("account_limit_digest", allocation.AccountLimitVersion.Digest);
        command.Parameters.Add("account_limit_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(allocation.AccountLimitVersion);
        command.Parameters.AddWithValue(
            "broker_connection_digest",
            allocation.BrokerConnectionVersion.Digest);
        command.Parameters.Add("broker_connection_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(allocation.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", allocation.Environment.ToString());
        command.Parameters.AddWithValue("endpoint_identity", allocation.EndpointIdentity);
        command.Parameters.AddWithValue("account_identifier", allocation.AccountIdentifier);
        command.Parameters.AddWithValue("allocation_amount", allocation.Allocation.Amount);
        command.Parameters.AddWithValue("currency", allocation.Allocation.Currency);
        command.Parameters.AddWithValue(
            "exposure_method_identifier",
            allocation.ExposureMethodIdentifier);
        command.Parameters.AddWithValue("effective_start", allocation.EffectiveStart);
        command.Parameters.AddWithValue("state", allocation.State.ToString());
        command.Parameters.AddWithValue("observed_environment", observation.Environment.ToString());
        command.Parameters.AddWithValue("observed_endpoint_identity", observation.EndpointIdentity);
        command.Parameters.AddWithValue("observed_account_identifier", observation.AccountIdentifier);
        command.Parameters.Add("observed_capabilities", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(
            observation.Capabilities.Order(StringComparer.Ordinal));
        command.Parameters.AddWithValue("observed_at", observation.ObservedAt);
        command.Parameters.AddWithValue("content_digest", allocation.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AppendAuthorityAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GoalActivation activation,
        AccountAllocationEntry allocation,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        var related = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["activationId"] = activation.Id.Value.ToString("D"),
            ["allocationEntryId"] = allocation.Id.Value.ToString("D")
        };
        _ = await PostgresAuditWriter.AppendAsync(
            connection,
            transaction,
            AuditEventType.BROKER_CONNECTION_VERIFIED,
            activation.ActivationTime,
            actorId,
            activation.GoalVersionId,
            null,
            related,
            null,
            "VERIFIED",
            "Paper Broker connection and account verified.",
            "paper-review-activation-service",
            cancellationToken).ConfigureAwait(false);
        _ = await PostgresAuditWriter.AppendAsync(
            connection,
            transaction,
            AuditEventType.ACCOUNT_ALLOCATION_RESERVED,
            activation.ActivationTime,
            actorId,
            activation.GoalVersionId,
            null,
            related,
            null,
            allocation.State.ToString(),
            "Paper account allocation reserved.",
            "account-allocation-service",
            cancellationToken).ConfigureAwait(false);
        _ = await PostgresAuditWriter.AppendAsync(
            connection,
            transaction,
            AuditEventType.GOAL_ACTIVATED,
            activation.ActivationTime,
            actorId,
            activation.GoalVersionId,
            null,
            related,
            GoalActivationState.INACTIVE.ToString(),
            activation.State.ToString(),
            "PAPER_REVIEW Goal activated.",
            "paper-review-activation-service",
            cancellationToken).ConfigureAwait(false);
    }
}
