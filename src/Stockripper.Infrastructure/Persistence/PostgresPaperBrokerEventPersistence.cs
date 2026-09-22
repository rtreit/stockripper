using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Execution;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresPaperBrokerEventPersistence(NpgsqlDataSource dataSource)
    : IPaperBrokerEventPersistence
{
    public async ValueTask<PaperBrokerEventIngestion> ApplyAsync(
        BrokerExecutionEvent brokerEvent,
        FillRecord? fill,
        OrderRecord updatedOrder,
        OrderState expectedOrderState,
        decimal remainingExposure,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireEventLockAsync(connection, transaction, brokerEvent.EventReference, cancellationToken)
                .ConfigureAwait(false);
            if (await EventExistsAsync(connection, transaction, brokerEvent, cancellationToken)
                .ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(PaperBrokerEventDisposition.DUPLICATE, updatedOrder, null);
            }

            var authority = await LockAndValidateOrderAsync(connection, transaction, brokerEvent,
                expectedOrderState, cancellationToken).ConfigureAwait(false);
            await InsertEventAsync(connection, transaction, brokerEvent, cancellationToken).ConfigureAwait(false);
            var storedFill = fill is null ? null : await InsertFillAsync(connection, transaction, fill,
                authority.OrderQuantity, cancellationToken).ConfigureAwait(false);
            var changed = updatedOrder.State != expectedOrderState
                || updatedOrder.FilledQuantity != authority.FilledQuantity;
            if (changed)
            {
                await UpdateOrderAsync(connection, transaction, updatedOrder,
                    expectedOrderState, cancellationToken).ConfigureAwait(false);
            }

            var exposureChanged = await AdjustReservationAsync(connection, transaction,
                updatedOrder.IntentId, remainingExposure, brokerEvent.ReceivedAt, cancellationToken)
                .ConfigureAwait(false);
            var cancellationChange = await ResolveCancellationAsync(connection, transaction,
                brokerEvent, cancellationToken).ConfigureAwait(false);
            var intentCanceled = updatedOrder.State == OrderState.CANCELED
                && await MarkIntentCanceledAsync(connection, transaction, authority,
                    cancellationToken).ConfigureAwait(false);
            await AppendAuditAsync(connection, transaction, brokerEvent, storedFill,
                updatedOrder, expectedOrderState, authority, changed, exposureChanged,
                remainingExposure, cancellationChange, intentCanceled, actorId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(changed ? PaperBrokerEventDisposition.APPLIED
                : PaperBrokerEventDisposition.RECORDED_NO_CHANGE, updatedOrder, storedFill);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask AcquireEventLockAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string eventReference, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@event_reference, 0))",
            connection, transaction);
        command.Parameters.AddWithValue("event_reference", eventReference);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> EventExistsAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, BrokerExecutionEvent value, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT order_id, broker_connection_version::text, environment,
                   endpoint_identity, account_identifier, broker_client_order_id,
                   broker_order_id, kind, cumulative_filled_quantity,
                   broker_event_time, source_payload_reference
            FROM broker_execution_events WHERE event_reference = @event_reference
            """, connection, transaction);
        command.Parameters.AddWithValue("event_reference", value.EventReference);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (reader.GetGuid(0) != value.OrderId.Value
            || PersistenceJson.DeserializeVersion(reader.GetString(1)) != value.AccountBinding.BrokerConnectionVersion
            || !string.Equals(reader.GetString(2), value.AccountBinding.Environment.ToString(), StringComparison.Ordinal)
            || !string.Equals(reader.GetString(3), value.AccountBinding.EndpointIdentity, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(4), value.AccountBinding.AccountIdentifier, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(5), value.BrokerClientOrderId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(6), value.BrokerOrderId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(7), value.Kind.ToString(), StringComparison.Ordinal)
            || reader.GetDecimal(8) != value.CumulativeFilledQuantity
            || reader.GetFieldValue<DateTimeOffset>(9) != value.BrokerEventTime
            || !string.Equals(reader.GetString(10), value.SourcePayloadReference, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Broker event reference has conflicting content.");
        }

        return true;
    }

    private static async ValueTask<Authority> LockAndValidateOrderAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, BrokerExecutionEvent brokerEvent, OrderState expectedState,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT o.state, o.filled_quantity, o.quantity, o.intent_id,
                   i.goal_version_id, i.session_id, i.state,
                   o.broker_connection_version::text, o.environment,
                   o.endpoint_identity, o.account_identifier,
                   o.broker_client_order_id, o.broker_order_id
            FROM orders o
            JOIN execution_intents i ON i.intent_id = o.intent_id
            WHERE o.order_id = @order_id
            FOR UPDATE OF o
            """, connection, transaction);
        command.Parameters.AddWithValue("order_id", brokerEvent.OrderId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The Broker event Order does not exist.");
        }

        var storedState = reader.GetString(0);
        var storedBrokerOrderId = reader.IsDBNull(12) ? null : reader.GetString(12);
        if (!string.Equals(storedState, expectedState.ToString(), StringComparison.Ordinal)
            || PersistenceJson.DeserializeVersion(reader.GetString(7)) != brokerEvent.AccountBinding.BrokerConnectionVersion
            || !string.Equals(reader.GetString(8), brokerEvent.AccountBinding.Environment.ToString(), StringComparison.Ordinal)
            || !string.Equals(reader.GetString(9), brokerEvent.AccountBinding.EndpointIdentity, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(10), brokerEvent.AccountBinding.AccountIdentifier, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(11), brokerEvent.BrokerClientOrderId, StringComparison.Ordinal)
            || (storedBrokerOrderId is not null
                && !string.Equals(storedBrokerOrderId, brokerEvent.BrokerOrderId, StringComparison.Ordinal)))
        {
            throw new PaperReviewIntentConcurrencyException();
        }

        return new(new ExecutionIntentId(reader.GetGuid(3)), new GoalVersionId(reader.GetGuid(4)),
            new SessionId(reader.GetGuid(5)), reader.GetString(6), reader.GetDecimal(1), reader.GetDecimal(2));
    }

    private static async ValueTask InsertEventAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, BrokerExecutionEvent value, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO broker_execution_events (
                event_id, order_id, broker_connection_version, environment,
                endpoint_identity, account_identifier, broker_client_order_id,
                broker_order_id, event_reference, kind, cumulative_filled_quantity,
                broker_event_time, received_at, source_payload_reference, content_digest)
            VALUES (@event_id, @order_id, @connection, @environment,
                @endpoint, @account, @client_order_id, @broker_order_id,
                @event_reference, @kind, @cumulative, @broker_time,
                @received_at, @payload_reference, @digest)
            """, connection, transaction);
        command.Parameters.AddWithValue("event_id", value.Id.Value);
        command.Parameters.AddWithValue("order_id", value.OrderId.Value);
        command.Parameters.Add("connection", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(value.AccountBinding.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", value.AccountBinding.Environment.ToString());
        command.Parameters.AddWithValue("endpoint", value.AccountBinding.EndpointIdentity);
        command.Parameters.AddWithValue("account", value.AccountBinding.AccountIdentifier);
        command.Parameters.AddWithValue("client_order_id", value.BrokerClientOrderId);
        command.Parameters.AddWithValue("broker_order_id", value.BrokerOrderId);
        command.Parameters.AddWithValue("event_reference", value.EventReference);
        command.Parameters.AddWithValue("kind", value.Kind.ToString());
        command.Parameters.AddWithValue("cumulative", value.CumulativeFilledQuantity);
        command.Parameters.AddWithValue("broker_time", value.BrokerEventTime);
        command.Parameters.AddWithValue("received_at", value.ReceivedAt);
        command.Parameters.AddWithValue("payload_reference", value.SourcePayloadReference);
        command.Parameters.AddWithValue("digest", value.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<FillRecord?> InsertFillAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, FillRecord fill, decimal orderQuantity,
        CancellationToken cancellationToken)
    {
        if (fill.BrokerFillId is not null)
        {
            await using var existing = new NpgsqlCommand(
                """
                SELECT order_id, broker_order_id, instrument_identifier, quantity,
                       price, fee_amount, fee_currency, broker_execution_time
                FROM fills WHERE broker_fill_id = @broker_fill_id
                """,
                connection, transaction);
            existing.Parameters.AddWithValue("broker_fill_id", fill.BrokerFillId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetGuid(0) != fill.OrderId.Value
                    || !string.Equals(reader.GetString(1), fill.BrokerOrderId, StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(2), fill.InstrumentIdentifier, StringComparison.Ordinal)
                    || reader.GetDecimal(3) != fill.Quantity || reader.GetDecimal(4) != fill.Price
                    || (reader.IsDBNull(5) ? null : reader.GetDecimal(5)) != fill.FeeAmount
                    || !string.Equals(reader.IsDBNull(6) ? null : reader.GetString(6), fill.FeeCurrency, StringComparison.Ordinal)
                    || reader.GetFieldValue<DateTimeOffset>(7) != fill.BrokerExecutionTime)
                {
                    throw new InvalidDataException("The Broker Fill identifier has conflicting content.");
                }

                return null;
            }
        }

        await using (var total = new NpgsqlCommand(
            "SELECT COALESCE(sum(quantity), 0) FROM fills WHERE order_id = @order_id", connection, transaction))
        {
            total.Parameters.AddWithValue("order_id", fill.OrderId.Value);
            var storedQuantity = (decimal)(await total.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0m);
            if (storedQuantity + fill.Quantity > orderQuantity)
            {
                throw new InvalidDataException("The Broker Fill quantity exceeds the Order quantity.");
            }
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO fills (fill_id, broker_fill_id, order_id, event_id,
                broker_order_id, instrument_identifier, quantity, price,
                fee_amount, fee_currency, broker_execution_time, received_at,
                source_payload_reference, content_digest)
            VALUES (@fill_id, @broker_fill_id, @order_id, @event_id,
                @broker_order_id, @instrument, @quantity, @price,
                @fee_amount, @fee_currency, @execution_time, @received_at,
                @payload_reference, @digest)
            """, connection, transaction);
        command.Parameters.AddWithValue("fill_id", fill.Id.Value);
        AddNullable(command, "broker_fill_id", NpgsqlDbType.Text, fill.BrokerFillId);
        command.Parameters.AddWithValue("order_id", fill.OrderId.Value);
        command.Parameters.AddWithValue("event_id", fill.EventId.Value);
        command.Parameters.AddWithValue("broker_order_id", fill.BrokerOrderId);
        command.Parameters.AddWithValue("instrument", fill.InstrumentIdentifier);
        command.Parameters.AddWithValue("quantity", fill.Quantity);
        command.Parameters.AddWithValue("price", fill.Price);
        AddNullable(command, "fee_amount", NpgsqlDbType.Numeric, fill.FeeAmount);
        AddNullable(command, "fee_currency", NpgsqlDbType.Text, fill.FeeCurrency);
        command.Parameters.AddWithValue("execution_time", fill.BrokerExecutionTime);
        command.Parameters.AddWithValue("received_at", fill.ReceivedAt);
        command.Parameters.AddWithValue("payload_reference", fill.SourcePayloadReference);
        command.Parameters.AddWithValue("digest", fill.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return fill;
    }

    private static async ValueTask UpdateOrderAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, OrderRecord order, OrderState expectedState,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE orders
            SET state = @state, broker_order_id = @broker_order_id,
                broker_event_reference = @event_reference,
                filled_quantity = @filled_quantity, last_updated_at = @updated_at,
                terminal_at = @terminal_at
            WHERE order_id = @order_id AND state = @expected_state
            """, connection, transaction);
        command.Parameters.AddWithValue("state", order.State.ToString());
        command.Parameters.AddWithValue("broker_order_id", order.BrokerOrderId!);
        command.Parameters.AddWithValue("event_reference", order.BrokerEventReference!);
        command.Parameters.AddWithValue("filled_quantity", order.FilledQuantity);
        command.Parameters.AddWithValue("updated_at", order.LastUpdatedAt);
        AddNullable(command, "terminal_at", NpgsqlDbType.TimestampTz, order.TerminalAt);
        command.Parameters.AddWithValue("order_id", order.Id.Value);
        command.Parameters.AddWithValue("expected_state", expectedState.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PaperReviewIntentConcurrencyException();
        }
    }

    private static async ValueTask<bool> AdjustReservationAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, ExecutionIntentId intentId, decimal remainingExposure,
        DateTimeOffset changedAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE exposure_reservations
            SET remaining_amount = @remaining,
                state = CASE WHEN @remaining = 0 THEN 'RELEASED' ELSE state END,
                released_at = CASE WHEN @remaining = 0 THEN @changed_at ELSE released_at END,
                release_reason = CASE WHEN @remaining = 0 THEN 'Broker truth made the Order terminal.' ELSE release_reason END,
                revision = revision + 1
            WHERE intent_id = @intent_id AND state = 'RESERVED'
              AND remaining_amount > @remaining
            """, connection, transaction);
        command.Parameters.AddWithValue("remaining", remainingExposure);
        command.Parameters.AddWithValue("changed_at", changedAt);
        command.Parameters.AddWithValue("intent_id", intentId.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async ValueTask<CancellationChange?> ResolveCancellationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        BrokerExecutionEvent brokerEvent, CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand(
            """
            SELECT cancellation_request_id, broker_connection_version::text,
                   environment, endpoint_identity, account_identifier,
                   broker_client_order_id, broker_order_id, idempotency_key,
                   reason, state, broker_request_reference,
                   resolution_event_reference, prepared_at, last_updated_at,
                   content_digest
            FROM cancellation_requests WHERE order_id = @order_id FOR UPDATE
            """, connection, transaction);
        read.Parameters.AddWithValue("order_id", brokerEvent.OrderId.Value);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var current = CancellationRequest.Restore(
            new CancellationRequestId(reader.GetGuid(0)), brokerEvent.OrderId,
            new IntentAccountBinding(PersistenceJson.DeserializeVersion(reader.GetString(1)),
                Enum.Parse<BrokerEnvironment>(reader.GetString(2), false),
                reader.GetString(3), reader.GetString(4)), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8),
            Enum.Parse<CancellationRequestState>(reader.GetString(9), false),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetFieldValue<DateTimeOffset>(12), reader.GetFieldValue<DateTimeOffset>(13),
            reader.GetString(14));
        await reader.DisposeAsync().ConfigureAwait(false);
        var resolved = current.Resolve(brokerEvent);
        if (resolved.State == current.State)
        {
            return null;
        }

        await using var update = new NpgsqlCommand(
            """
            UPDATE cancellation_requests
            SET state = @state, resolution_event_reference = @event_reference,
                last_updated_at = @updated_at
            WHERE cancellation_request_id = @request_id AND state = @expected_state
            """, connection, transaction);
        update.Parameters.AddWithValue("state", resolved.State.ToString());
        update.Parameters.AddWithValue("event_reference", resolved.ResolutionEventReference!);
        update.Parameters.AddWithValue("updated_at", resolved.LastUpdatedAt);
        update.Parameters.AddWithValue("request_id", resolved.Id.Value);
        update.Parameters.AddWithValue("expected_state", current.State.ToString());
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PaperReviewIntentConcurrencyException();
        }

        return new(current.State, resolved);
    }

    private static async ValueTask<bool> MarkIntentCanceledAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Authority authority, CancellationToken cancellationToken)
    {
        if (authority.IntentState is not ("SUBMITTED" or "SUBMISSION_UNKNOWN"))
        {
            return false;
        }

        await using var command = new NpgsqlCommand(
            """
            UPDATE execution_intents SET state = 'CANCELED'
            WHERE intent_id = @intent_id AND state = @expected_state
            """, connection, transaction);
        command.Parameters.AddWithValue("intent_id", authority.IntentId.Value);
        command.Parameters.AddWithValue("expected_state", authority.IntentState);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PaperReviewIntentConcurrencyException();
        }

        return true;
    }

    private static async ValueTask AppendAuditAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, BrokerExecutionEvent brokerEvent, FillRecord? fill,
        OrderRecord order, OrderState previousState, Authority authority, bool changed,
        bool exposureChanged, decimal remainingExposure,
        CancellationChange? cancellationChange, bool intentCanceled, OperatorId actorId,
        CancellationToken cancellationToken)
    {
        await PostgresAuditWriter.AppendAsync(connection, transaction,
            AuditEventType.BROKER_EXECUTION_EVENT_RECORDED, brokerEvent.ReceivedAt, actorId,
            authority.GoalVersionId, authority.SessionId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["brokerExecutionEventId"] = brokerEvent.Id.Value.ToString("D"),
                ["orderId"] = order.Id.Value.ToString("D")
            }, null, brokerEvent.Kind.ToString(), "The paper Broker event was recorded.",
            brokerEvent.SourcePayloadReference, cancellationToken).ConfigureAwait(false);
        if (fill is not null)
        {
            await PostgresAuditWriter.AppendAsync(connection, transaction, AuditEventType.FILL_RECORDED,
                fill.ReceivedAt, actorId, authority.GoalVersionId, authority.SessionId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fillId"] = fill.Id.Value.ToString("D"),
                    ["orderId"] = order.Id.Value.ToString("D")
                }, null, "RECORDED", "The Broker Fill was recorded once.",
                fill.SourcePayloadReference, cancellationToken).ConfigureAwait(false);
        }

        if (changed)
        {
            await PostgresAuditWriter.AppendAsync(connection, transaction, AuditEventType.ORDER_STATE_CHANGED,
                brokerEvent.ReceivedAt, actorId, authority.GoalVersionId, authority.SessionId,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["orderId"] = order.Id.Value.ToString("D") },
                previousState.ToString(), order.State.ToString(), "Broker truth changed the Order state.",
                brokerEvent.SourcePayloadReference, cancellationToken).ConfigureAwait(false);
        }

        if (exposureChanged)
        {
            await PostgresAuditWriter.AppendAsync(connection, transaction, AuditEventType.EXPOSURE_ADJUSTED,
                brokerEvent.ReceivedAt, actorId, authority.GoalVersionId, authority.SessionId,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["intentId"] = order.IntentId.Value.ToString("D") },
                null, remainingExposure.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Broker truth reduced the open exposure reservation.", brokerEvent.SourcePayloadReference,
                cancellationToken).ConfigureAwait(false);
        }

        if (cancellationChange is not null)
        {
            await PostgresAuditWriter.AppendAsync(connection, transaction,
                AuditEventType.CANCELLATION_STATE_CHANGED, brokerEvent.ReceivedAt, actorId,
                authority.GoalVersionId, authority.SessionId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cancellationRequestId"] = cancellationChange.Request.Id.Value.ToString("D"),
                    ["orderId"] = order.Id.Value.ToString("D")
                }, cancellationChange.PreviousState.ToString(),
                cancellationChange.Request.State.ToString(),
                "Broker truth resolved the cancellation request.",
                brokerEvent.SourcePayloadReference, cancellationToken).ConfigureAwait(false);
        }

        if (intentCanceled)
        {
            await PostgresAuditWriter.AppendAsync(connection, transaction,
                AuditEventType.EXECUTION_INTENT_STATE_CHANGED, brokerEvent.ReceivedAt,
                actorId, authority.GoalVersionId, authority.SessionId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["intentId"] = authority.IntentId.Value.ToString("D"),
                    ["orderId"] = order.Id.Value.ToString("D")
                }, authority.IntentState, ExecutionIntentState.CANCELED.ToString(),
                "Broker cancellation truth canceled the Execution intent.",
                brokerEvent.SourcePayloadReference, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(name, type).Value = value ?? DBNull.Value;

    private sealed record Authority(ExecutionIntentId IntentId, GoalVersionId GoalVersionId,
        SessionId SessionId, string IntentState, decimal FilledQuantity, decimal OrderQuantity);

    private sealed record CancellationChange(CancellationRequestState PreviousState,
        CancellationRequest Request);
}
