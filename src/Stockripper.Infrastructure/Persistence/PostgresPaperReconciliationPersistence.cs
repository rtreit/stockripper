using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Execution;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresPaperReconciliationPersistence(NpgsqlDataSource dataSource)
    : IPaperReconciliationPersistence
{
    public async ValueTask<IReadOnlyList<GoalVersionId>> FindRecoveryTargetsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT goal_version_id
            FROM sessions
            WHERE mode IN ('PAPER_REVIEW', 'PAPER_AUTO')
              AND state IN ('READY', 'RUNNING', 'STOPPING', 'RECONCILING', 'LOCKED')
            ORDER BY goal_version_id
            """, connection);
        var values = new List<GoalVersionId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new GoalVersionId(reader.GetGuid(0)));
        }

        return values;
    }

    public async ValueTask<PaperLocalReconciliationState> ReadAsync(
        GoalVersionId goalVersionId,
        IntentAccountBinding accountBinding,
        CancellationToken cancellationToken)
    {
        if (accountBinding.Environment != BrokerEnvironment.PAPER)
        {
            throw new InvalidDataException("Reconciliation persistence accepts only paper authority.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var orders = await ReadOrdersAsync(connection, goalVersionId, accountBinding,
            cancellationToken).ConfigureAwait(false);
        var fills = await ReadFillsAsync(connection, goalVersionId, accountBinding,
            cancellationToken).ConfigureAwait(false);
        var reservations = await ReadReservationsAsync(connection, goalVersionId,
            accountBinding, cancellationToken).ConfigureAwait(false);
        var unevaluatedFillCount = await ReadUnevaluatedFillCountAsync(
            connection, goalVersionId, accountBinding, cancellationToken)
            .ConfigureAwait(false);
        return new(orders, fills, reservations, unevaluatedFillCount);
    }

    private static async ValueTask<IReadOnlyList<PaperReconciliationOrder>> ReadOrdersAsync(
        NpgsqlConnection connection, GoalVersionId goalVersionId,
        IntentAccountBinding binding, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT o.intent_id, o.broker_client_order_id, o.broker_order_id,
                   o.instrument_identifier, o.side, o.quantity, o.limit_price,
                   o.filled_quantity, o.state
            FROM orders o
            JOIN execution_intents i ON i.intent_id = o.intent_id
            WHERE i.goal_version_id = @goal_version_id
              AND o.broker_connection_version = @connection
              AND o.environment = 'PAPER'
              AND o.endpoint_identity = @endpoint
              AND o.account_identifier = @account
            ORDER BY o.broker_client_order_id
            """, connection);
        AddAuthority(command, goalVersionId, binding);
        var values = new List<PaperReconciliationOrder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new(new ExecutionIntentId(reader.GetGuid(0)), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                Enum.Parse<OrderSide>(reader.GetString(4), false), reader.GetDecimal(5),
                reader.GetDecimal(6), reader.GetDecimal(7),
                Enum.Parse<OrderState>(reader.GetString(8), false)));
        }

        return values;
    }

    private static async ValueTask<int> ReadUnevaluatedFillCountAsync(
        NpgsqlConnection connection,
        GoalVersionId goalVersionId,
        IntentAccountBinding binding,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM fills f
            JOIN orders o ON o.order_id = f.order_id
            JOIN execution_intents i ON i.intent_id = o.intent_id
            LEFT JOIN post_fill_evaluations evaluation
              ON evaluation.fill_id = f.fill_id
            WHERE i.goal_version_id = @goal_version_id
              AND o.broker_connection_version = @connection
              AND o.environment = 'PAPER'
              AND o.endpoint_identity = @endpoint
              AND o.account_identifier = @account
              AND evaluation.fill_id IS NULL
            """, connection);
        AddAuthority(command, goalVersionId, binding);
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0L));
    }

    private static async ValueTask<IReadOnlyList<PaperReconciliationFill>> ReadFillsAsync(
        NpgsqlConnection connection, GoalVersionId goalVersionId,
        IntentAccountBinding binding, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(f.broker_fill_id, 'event:' || f.event_id::text),
                   f.broker_order_id, f.instrument_identifier, f.quantity,
                   f.price, f.broker_execution_time
            FROM fills f
            JOIN orders o ON o.order_id = f.order_id
            JOIN execution_intents i ON i.intent_id = o.intent_id
            WHERE i.goal_version_id = @goal_version_id
              AND o.broker_connection_version = @connection
              AND o.environment = 'PAPER'
              AND o.endpoint_identity = @endpoint
              AND o.account_identifier = @account
            ORDER BY 1
            """, connection);
        AddAuthority(command, goalVersionId, binding);
        var values = new List<PaperReconciliationFill>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetDecimal(3), reader.GetDecimal(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return values;
    }

    private static async ValueTask<IReadOnlyList<PaperReconciliationReservation>>
        ReadReservationsAsync(NpgsqlConnection connection, GoalVersionId goalVersionId,
            IntentAccountBinding binding, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT r.intent_id, r.remaining_amount, r.state
            FROM exposure_reservations r
            JOIN execution_intents i ON i.intent_id = r.intent_id
            WHERE i.goal_version_id = @goal_version_id
              AND r.broker_connection_version = @connection
              AND r.environment = 'PAPER'
              AND r.endpoint_identity = @endpoint
              AND r.account_identifier = @account
            ORDER BY r.intent_id
            """, connection);
        AddAuthority(command, goalVersionId, binding);
        var values = new List<PaperReconciliationReservation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new(new ExecutionIntentId(reader.GetGuid(0)), reader.GetDecimal(1),
                Enum.Parse<ExposureReservationState>(reader.GetString(2), false)));
        }

        return values;
    }

    private static void AddAuthority(NpgsqlCommand command, GoalVersionId goalVersionId,
        IntentAccountBinding binding)
    {
        command.Parameters.AddWithValue("goal_version_id", goalVersionId.Value);
        command.Parameters.Add("connection", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(binding.BrokerConnectionVersion);
        command.Parameters.AddWithValue("endpoint", binding.EndpointIdentity);
        command.Parameters.AddWithValue("account", binding.AccountIdentifier);
    }
}
