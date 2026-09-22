using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Execution;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresPaperCancellationPersistence(NpgsqlDataSource dataSource)
    : IPaperCancellationPersistence
{
    public async ValueTask<CancellationPreparation> PrepareAsync(
        CancellationRequest request, OrderRecord cancelPendingOrder,
        OrderState expectedOrderState, OperatorId actorId,
        CancellationToken cancellationToken)
    {
        if (request.OrderId != cancelPendingOrder.Id
            || cancelPendingOrder.State != OrderState.CANCEL_PENDING
            || request.AccountBinding != cancelPendingOrder.AccountBinding)
        {
            throw new InvalidDataException("The durable cancellation preparation is inconsistent.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireOrderLockAsync(connection, transaction, request.OrderId, cancellationToken)
                .ConfigureAwait(false);
            var existing = await FindByOrderAsync(connection, transaction, request.OrderId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var storedOrder = await ReadOrderAsync(connection, transaction, request.OrderId,
                    cancellationToken).ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(existing, storedOrder, true);
            }

            var authority = await ReadAuthorityAsync(connection, transaction, request.OrderId,
                expectedOrderState, cancellationToken).ConfigureAwait(false);
            if (authority.AccountBinding != request.AccountBinding
                || !string.Equals(authority.BrokerClientOrderId,
                    request.BrokerClientOrderId, StringComparison.Ordinal)
                || !string.Equals(authority.BrokerOrderId,
                    request.BrokerOrderId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The cancellation request does not match the Order authority.");
            }

            await UpdateOrderAsync(connection, transaction, cancelPendingOrder,
                expectedOrderState, cancellationToken).ConfigureAwait(false);
            await InsertRequestAsync(connection, transaction, request, cancellationToken)
                .ConfigureAwait(false);
            await AppendPreparationAuditAsync(connection, transaction, request,
                expectedOrderState, authority, actorId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(request, cancelPendingOrder, false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask RecordStateAsync(CancellationRequest request,
        CancellationRequestState expectedState, OperatorId actorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            var authority = await ReadAuditAuthorityAsync(connection, transaction,
                request.OrderId, cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                UPDATE cancellation_requests
                SET state = @state, broker_request_reference = @broker_reference,
                    resolution_event_reference = @resolution_reference,
                    last_updated_at = @updated_at
                WHERE cancellation_request_id = @request_id AND state = @expected_state
                """, connection, transaction);
            command.Parameters.AddWithValue("state", request.State.ToString());
            AddNullable(command, "broker_reference", NpgsqlDbType.Text,
                request.BrokerRequestReference);
            AddNullable(command, "resolution_reference", NpgsqlDbType.Text,
                request.ResolutionEventReference);
            command.Parameters.AddWithValue("updated_at", request.LastUpdatedAt);
            command.Parameters.AddWithValue("request_id", request.Id.Value);
            command.Parameters.AddWithValue("expected_state", expectedState.ToString());
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new PaperReviewIntentConcurrencyException();
            }

            await AppendStateAuditAsync(connection, transaction, request, expectedState,
                authority, actorId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<CancellationRequest?> FindByOrderAsync(OrderId orderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindByOrderAsync(connection, null, orderId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<CancellationRequest>> FindRecoverableAsync(
        CancellationToken cancellationToken)
    {
        var values = new List<CancellationRequest>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(RequestSelectSql
            + " WHERE state = 'PREPARED' ORDER BY prepared_at, cancellation_request_id", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(ReadRequest(reader));
        }

        return values;
    }

    public async ValueTask<IReadOnlyList<OrderId>> FindCancelableOrderIdsAsync(
        KillSwitch killSwitch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(killSwitch);
        var condition = killSwitch.ScopeType switch
        {
            KillSwitchScopeType.SYSTEM => "o.environment = @scope_identifier",
            KillSwitchScopeType.CONNECTION => "o.broker_connection_version->>'digest' = @scope_identifier",
            KillSwitchScopeType.ACCOUNT => "o.account_identifier = @scope_identifier",
            KillSwitchScopeType.GOAL => "i.goal_version_id::text = @scope_identifier",
            _ => throw new InvalidDataException("The kill-switch scope is invalid.")
        };
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT o.order_id
            FROM orders o
            JOIN execution_intents i ON i.intent_id = o.intent_id
            LEFT JOIN cancellation_requests request ON request.order_id = o.order_id
            WHERE o.environment = 'PAPER'
              AND o.state IN ('ACKNOWLEDGED', 'PARTIALLY_FILLED')
              AND request.order_id IS NULL
              AND {condition}
            ORDER BY o.first_submission_time, o.order_id
            """, connection);
        command.Parameters.AddWithValue("scope_identifier", killSwitch.ScopeIdentifier);
        var values = new List<OrderId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new OrderId(reader.GetGuid(0)));
        }

        return values;
    }

    private static async ValueTask AcquireOrderLockAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, OrderId orderId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@order_id, 0))", connection, transaction);
        command.Parameters.AddWithValue("order_id", orderId.Value.ToString("D"));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Authority> ReadAuthorityAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, OrderId orderId, OrderState expectedState,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT i.goal_version_id, i.session_id,
                   o.broker_connection_version::text, o.environment,
                   o.endpoint_identity, o.account_identifier,
                   o.broker_client_order_id, o.broker_order_id
            FROM orders o
            JOIN execution_intents i ON i.intent_id = o.intent_id
            WHERE o.order_id = @order_id AND o.state = @expected_state
            FOR UPDATE OF o
            """, connection, transaction);
        command.Parameters.AddWithValue("order_id", orderId.Value);
        command.Parameters.AddWithValue("expected_state", expectedState.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new PaperReviewIntentConcurrencyException();
        }

        return new(new GoalVersionId(reader.GetGuid(0)), new SessionId(reader.GetGuid(1)),
            new IntentAccountBinding(PersistenceJson.DeserializeVersion(reader.GetString(2)),
                Enum.Parse<BrokerEnvironment>(reader.GetString(3), false),
                reader.GetString(4), reader.GetString(5)), reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private static async ValueTask<AuditAuthority> ReadAuditAuthorityAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, OrderId orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT i.goal_version_id, i.session_id
            FROM orders o JOIN execution_intents i ON i.intent_id = o.intent_id
            WHERE o.order_id = @order_id FOR SHARE OF o, i
            """, connection, transaction);
        command.Parameters.AddWithValue("order_id", orderId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The cancellation Order does not exist.");
        }

        return new(new GoalVersionId(reader.GetGuid(0)), new SessionId(reader.GetGuid(1)));
    }

    private static async ValueTask UpdateOrderAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, OrderRecord order, OrderState expectedState,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE orders SET state = 'CANCEL_PENDING', last_updated_at = @updated_at
            WHERE order_id = @order_id AND state = @expected_state
            """, connection, transaction);
        command.Parameters.AddWithValue("updated_at", order.LastUpdatedAt);
        command.Parameters.AddWithValue("order_id", order.Id.Value);
        command.Parameters.AddWithValue("expected_state", expectedState.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PaperReviewIntentConcurrencyException();
        }
    }

    private static async ValueTask InsertRequestAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO cancellation_requests (
                cancellation_request_id, order_id, broker_connection_version,
                environment, endpoint_identity, account_identifier,
                broker_client_order_id, broker_order_id, idempotency_key,
                reason, state, broker_request_reference, resolution_event_reference,
                prepared_at, last_updated_at, content_digest)
            VALUES (@request_id, @order_id, @connection, @environment, @endpoint,
                @account, @client_order_id, @broker_order_id, @idempotency_key,
                @reason, @state, NULL, NULL, @prepared_at, @updated_at, @digest)
            """, connection, transaction);
        command.Parameters.AddWithValue("request_id", request.Id.Value);
        command.Parameters.AddWithValue("order_id", request.OrderId.Value);
        command.Parameters.Add("connection", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(request.AccountBinding.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", request.AccountBinding.Environment.ToString());
        command.Parameters.AddWithValue("endpoint", request.AccountBinding.EndpointIdentity);
        command.Parameters.AddWithValue("account", request.AccountBinding.AccountIdentifier);
        command.Parameters.AddWithValue("client_order_id", request.BrokerClientOrderId);
        command.Parameters.AddWithValue("broker_order_id", request.BrokerOrderId);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("reason", request.Reason);
        command.Parameters.AddWithValue("state", request.State.ToString());
        command.Parameters.AddWithValue("prepared_at", request.PreparedAt);
        command.Parameters.AddWithValue("updated_at", request.LastUpdatedAt);
        command.Parameters.AddWithValue("digest", request.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<CancellationRequest?> FindByOrderAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, OrderId orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RequestSelectSql
            + " WHERE order_id = @order_id", connection, transaction);
        command.Parameters.AddWithValue("order_id", orderId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRequest(reader)
            : null;
    }

    private static async ValueTask<OrderRecord> ReadOrderAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, OrderId orderId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(OrderSelectSql
            + " WHERE order_id = @order_id", connection, transaction);
        command.Parameters.AddWithValue("order_id", orderId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The cancellation Order does not exist.");
        }

        return ReadOrder(reader);
    }

    private static CancellationRequest ReadRequest(NpgsqlDataReader reader) =>
        CancellationRequest.Restore(new CancellationRequestId(reader.GetGuid(0)),
            new OrderId(reader.GetGuid(1)),
            new IntentAccountBinding(PersistenceJson.DeserializeVersion(reader.GetString(2)),
                Enum.Parse<BrokerEnvironment>(reader.GetString(3), false),
                reader.GetString(4), reader.GetString(5)), reader.GetString(6),
            reader.GetString(7), reader.GetString(8), reader.GetString(9),
            Enum.Parse<CancellationRequestState>(reader.GetString(10), false),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetFieldValue<DateTimeOffset>(13), reader.GetFieldValue<DateTimeOffset>(14),
            reader.GetString(15));

    private static OrderRecord ReadOrder(NpgsqlDataReader reader) => OrderRecord.Restore(
        new OrderId(reader.GetGuid(0)), new ExecutionIntentId(reader.GetGuid(1)), reader.GetString(2),
        new PreSubmitEvaluationId(reader.GetGuid(3)),
        new IntentAccountBinding(PersistenceJson.DeserializeVersion(reader.GetString(4)),
            Enum.Parse<BrokerEnvironment>(reader.GetString(5), false), reader.GetString(6), reader.GetString(7)),
        new OrderRequestSnapshot(reader.GetString(8), Enum.Parse<OrderSide>(reader.GetString(9), false),
            Enum.Parse<IntentOrderType>(reader.GetString(10), false), reader.GetDecimal(11), reader.GetDecimal(12),
            Enum.Parse<IntentTimeInForce>(reader.GetString(13), false)),
        reader.GetString(14), reader.GetString(15), Enum.Parse<OrderState>(reader.GetString(16), false),
        reader.IsDBNull(17) ? null : reader.GetString(17), reader.IsDBNull(18) ? null : reader.GetString(18),
        reader.GetDecimal(19), reader.GetFieldValue<DateTimeOffset>(20), reader.GetFieldValue<DateTimeOffset>(21),
        reader.IsDBNull(22) ? null : reader.GetFieldValue<DateTimeOffset>(22));

    private static async ValueTask AppendPreparationAuditAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationRequest request, OrderState previousOrderState,
        Authority authority, OperatorId actorId, CancellationToken cancellationToken)
    {
        var identifiers = Identifiers(request);
        await PostgresAuditWriter.AppendAsync(connection, transaction,
            AuditEventType.CANCELLATION_PREPARED, request.PreparedAt, actorId,
            authority.GoalVersionId, authority.SessionId, identifiers, null,
            request.State.ToString(), request.Reason, "paper-cancellation-preparation",
            cancellationToken).ConfigureAwait(false);
        await PostgresAuditWriter.AppendAsync(connection, transaction,
            AuditEventType.ORDER_STATE_CHANGED, request.PreparedAt, actorId,
            authority.GoalVersionId, authority.SessionId, identifiers,
            previousOrderState.ToString(), OrderState.CANCEL_PENDING.ToString(),
            "The cancellation request made the Order cancel-pending.",
            "paper-cancellation-preparation", cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AppendStateAuditAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationRequest request,
        CancellationRequestState previousState, AuditAuthority authority,
        OperatorId actorId, CancellationToken cancellationToken) =>
        _ = await PostgresAuditWriter.AppendAsync(connection, transaction,
            AuditEventType.CANCELLATION_STATE_CHANGED, request.LastUpdatedAt, actorId,
            authority.GoalVersionId, authority.SessionId, Identifiers(request),
            previousState.ToString(), request.State.ToString(),
            "The paper cancellation request state changed.",
            request.BrokerRequestReference ?? "paper-cancellation-reconciliation",
            cancellationToken).ConfigureAwait(false);

    private static Dictionary<string, string> Identifiers(CancellationRequest request) =>
        new(StringComparer.Ordinal)
        {
            ["cancellationRequestId"] = request.Id.Value.ToString("D"),
            ["orderId"] = request.OrderId.Value.ToString("D")
        };

    private static void AddNullable(NpgsqlCommand command, string name,
        NpgsqlDbType type, object? value) =>
        command.Parameters.Add(name, type).Value = value ?? DBNull.Value;

    private const string RequestSelectSql = """
        SELECT cancellation_request_id, order_id, broker_connection_version::text,
               environment, endpoint_identity, account_identifier,
               broker_client_order_id, broker_order_id, idempotency_key, reason,
               state, broker_request_reference, resolution_event_reference,
               prepared_at, last_updated_at, content_digest
        FROM cancellation_requests
        """;

    private const string OrderSelectSql = """
        SELECT order_id, intent_id, intent_digest, evaluation_id,
               broker_connection_version::text, environment, endpoint_identity, account_identifier,
               instrument_identifier, side, order_type, quantity, limit_price, time_in_force,
               idempotency_key, broker_client_order_id, state, broker_order_id,
               broker_event_reference, filled_quantity, first_submission_time, last_updated_at, terminal_at
        FROM orders
        """;

    private sealed record Authority(GoalVersionId GoalVersionId, SessionId SessionId,
        IntentAccountBinding AccountBinding, string BrokerClientOrderId, string? BrokerOrderId);

    private sealed record AuditAuthority(GoalVersionId GoalVersionId, SessionId SessionId);
}
