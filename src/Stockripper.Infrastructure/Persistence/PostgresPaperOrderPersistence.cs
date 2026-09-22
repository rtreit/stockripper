using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Execution;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresPaperOrderPersistence(NpgsqlDataSource dataSource) : IPaperOrderPersistence
{
    public async ValueTask RecordEvaluationAsync(PreSubmitEvaluation evaluation,
        OperatorId actorId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var authority = await ReadAuthorityAsync(connection, transaction, evaluation.IntentId, cancellationToken)
                .ConfigureAwait(false);
            await InsertEvaluationAsync(connection, transaction, evaluation, cancellationToken).ConfigureAwait(false);
            await AppendEvaluationAuditAsync(connection, transaction, evaluation, authority, actorId, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask PrepareAsync(PreSubmitEvaluation evaluation, OrderRecord order,
        ExecutionIntent pendingIntent, ExecutionIntentState expectedIntentState,
        OperatorId actorId, CancellationToken cancellationToken)
    {
        if (evaluation.Result != PreSubmitEvaluationResult.PASSED
            || order.State != OrderState.SUBMITTING
            || pendingIntent.State != ExecutionIntentState.SUBMISSION_PENDING
            || order.IntentId != pendingIntent.Id || evaluation.IntentId != pendingIntent.Id)
        {
            throw new InvalidDataException("The durable Order preparation is inconsistent.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var authority = await ReadAuthorityAsync(connection, transaction, pendingIntent.Id, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(authority.State, expectedIntentState.ToString(), StringComparison.Ordinal)
                || !string.Equals(authority.IntentDigest, pendingIntent.ContentDigest, StringComparison.Ordinal))
            {
                throw new PaperReviewIntentConcurrencyException();
            }

            await EnsureCurrentSafetyAsync(
                connection, transaction, pendingIntent, evaluation, cancellationToken)
                .ConfigureAwait(false);
            await InsertEvaluationAsync(connection, transaction, evaluation, cancellationToken).ConfigureAwait(false);
            await InsertOrderAsync(connection, transaction, order, cancellationToken).ConfigureAwait(false);
            await UpdateIntentAsync(connection, transaction, pendingIntent, expectedIntentState, cancellationToken)
                .ConfigureAwait(false);
            await AppendEvaluationAuditAsync(connection, transaction, evaluation, authority, actorId, cancellationToken)
                .ConfigureAwait(false);
            await PostgresAuditWriter.AppendAsync(connection, transaction, AuditEventType.ORDER_PREPARED,
                order.FirstSubmissionTime, actorId, authority.GoalVersionId, authority.SessionId,
                Identifiers(order), null, order.State.ToString(),
                "The paper Order was persisted before the Broker boundary.",
                "paper-order-preparation", cancellationToken).ConfigureAwait(false);
            await AppendIntentAuditAsync(connection, transaction, pendingIntent, expectedIntentState,
                order.FirstSubmissionTime, actorId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask<OrderRecord?> FindOrderAsync(OrderId orderId, CancellationToken cancellationToken) =>
        FindAsync("order_id", orderId.Value, cancellationToken);

    public ValueTask<OrderRecord?> FindByIntentAsync(ExecutionIntentId intentId, CancellationToken cancellationToken) =>
        FindAsync("intent_id", intentId.Value, cancellationToken);

    public async ValueTask<PreSubmitEvaluation?> FindEvaluationAsync(
        PreSubmitEvaluationId evaluationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            PreSubmitEvaluationSelectSql + " WHERE evaluation_id = @evaluation_id",
            connection);
        command.Parameters.AddWithValue("evaluation_id", evaluationId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadEvaluation(reader)
            : null;
    }

    public async ValueTask RecordTransitionAsync(OrderRecord order, OrderState expectedOrderState,
        ExecutionIntent intent, ExecutionIntentState expectedIntentState, BrokerOrderEvent? brokerEvent,
        OperatorId actorId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (brokerEvent is not null)
            {
                await InsertBrokerEventAsync(connection, transaction, brokerEvent, cancellationToken)
                    .ConfigureAwait(false);
            }

            await using (var update = new NpgsqlCommand(
                """
                UPDATE orders
                SET state = @state, broker_order_id = @broker_order_id,
                    broker_event_reference = @broker_event_reference,
                    filled_quantity = @filled_quantity,
                    last_updated_at = @last_updated_at, terminal_at = @terminal_at
                WHERE order_id = @order_id AND state = @expected_state
                """, connection, transaction))
            {
                update.Parameters.AddWithValue("state", order.State.ToString());
                AddNullable(update, "broker_order_id", NpgsqlDbType.Text, order.BrokerOrderId);
                AddNullable(update, "broker_event_reference", NpgsqlDbType.Text, order.BrokerEventReference);
                update.Parameters.AddWithValue("filled_quantity", order.FilledQuantity);
                update.Parameters.AddWithValue("last_updated_at", order.LastUpdatedAt);
                AddNullable(update, "terminal_at", NpgsqlDbType.TimestampTz, order.TerminalAt);
                update.Parameters.AddWithValue("order_id", order.Id.Value);
                update.Parameters.AddWithValue("expected_state", expectedOrderState.ToString());
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new PaperReviewIntentConcurrencyException();
                }
            }

            await UpdateIntentAsync(connection, transaction, intent, expectedIntentState, cancellationToken)
                .ConfigureAwait(false);
            await PostgresAuditWriter.AppendAsync(connection, transaction, AuditEventType.ORDER_STATE_CHANGED,
                order.LastUpdatedAt, actorId, intent.GoalVersionId, intent.SessionId, Identifiers(order),
                expectedOrderState.ToString(), order.State.ToString(),
                "The durable paper Order state changed.", "paper-order-state", cancellationToken)
                .ConfigureAwait(false);
            if (brokerEvent is not null)
            {
                await PostgresAuditWriter.AppendAsync(connection, transaction,
                    AuditEventType.BROKER_ORDER_EVENT_RECORDED, brokerEvent.ObservedAt, actorId,
                    intent.GoalVersionId, intent.SessionId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["brokerEventId"] = brokerEvent.Id.Value.ToString("D"),
                        ["orderId"] = order.Id.Value.ToString("D")
                    }, null, brokerEvent.Result.ToString(),
                    "The fake Broker result was recorded.", "paper-broker-fake-result", cancellationToken)
                    .ConfigureAwait(false);
            }

            await AppendIntentAuditAsync(connection, transaction, intent, expectedIntentState,
                order.LastUpdatedAt, actorId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<OrderRecord>> FindRecoverableAsync(CancellationToken cancellationToken)
    {
        var orders = new List<OrderRecord>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(OrderSelectSql +
            " WHERE state IN ('SUBMITTING', 'SUBMISSION_UNKNOWN') ORDER BY first_submission_time, order_id", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            orders.Add(ReadOrder(reader));
        }

        return orders;
    }

    private async ValueTask<OrderRecord?> FindAsync(string column, Guid value, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"{OrderSelectSql} WHERE {column} = @value", connection);
        command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadOrder(reader) : null;
    }

    private static async ValueTask InsertEvaluationAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PreSubmitEvaluation evaluation, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pre_submit_evaluations (
                evaluation_id, intent_id, intent_digest, approval_id, reservation_id,
                reconciliation_id, position_exposure, open_order_exposure, buying_power,
                currency, checks, result, evaluated_at, content_digest)
            VALUES (@evaluation_id, @intent_id, @intent_digest, @approval_id, @reservation_id,
                @reconciliation_id, @position_exposure, @open_order_exposure, @buying_power,
                @currency, @checks, @result, @evaluated_at, @content_digest)
            """, connection, transaction);
        command.Parameters.AddWithValue("evaluation_id", evaluation.Id.Value);
        command.Parameters.AddWithValue("intent_id", evaluation.IntentId.Value);
        command.Parameters.AddWithValue("intent_digest", evaluation.IntentDigest);
        AddNullable(command, "approval_id", NpgsqlDbType.Uuid,
            evaluation.ApprovalId?.Value);
        command.Parameters.AddWithValue("reservation_id", evaluation.ReservationId.Value);
        command.Parameters.AddWithValue("reconciliation_id", evaluation.ReconciliationId.Value);
        command.Parameters.AddWithValue("position_exposure", evaluation.PositionExposure);
        command.Parameters.AddWithValue("open_order_exposure", evaluation.OpenOrderExposure);
        command.Parameters.AddWithValue("buying_power", evaluation.BuyingPower);
        command.Parameters.AddWithValue("currency", evaluation.Currency);
        command.Parameters.Add("checks", NpgsqlDbType.Jsonb).Value = PersistenceJson.SerializePreSubmitChecks(evaluation.Checks);
        command.Parameters.AddWithValue("result", evaluation.Result.ToString());
        command.Parameters.AddWithValue("evaluated_at", evaluation.EvaluatedAt);
        command.Parameters.AddWithValue("content_digest", evaluation.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask EnsureCurrentSafetyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExecutionIntent intent,
        PreSubmitEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM reconciliations AS reconciliation
                    WHERE reconciliation.reconciliation_id = @reconciliation_id
                      AND reconciliation.goal_version_id = @goal_version_id
                      AND reconciliation.broker_connection_version = @broker_connection_version
                      AND reconciliation.environment = @environment
                      AND reconciliation.endpoint_identity = @endpoint_identity
                      AND reconciliation.account_identifier = @account_identifier
                      AND reconciliation.result = 'CURRENT'
                      AND reconciliation.sequence = (
                          SELECT max(latest.sequence)
                          FROM reconciliations AS latest
                          WHERE latest.goal_version_id = @goal_version_id
                            AND latest.broker_connection_version = @broker_connection_version
                            AND latest.environment = @environment
                            AND latest.endpoint_identity = @endpoint_identity
                            AND latest.account_identifier = @account_identifier)),
                (
                    SELECT count(*)
                    FROM kill_switches
                    WHERE state = 'CLEAR'
                      AND ((scope_type = 'SYSTEM' AND scope_identifier = @environment)
                        OR (scope_type = 'CONNECTION' AND scope_identifier = @connection_digest)
                        OR (scope_type = 'ACCOUNT' AND scope_identifier = @account_identifier)
                        OR (scope_type = 'GOAL' AND scope_identifier = @goal_scope))),
                EXISTS (
                    SELECT 1
                    FROM exposure_reservations
                    WHERE reservation_id = @reservation_id
                      AND intent_id = @intent_id
                      AND goal_version_id = @goal_version_id
                      AND broker_connection_version = @broker_connection_version
                      AND environment = @environment
                      AND endpoint_identity = @endpoint_identity
                      AND account_identifier = @account_identifier
                      AND state = 'RESERVED')
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "reconciliation_id", evaluation.ReconciliationId.Value);
        command.Parameters.AddWithValue("reservation_id", evaluation.ReservationId.Value);
        command.Parameters.AddWithValue("intent_id", intent.Id.Value);
        command.Parameters.AddWithValue("goal_version_id", intent.GoalVersionId.Value);
        command.Parameters.Add("broker_connection_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(intent.AccountBinding.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", intent.AccountBinding.Environment.ToString());
        command.Parameters.AddWithValue("endpoint_identity", intent.AccountBinding.EndpointIdentity);
        command.Parameters.AddWithValue("account_identifier", intent.AccountBinding.AccountIdentifier);
        command.Parameters.AddWithValue(
            "connection_digest", intent.AccountBinding.BrokerConnectionVersion.Digest);
        command.Parameters.AddWithValue("goal_scope", intent.GoalVersionId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!reader.GetBoolean(0) || reader.GetInt64(1) != 4 || !reader.GetBoolean(2))
        {
            throw new InvalidOperationException(
                "Current durable execution safety evidence is invalid.");
        }
    }

    private static async ValueTask InsertOrderAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, OrderRecord order, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO orders (order_id, intent_id, intent_digest, evaluation_id,
                broker_connection_version, environment, endpoint_identity, account_identifier,
                instrument_identifier, side, order_type, quantity, limit_price, time_in_force,
                idempotency_key, broker_client_order_id, state, broker_order_id,
                broker_event_reference, filled_quantity, first_submission_time, last_updated_at, terminal_at)
            VALUES (@order_id, @intent_id, @intent_digest, @evaluation_id,
                @broker_connection_version, @environment, @endpoint_identity, @account_identifier,
                @instrument_identifier, @side, @order_type, @quantity, @limit_price, @time_in_force,
                @idempotency_key, @broker_client_order_id, @state, NULL, NULL,
                @filled_quantity, @first_submission_time, @last_updated_at, NULL)
            """, connection, transaction);
        command.Parameters.AddWithValue("order_id", order.Id.Value);
        command.Parameters.AddWithValue("intent_id", order.IntentId.Value);
        command.Parameters.AddWithValue("intent_digest", order.IntentDigest);
        command.Parameters.AddWithValue("evaluation_id", order.PreSubmitEvaluationId.Value);
        command.Parameters.Add("broker_connection_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(order.AccountBinding.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", order.AccountBinding.Environment.ToString());
        command.Parameters.AddWithValue("endpoint_identity", order.AccountBinding.EndpointIdentity);
        command.Parameters.AddWithValue("account_identifier", order.AccountBinding.AccountIdentifier);
        command.Parameters.AddWithValue("instrument_identifier", order.Request.InstrumentIdentifier);
        command.Parameters.AddWithValue("side", order.Request.Side.ToString());
        command.Parameters.AddWithValue("order_type", order.Request.OrderType.ToString());
        command.Parameters.AddWithValue("quantity", order.Request.Quantity);
        command.Parameters.AddWithValue("limit_price", order.Request.LimitPrice);
        command.Parameters.AddWithValue("time_in_force", order.Request.TimeInForce.ToString());
        command.Parameters.AddWithValue("idempotency_key", order.IdempotencyKey);
        command.Parameters.AddWithValue("broker_client_order_id", order.BrokerClientOrderId);
        command.Parameters.AddWithValue("state", order.State.ToString());
        command.Parameters.AddWithValue("filled_quantity", order.FilledQuantity);
        command.Parameters.AddWithValue("first_submission_time", order.FirstSubmissionTime);
        command.Parameters.AddWithValue("last_updated_at", order.LastUpdatedAt);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertBrokerEventAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, BrokerOrderEvent brokerEvent, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO broker_order_events (event_id, order_id, broker_client_order_id,
                broker_order_id, event_reference, result, observed_at, content_digest)
            VALUES (@event_id, @order_id, @broker_client_order_id,
                @broker_order_id, @event_reference, @result, @observed_at, @content_digest)
            """, connection, transaction);
        command.Parameters.AddWithValue("event_id", brokerEvent.Id.Value);
        command.Parameters.AddWithValue("order_id", brokerEvent.OrderId.Value);
        command.Parameters.AddWithValue("broker_client_order_id", brokerEvent.BrokerClientOrderId);
        AddNullable(command, "broker_order_id", NpgsqlDbType.Text, brokerEvent.BrokerOrderId);
        command.Parameters.AddWithValue("event_reference", brokerEvent.EventReference);
        command.Parameters.AddWithValue("result", brokerEvent.Result.ToString());
        command.Parameters.AddWithValue("observed_at", brokerEvent.ObservedAt);
        command.Parameters.AddWithValue("content_digest", brokerEvent.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpdateIntentAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, ExecutionIntent intent, ExecutionIntentState expected,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE execution_intents SET state = @state WHERE intent_id = @intent_id AND state = @expected AND content_digest = @digest",
            connection, transaction);
        command.Parameters.AddWithValue("state", intent.State.ToString());
        command.Parameters.AddWithValue("intent_id", intent.Id.Value);
        command.Parameters.AddWithValue("expected", expected.ToString());
        command.Parameters.AddWithValue("digest", intent.ContentDigest);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PaperReviewIntentConcurrencyException();
        }
    }

    private static async ValueTask<Authority> ReadAuthorityAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, ExecutionIntentId intentId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT goal_version_id, session_id, state, content_digest FROM execution_intents WHERE intent_id = @intent_id FOR UPDATE",
            connection, transaction);
        command.Parameters.AddWithValue("intent_id", intentId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The Execution intent does not exist.");
        }

        return new(new GoalVersionId(reader.GetGuid(0)), new SessionId(reader.GetGuid(1)),
            reader.GetString(2), reader.GetString(3));
    }

    private static async ValueTask AppendEvaluationAuditAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PreSubmitEvaluation evaluation, Authority authority,
        OperatorId actorId, CancellationToken cancellationToken) =>
        _ = await PostgresAuditWriter.AppendAsync(connection, transaction,
            AuditEventType.PRE_SUBMIT_EVALUATED, evaluation.EvaluatedAt, actorId,
            authority.GoalVersionId, authority.SessionId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["evaluationId"] = evaluation.Id.Value.ToString("D"),
                ["intentId"] = evaluation.IntentId.Value.ToString("D")
            }, null, evaluation.Result.ToString(), "Final paper submission checks recorded.",
            "paper-pre-submit", cancellationToken).ConfigureAwait(false);

    private static async ValueTask AppendIntentAuditAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, ExecutionIntent intent, ExecutionIntentState previous,
        DateTimeOffset eventTime, OperatorId actorId, CancellationToken cancellationToken) =>
        _ = await PostgresAuditWriter.AppendAsync(connection, transaction,
            AuditEventType.EXECUTION_INTENT_STATE_CHANGED, eventTime,
            actorId, intent.GoalVersionId, intent.SessionId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["intentId"] = intent.Id.Value.ToString("D")
            }, previous.ToString(), intent.State.ToString(), "Execution intent submission state recorded.",
            "paper-order-state", cancellationToken).ConfigureAwait(false);

    private static Dictionary<string, string> Identifiers(OrderRecord order) =>
        new(StringComparer.Ordinal)
        {
            ["intentId"] = order.IntentId.Value.ToString("D"),
            ["orderId"] = order.Id.Value.ToString("D")
        };

    private static OrderRecord ReadOrder(NpgsqlDataReader reader) => OrderRecord.Restore(
        new OrderId(reader.GetGuid(0)), new ExecutionIntentId(reader.GetGuid(1)), reader.GetString(2),
        new PreSubmitEvaluationId(reader.GetGuid(3)),
        new IntentAccountBinding(PersistenceJson.DeserializeVersion(reader.GetString(4)),
            Enum.Parse<BrokerEnvironment>(reader.GetString(5), ignoreCase: false), reader.GetString(6), reader.GetString(7)),
        new OrderRequestSnapshot(reader.GetString(8), Enum.Parse<OrderSide>(reader.GetString(9), false),
            Enum.Parse<IntentOrderType>(reader.GetString(10), false), reader.GetDecimal(11), reader.GetDecimal(12),
            Enum.Parse<IntentTimeInForce>(reader.GetString(13), false)),
        reader.GetString(14), reader.GetString(15), Enum.Parse<OrderState>(reader.GetString(16), false),
        reader.IsDBNull(17) ? null : reader.GetString(17), reader.IsDBNull(18) ? null : reader.GetString(18),
        reader.GetDecimal(19), reader.GetFieldValue<DateTimeOffset>(20), reader.GetFieldValue<DateTimeOffset>(21),
        reader.IsDBNull(22) ? null : reader.GetFieldValue<DateTimeOffset>(22));

    private static PreSubmitEvaluation ReadEvaluation(NpgsqlDataReader reader) =>
        PreSubmitEvaluation.Restore(
            new PreSubmitEvaluationId(reader.GetGuid(0)),
            new ExecutionIntentId(reader.GetGuid(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : new ReviewApprovalId(reader.GetGuid(3)),
            new ExposureReservationId(reader.GetGuid(4)),
            new ReconciliationId(reader.GetGuid(5)),
            reader.GetDecimal(6),
            reader.GetDecimal(7),
            reader.GetDecimal(8),
            reader.GetString(9),
            PersistenceJson.DeserializePreSubmitChecks(reader.GetString(10)),
            Enum.Parse<PreSubmitEvaluationResult>(reader.GetString(11), false),
            reader.GetFieldValue<DateTimeOffset>(12),
            reader.GetString(13));

    private static void AddNullable(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object? value) => command.Parameters.Add(name, type).Value = value ?? DBNull.Value;

    private const string OrderSelectSql = """
        SELECT order_id, intent_id, intent_digest, evaluation_id,
               broker_connection_version::text, environment, endpoint_identity, account_identifier,
               instrument_identifier, side, order_type, quantity, limit_price, time_in_force,
               idempotency_key, broker_client_order_id, state, broker_order_id,
               broker_event_reference, filled_quantity, first_submission_time, last_updated_at, terminal_at
        FROM orders
        """;

    private const string PreSubmitEvaluationSelectSql = """
        SELECT evaluation_id, intent_id, intent_digest, approval_id, reservation_id,
               reconciliation_id, position_exposure, open_order_exposure, buying_power,
               currency, checks::text, result, evaluated_at, content_digest
        FROM pre_submit_evaluations
        """;

    private sealed record Authority(GoalVersionId GoalVersionId, SessionId SessionId,
        string State, string IntentDigest);
}
