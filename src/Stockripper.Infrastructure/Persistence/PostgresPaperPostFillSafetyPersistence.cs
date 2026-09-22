using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Execution;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Risk;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresPaperPostFillSafetyPersistence(
    NpgsqlDataSource dataSource,
    IPaperRiskPolicyProvider riskPolicyProvider)
    : IPaperPostFillSafetyPersistence
{
    public async ValueTask<PostFillSafetyOutcome> EvaluateAsync(FillId fillId,
        PositionSnapshotBatch positionBatch, IReadOnlyList<PositionSnapshot> positions,
        OperatorId actorId, CancellationToken cancellationToken)
    {
        if (positions.Count != positionBatch.PositionCount
            || positions.Any(item => item.BatchId != positionBatch.Id
                || item.AccountBinding != positionBatch.AccountBinding))
        {
            throw new InvalidDataException("The Position snapshot batch is incomplete.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireLocksAsync(connection, transaction, fillId,
                positionBatch.ObservationReference, cancellationToken).ConfigureAwait(false);
            var existing = await FindExistingAsync(connection, transaction, fillId,
                positionBatch, positions, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }

            var authority = await ReadAuthorityAsync(connection, transaction, fillId,
                positionBatch.AccountBinding, cancellationToken).ConfigureAwait(false);
            var riskPolicy = await riskPolicyProvider.FindAsync(
                authority.RiskPolicyVersion, cancellationToken).ConfigureAwait(false);
            if (riskPolicy is null || !riskPolicy.IsBoundTo(authority.RiskPolicyVersion))
            {
                throw new InvalidDataException(
                    "The exact post-Fill Risk policy is unavailable.");
            }

            ValidatePositionEvidence(positionBatch, positions, authority);
            await InsertBatchAsync(connection, transaction, positionBatch, cancellationToken)
                .ConfigureAwait(false);
            foreach (var position in positions)
            {
                await InsertPositionAsync(connection, transaction, position, cancellationToken)
                    .ConfigureAwait(false);
            }

            var positionExposure = positions.Sum(item => item.MarketValue);
            var openExposure = await ReadOpenExposureAsync(connection, transaction,
                authority.AllocationEntryId, cancellationToken).ConfigureAwait(false);
            var instrumentExposure = positions.ToDictionary(
                item => item.InstrumentIdentifier,
                item => item.MarketValue,
                StringComparer.Ordinal);
            foreach (var item in openExposure.ByInstrument)
            {
                instrumentExposure[item.Key] = instrumentExposure.GetValueOrDefault(item.Key)
                    + item.Value;
            }

            var largestInstrumentExposure = instrumentExposure.Count == 0
                ? 0m
                : instrumentExposure.Values.Max();
            var positionCurrent = positions.Any(item =>
                string.Equals(item.InstrumentIdentifier, authority.InstrumentIdentifier, StringComparison.Ordinal)
                && item.Quantity >= authority.FilledQuantity);
            var evaluation = PostFillEvaluation.Create(PostFillEvaluationId.New(), fillId,
                authority.OrderId, authority.GoalVersionId, authority.SessionId,
                authority.RiskPolicyVersion, authority.AllocationEntryId, positionBatch,
                positionExposure, openExposure.Total, authority.AllocationAmount,
                largestInstrumentExposure, riskPolicy.MaximumPositionExposure,
                riskPolicy.MaximumPortfolioExposure, riskPolicy.MaximumConcentration,
                authority.Currency, positionCurrent, positionBatch.ReceivedAt);
            await InsertEvaluationAsync(connection, transaction, evaluation, cancellationToken)
                .ConfigureAwait(false);

            PostFillSafetyResponse? response = null;
            if (evaluation.Result == PostFillEvaluationResult.VIOLATION_LOCKED)
            {
                response = PostFillSafetyResponse.Create(PostFillSafetyResponseId.New(), evaluation,
                    positionBatch.AccountBinding,
                    ViolationReason(evaluation),
                    evaluation.EvaluatedAt);
                await InsertResponseAsync(connection, transaction, response, cancellationToken)
                    .ConfigureAwait(false);
                await LockSessionAsync(connection, transaction, authority, actorId,
                    response.Reason, evaluation.EvaluatedAt, cancellationToken).ConfigureAwait(false);
                await TriggerSafetyScopesAsync(connection, transaction, authority,
                    positionBatch.AccountBinding, actorId, response.Reason,
                    evaluation.EvaluatedAt, cancellationToken).ConfigureAwait(false);
            }

            await AppendAuditAsync(connection, transaction, positionBatch, evaluation,
                response, actorId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(positionBatch, positions, evaluation, response, false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask AcquireLocksAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, FillId fillId, string observationReference,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@fill_id, 0)), pg_advisory_xact_lock(hashtextextended(@observation_reference, 0))",
            connection, transaction);
        command.Parameters.AddWithValue("fill_id", fillId.Value.ToString("D"));
        command.Parameters.AddWithValue("observation_reference", observationReference);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Authority> ReadAuthorityAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, FillId fillId, IntentAccountBinding observedBinding,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT f.order_id, f.instrument_identifier, o.filled_quantity,
                   o.intent_id, i.goal_version_id, i.session_id,
                   risk.risk_policy_version::text, reservation.allocation_entry_id,
                   allocation.allocation_amount, allocation.currency,
                   o.broker_connection_version::text, o.environment,
                   o.endpoint_identity, o.account_identifier,
                   session.state, session.revision
            FROM fills f
            JOIN orders o ON o.order_id = f.order_id
            JOIN execution_intents i ON i.intent_id = o.intent_id
            JOIN risk_evaluations risk ON risk.risk_evaluation_id = i.risk_evaluation_id
            JOIN exposure_reservations reservation ON reservation.intent_id = i.intent_id
            JOIN account_allocation_entries allocation
              ON allocation.allocation_entry_id = reservation.allocation_entry_id
            JOIN sessions session ON session.session_id = i.session_id
            WHERE f.fill_id = @fill_id
            FOR UPDATE OF o, reservation, allocation, session
            """, connection, transaction);
        command.Parameters.AddWithValue("fill_id", fillId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The post-Fill evaluation Fill does not exist.");
        }

        var storedBinding = new IntentAccountBinding(
            PersistenceJson.DeserializeVersion(reader.GetString(10)),
            Enum.Parse<BrokerEnvironment>(reader.GetString(11), false),
            reader.GetString(12), reader.GetString(13));
        if (storedBinding != observedBinding
            || observedBinding.Environment != BrokerEnvironment.PAPER)
        {
            throw new InvalidDataException("The Position observation does not match the paper authority.");
        }

        return new(new OrderId(reader.GetGuid(0)), reader.GetString(1), reader.GetDecimal(2),
            new ExecutionIntentId(reader.GetGuid(3)), new GoalVersionId(reader.GetGuid(4)),
            new SessionId(reader.GetGuid(5)), PersistenceJson.DeserializeVersion(reader.GetString(6)),
            new AccountAllocationEntryId(reader.GetGuid(7)), reader.GetDecimal(8), reader.GetString(9),
            Enum.Parse<Domain.Sessions.SessionState>(reader.GetString(14), false), reader.GetInt32(15));
    }

    private static void ValidatePositionEvidence(PositionSnapshotBatch batch,
        IReadOnlyList<PositionSnapshot> positions, Authority authority)
    {
        if (positions.Any(item => !string.Equals(item.Currency, authority.Currency, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The Position observation has invalid policy evidence.");
        }
    }

    private static string ViolationReason(PostFillEvaluation evaluation)
    {
        if (!evaluation.PositionEvidenceCurrent)
        {
            return "The Position observation does not contain the filled Order quantity.";
        }

        if (!evaluation.AllocationLimitPassed)
        {
            return "Observed exposure exceeds the Goal allocation.";
        }

        if (!evaluation.PositionLimitPassed)
        {
            return "Observed exposure exceeds the maximum Position exposure.";
        }

        if (!evaluation.PortfolioLimitPassed)
        {
            return "Observed exposure exceeds the maximum portfolio exposure.";
        }

        return "Observed exposure exceeds the concentration limit.";
    }

    private static async ValueTask<OpenExposure> ReadOpenExposureAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, AccountAllocationEntryId allocationEntryId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT intent.instrument_identifier, sum(reservation.remaining_amount)
            FROM exposure_reservations reservation
            JOIN execution_intents intent ON intent.intent_id = reservation.intent_id
            WHERE reservation.allocation_entry_id = @allocation_entry_id
              AND reservation.state = 'RESERVED'
            GROUP BY intent.instrument_identifier
            """, connection, transaction);
        command.Parameters.AddWithValue("allocation_entry_id", allocationEntryId.Value);
        var byInstrument = new Dictionary<string, decimal>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            byInstrument.Add(reader.GetString(0), reader.GetDecimal(1));
        }

        return new OpenExposure(byInstrument.Values.Sum(), byInstrument);
    }

    private static async ValueTask InsertBatchAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PositionSnapshotBatch value, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO position_snapshot_batches (batch_id, broker_connection_version,
                environment, endpoint_identity, account_identifier, observation_reference,
                position_count, broker_observation_time, received_at,
                source_payload_reference, content_digest)
            VALUES (@batch_id, @connection, @environment, @endpoint, @account,
                @reference, @count, @broker_time, @received_at, @source, @digest)
            """, connection, transaction);
        command.Parameters.AddWithValue("batch_id", value.Id.Value);
        command.Parameters.Add("connection", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(value.AccountBinding.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", value.AccountBinding.Environment.ToString());
        command.Parameters.AddWithValue("endpoint", value.AccountBinding.EndpointIdentity);
        command.Parameters.AddWithValue("account", value.AccountBinding.AccountIdentifier);
        command.Parameters.AddWithValue("reference", value.ObservationReference);
        command.Parameters.AddWithValue("count", value.PositionCount);
        command.Parameters.AddWithValue("broker_time", value.BrokerObservationTime);
        command.Parameters.AddWithValue("received_at", value.ReceivedAt);
        command.Parameters.AddWithValue("source", value.SourcePayloadReference);
        command.Parameters.AddWithValue("digest", value.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertPositionAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PositionSnapshot value, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO position_snapshots (snapshot_id, batch_id, broker_connection_version,
                environment, endpoint_identity, account_identifier, instrument_identifier,
                quantity, average_entry_price, market_value, currency, content_digest)
            VALUES (@snapshot_id, @batch_id, @connection, @environment, @endpoint,
                @account, @instrument, @quantity, @average_price, @market_value,
                @currency, @digest)
            """, connection, transaction);
        command.Parameters.AddWithValue("snapshot_id", value.Id.Value);
        command.Parameters.AddWithValue("batch_id", value.BatchId.Value);
        command.Parameters.Add("connection", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(value.AccountBinding.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", value.AccountBinding.Environment.ToString());
        command.Parameters.AddWithValue("endpoint", value.AccountBinding.EndpointIdentity);
        command.Parameters.AddWithValue("account", value.AccountBinding.AccountIdentifier);
        command.Parameters.AddWithValue("instrument", value.InstrumentIdentifier);
        command.Parameters.AddWithValue("quantity", value.Quantity);
        AddNullable(command, "average_price", NpgsqlDbType.Numeric, value.AverageEntryPrice);
        command.Parameters.AddWithValue("market_value", value.MarketValue);
        command.Parameters.AddWithValue("currency", value.Currency);
        command.Parameters.AddWithValue("digest", value.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertEvaluationAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PostFillEvaluation value, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO post_fill_evaluations (evaluation_id, fill_id, order_id,
                goal_version_id, session_id, risk_policy_version, allocation_entry_id,
                position_batch_id, position_exposure, open_order_exposure,
                allocation_amount, largest_instrument_exposure,
                maximum_position_exposure, maximum_portfolio_exposure,
                maximum_concentration, currency, position_evidence_current,
                result, evaluated_at, content_digest)
            VALUES (@evaluation_id, @fill_id, @order_id, @goal_id, @session_id,
                @risk_policy, @allocation_id, @batch_id, @position_exposure,
                @open_exposure, @allocation_amount, @largest_exposure,
                @maximum_position, @maximum_portfolio, @maximum_concentration,
                @currency, @position_current, @result, @evaluated_at, @digest)
            """, connection, transaction);
        command.Parameters.AddWithValue("evaluation_id", value.Id.Value);
        command.Parameters.AddWithValue("fill_id", value.FillId.Value);
        command.Parameters.AddWithValue("order_id", value.OrderId.Value);
        command.Parameters.AddWithValue("goal_id", value.GoalVersionId.Value);
        command.Parameters.AddWithValue("session_id", value.SessionId.Value);
        command.Parameters.Add("risk_policy", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(value.RiskPolicyVersion);
        command.Parameters.AddWithValue("allocation_id", value.AllocationEntryId.Value);
        command.Parameters.AddWithValue("batch_id", value.PositionBatchId.Value);
        command.Parameters.AddWithValue("position_exposure", value.PositionExposure);
        command.Parameters.AddWithValue("open_exposure", value.OpenOrderExposure);
        command.Parameters.AddWithValue("allocation_amount", value.AllocationAmount);
        command.Parameters.AddWithValue("largest_exposure", value.LargestInstrumentExposure);
        command.Parameters.AddWithValue("maximum_position", value.MaximumPositionExposure);
        command.Parameters.AddWithValue("maximum_portfolio", value.MaximumPortfolioExposure);
        command.Parameters.AddWithValue("maximum_concentration", value.MaximumConcentration);
        command.Parameters.AddWithValue("currency", value.Currency);
        command.Parameters.AddWithValue("position_current", value.PositionEvidenceCurrent);
        command.Parameters.AddWithValue("result", value.Result.ToString());
        command.Parameters.AddWithValue("evaluated_at", value.EvaluatedAt);
        command.Parameters.AddWithValue("digest", value.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertResponseAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PostFillSafetyResponse value, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO post_fill_safety_responses (response_id, evaluation_id,
                goal_version_id, session_id, broker_connection_version, environment,
                endpoint_identity, account_identifier, reconciliation_state,
                notification_state, reason, created_at, content_digest)
            VALUES (@response_id, @evaluation_id, @goal_id, @session_id,
                @connection, @environment, @endpoint, @account, @reconciliation,
                @notification, @reason, @created_at, @digest)
            """, connection, transaction);
        command.Parameters.AddWithValue("response_id", value.Id.Value);
        command.Parameters.AddWithValue("evaluation_id", value.EvaluationId.Value);
        command.Parameters.AddWithValue("goal_id", value.GoalVersionId.Value);
        command.Parameters.AddWithValue("session_id", value.SessionId.Value);
        command.Parameters.Add("connection", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(value.AccountBinding.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", value.AccountBinding.Environment.ToString());
        command.Parameters.AddWithValue("endpoint", value.AccountBinding.EndpointIdentity);
        command.Parameters.AddWithValue("account", value.AccountBinding.AccountIdentifier);
        command.Parameters.AddWithValue("reconciliation", value.ReconciliationState.ToString());
        command.Parameters.AddWithValue("notification", value.NotificationState.ToString());
        command.Parameters.AddWithValue("reason", value.Reason);
        command.Parameters.AddWithValue("created_at", value.CreatedAt);
        command.Parameters.AddWithValue("digest", value.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask LockSessionAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Authority authority, OperatorId actorId, string reason,
        DateTimeOffset changedAt, CancellationToken cancellationToken)
    {
        if (authority.SessionState == Domain.Sessions.SessionState.LOCKED)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            """
            UPDATE sessions SET state = 'LOCKED', revision = revision + 1
            WHERE session_id = @session_id AND state = @state AND revision = @revision
            """, connection, transaction);
        command.Parameters.AddWithValue("session_id", authority.SessionId.Value);
        command.Parameters.AddWithValue("state", authority.SessionState.ToString());
        command.Parameters.AddWithValue("revision", authority.SessionRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Session changed during post-Fill evaluation.");
        }

        await PostgresAuditWriter.AppendAsync(connection, transaction,
            AuditEventType.SESSION_STATE_CHANGED, changedAt, actorId,
            authority.GoalVersionId, authority.SessionId,
            new Dictionary<string, string>(StringComparer.Ordinal),
            authority.SessionState.ToString(), Domain.Sessions.SessionState.LOCKED.ToString(),
            reason, "post-fill-safety", cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask TriggerSafetyScopesAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Authority authority, IntentAccountBinding binding,
        OperatorId actorId, string reason, DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE kill_switches
            SET state = 'TRIGGERED', revision = revision + 1,
                triggered_at = @changed_at, trigger_reason = @reason,
                recovery_reconciliation_id = NULL, recovery_required_at = NULL,
                cleared_at = NULL
            WHERE state = 'CLEAR' AND (
                (scope_type = 'ACCOUNT' AND scope_identifier = @account)
                OR (scope_type = 'GOAL' AND scope_identifier = @goal_id))
            RETURNING kill_switch_id, scope_type
            """, connection, transaction);
        command.Parameters.AddWithValue("changed_at", changedAt);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("account", binding.AccountIdentifier);
        command.Parameters.AddWithValue("goal_id", authority.GoalVersionId.Value.ToString("D"));
        var changed = new List<(Guid Id, string Scope)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                changed.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        foreach (var item in changed)
        {
            await PostgresAuditWriter.AppendAsync(connection, transaction,
                AuditEventType.KILL_SWITCH_STATE_CHANGED, changedAt, actorId,
                authority.GoalVersionId, authority.SessionId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["killSwitchId"] = item.Id.ToString("D"),
                    ["scopeType"] = item.Scope
                }, KillSwitchState.CLEAR.ToString(), KillSwitchState.TRIGGERED.ToString(),
                reason, "post-fill-safety", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask AppendAuditAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PositionSnapshotBatch batch,
        PostFillEvaluation evaluation, PostFillSafetyResponse? response,
        OperatorId actorId, CancellationToken cancellationToken)
    {
        await PostgresAuditWriter.AppendAsync(connection, transaction,
            AuditEventType.POSITION_SNAPSHOT_RECORDED, batch.ReceivedAt, actorId,
            evaluation.GoalVersionId, evaluation.SessionId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["positionBatchId"] = batch.Id.Value.ToString("D")
            }, null, "RECORDED", "Complete paper Position truth was recorded.",
            batch.SourcePayloadReference, cancellationToken).ConfigureAwait(false);
        await PostgresAuditWriter.AppendAsync(connection, transaction,
            AuditEventType.POST_FILL_EVALUATED, evaluation.EvaluatedAt, actorId,
            evaluation.GoalVersionId, evaluation.SessionId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fillId"] = evaluation.FillId.Value.ToString("D"),
                ["postFillEvaluationId"] = evaluation.Id.Value.ToString("D")
            }, null, evaluation.Result.ToString(), "Post-Fill exposure was evaluated.",
            batch.SourcePayloadReference, cancellationToken).ConfigureAwait(false);
        if (response is not null)
        {
            await PostgresAuditWriter.AppendAsync(connection, transaction,
                AuditEventType.POST_FILL_SAFETY_RESPONSE_CREATED, response.CreatedAt, actorId,
                response.GoalVersionId, response.SessionId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["postFillSafetyResponseId"] = response.Id.Value.ToString("D")
                }, null, response.ReconciliationState.ToString(), response.Reason,
                "post-fill-safety", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<PostFillSafetyOutcome?> FindExistingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, FillId fillId,
        PositionSnapshotBatch incomingBatch, IReadOnlyList<PositionSnapshot> incomingPositions,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT evaluation.evaluation_id, evaluation.order_id,
                   evaluation.goal_version_id, evaluation.session_id,
                   evaluation.risk_policy_version::text, evaluation.allocation_entry_id,
                   evaluation.position_exposure, evaluation.open_order_exposure,
                   evaluation.allocation_amount, evaluation.largest_instrument_exposure,
                   evaluation.maximum_position_exposure,
                   evaluation.maximum_portfolio_exposure,
                   evaluation.maximum_concentration, evaluation.currency,
                   evaluation.position_evidence_current, evaluation.result,
                   evaluation.evaluated_at, evaluation.content_digest,
                   batch.batch_id, batch.broker_connection_version::text,
                   batch.environment, batch.endpoint_identity, batch.account_identifier,
                   batch.observation_reference, batch.position_count,
                   batch.broker_observation_time, batch.received_at,
                   batch.source_payload_reference, batch.content_digest,
                   response.response_id, response.reason, response.created_at,
                   response.content_digest
            FROM post_fill_evaluations evaluation
            JOIN position_snapshot_batches batch
              ON batch.batch_id = evaluation.position_batch_id
            LEFT JOIN post_fill_safety_responses response
              ON response.evaluation_id = evaluation.evaluation_id
            WHERE evaluation.fill_id = @fill_id
            """, connection, transaction);
        command.Parameters.AddWithValue("fill_id", fillId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var binding = new IntentAccountBinding(PersistenceJson.DeserializeVersion(reader.GetString(19)),
            Enum.Parse<BrokerEnvironment>(reader.GetString(20), false), reader.GetString(21), reader.GetString(22));
        var batch = PositionSnapshotBatch.Create(new PositionSnapshotBatchId(reader.GetGuid(18)),
            binding, reader.GetString(23), reader.GetInt32(24),
            reader.GetFieldValue<DateTimeOffset>(25), reader.GetFieldValue<DateTimeOffset>(26),
            reader.GetString(27));
        if (binding != incomingBatch.AccountBinding
            || !string.Equals(batch.ObservationReference, incomingBatch.ObservationReference, StringComparison.Ordinal)
            || batch.PositionCount != incomingBatch.PositionCount
            || batch.BrokerObservationTime != incomingBatch.BrokerObservationTime
            || !string.Equals(batch.SourcePayloadReference, incomingBatch.SourcePayloadReference, StringComparison.Ordinal)
            || !string.Equals(batch.ContentDigest, reader.GetString(28), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Position observation reference has conflicting content.");
        }

        var evaluation = PostFillEvaluation.Restore(new PostFillEvaluationId(reader.GetGuid(0)), fillId,
            new OrderId(reader.GetGuid(1)), new GoalVersionId(reader.GetGuid(2)),
            new SessionId(reader.GetGuid(3)), PersistenceJson.DeserializeVersion(reader.GetString(4)),
            new AccountAllocationEntryId(reader.GetGuid(5)), batch, reader.GetDecimal(6),
            reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9),
            reader.GetDecimal(10), reader.GetDecimal(11), reader.GetDecimal(12),
            reader.GetString(13), reader.GetBoolean(14),
            reader.GetFieldValue<DateTimeOffset>(16), reader.GetString(17));
        if (!string.Equals(evaluation.Result.ToString(), reader.GetString(15), StringComparison.Ordinal)
            || !string.Equals(evaluation.ContentDigest, reader.GetString(17), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The stored post-Fill evaluation is invalid.");
        }

        PostFillSafetyResponse? response = null;
        if (!reader.IsDBNull(29))
        {
            response = PostFillSafetyResponse.Create(new PostFillSafetyResponseId(reader.GetGuid(29)),
                evaluation, binding, reader.GetString(30), reader.GetFieldValue<DateTimeOffset>(31));
            if (!string.Equals(response.ContentDigest, reader.GetString(32), StringComparison.Ordinal))
            {
                throw new InvalidDataException("The stored post-Fill safety response is invalid.");
            }
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        var positions = await ReadPositionsAsync(connection, transaction, batch,
            cancellationToken).ConfigureAwait(false);
        if (!Equivalent(positions, incomingPositions))
        {
            throw new InvalidDataException("The Position observation contains conflicting positions.");
        }

        return new(batch, positions, evaluation, response, true);
    }

    private static async ValueTask<IReadOnlyList<PositionSnapshot>> ReadPositionsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PositionSnapshotBatch batch,
        CancellationToken cancellationToken)
    {
        var values = new List<PositionSnapshot>();
        await using var command = new NpgsqlCommand(
            """
            SELECT snapshot_id, instrument_identifier, quantity, average_entry_price,
                   market_value, currency, content_digest
            FROM position_snapshots WHERE batch_id = @batch_id
            ORDER BY instrument_identifier
            """, connection, transaction);
        command.Parameters.AddWithValue("batch_id", batch.Id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var value = PositionSnapshot.Create(new PositionSnapshotId(reader.GetGuid(0)), batch,
                batch.AccountBinding, reader.GetString(1), reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3), reader.GetDecimal(4), reader.GetString(5));
            if (!string.Equals(value.ContentDigest, reader.GetString(6), StringComparison.Ordinal))
            {
                throw new InvalidDataException("The stored Position snapshot is invalid.");
            }

            values.Add(value);
        }

        return values;
    }

    private static bool Equivalent(IReadOnlyList<PositionSnapshot> left,
        IReadOnlyList<PositionSnapshot> right) => left.Count == right.Count
        && left.OrderBy(item => item.InstrumentIdentifier, StringComparer.Ordinal)
            .Zip(right.OrderBy(item => item.InstrumentIdentifier, StringComparer.Ordinal))
            .All(pair => pair.First.InstrumentIdentifier == pair.Second.InstrumentIdentifier
                && pair.First.Quantity == pair.Second.Quantity
                && pair.First.AverageEntryPrice == pair.Second.AverageEntryPrice
                && pair.First.MarketValue == pair.Second.MarketValue
                && pair.First.Currency == pair.Second.Currency);

    private static void AddNullable(NpgsqlCommand command, string name,
        NpgsqlDbType type, object? value) => command.Parameters.Add(name, type).Value = value ?? DBNull.Value;

    private sealed record Authority(OrderId OrderId, string InstrumentIdentifier,
        decimal FilledQuantity, ExecutionIntentId IntentId, GoalVersionId GoalVersionId,
        SessionId SessionId, VersionReference RiskPolicyVersion,
        AccountAllocationEntryId AllocationEntryId, decimal AllocationAmount, string Currency,
        Domain.Sessions.SessionState SessionState, int SessionRevision);

    private sealed record OpenExposure(
        decimal Total,
        IReadOnlyDictionary<string, decimal> ByInstrument);
}
