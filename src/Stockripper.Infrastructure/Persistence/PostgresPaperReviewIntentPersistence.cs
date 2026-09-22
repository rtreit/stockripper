using System.Data;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Execution;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresPaperReviewIntentPersistence(NpgsqlDataSource dataSource)
    : IPaperReviewIntentPersistence, IPaperReviewExpiryPersistence
{
    public async ValueTask CreateAsync(
        RiskEvaluation riskEvaluation,
        ExecutionIntent intent,
        ExposureReservation? reservation,
        ReconciliationId reconciliationId,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        if (riskEvaluation.ReconciliationId != reconciliationId)
        {
            throw new InvalidDataException("The risk reconciliation binding is inconsistent.");
        }

        var automaticReservationRequired = intent.Mode == ExecutionMode.PAPER_AUTO
            && intent.State == ExecutionIntentState.RISK_ACCEPTED;
        if (automaticReservationRequired != (reservation is not null)
            || reservation is not null && intent.Mode != ExecutionMode.PAPER_AUTO)
        {
            throw new InvalidDataException(
                "The automatic exposure reservation does not match the Execution intent.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentSafetyAsync(
                connection,
                transaction,
                intent,
                reconciliationId,
                riskEvaluation.EvaluatedAt,
                cancellationToken).ConfigureAwait(false);
            await InsertRiskAsync(connection, transaction, riskEvaluation, cancellationToken)
                .ConfigureAwait(false);
            await InsertIntentAsync(connection, transaction, intent, cancellationToken).ConfigureAwait(false);
            if (reservation is not null)
            {
                await InsertReservationAsync(connection, transaction, intent, reservation,
                    reconciliationId, cancellationToken).ConfigureAwait(false);
            }
            await AppendRiskAuditAsync(
                connection,
                transaction,
                riskEvaluation,
                actorId,
                reconciliationId,
                cancellationToken).ConfigureAwait(false);
            await AppendIntentAuditAsync(
                connection,
                transaction,
                intent,
                actorId,
                cancellationToken).ConfigureAwait(false);
            if (reservation is not null)
            {
                await AppendReservationAuditAsync(connection, transaction, intent,
                    reservation, reconciliationId, actorId, cancellationToken)
                    .ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<ExecutionIntent?> FindIntentAsync(
        ExecutionIntentId intentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            IntentSelectSql + " WHERE intent_id = @intent_id",
            connection);
        command.Parameters.AddWithValue("intent_id", intentId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadIntent(reader) : null;
    }

    public async ValueTask<RiskEvaluation?> FindRiskEvaluationAsync(
        RiskEvaluationId riskEvaluationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            RiskSelectSql + " WHERE risk_evaluation_id = @risk_evaluation_id",
            connection);
        command.Parameters.AddWithValue("risk_evaluation_id", riskEvaluationId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRisk(reader) : null;
    }

    public async ValueTask RecordReviewAsync(
        ExecutionIntent reviewedIntent,
        ExecutionIntentState expectedState,
        ReviewApproval review,
        ExposureReservation? reservation,
        ReconciliationId? reconciliationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (review.Result == ReviewResult.APPROVED)
            {
                if (reservation is null || reconciliationId is null)
                {
                    throw new InvalidDataException(
                        "Approval needs a durable reconciliation and exposure reservation.");
                }

                await EnsureCurrentSafetyAsync(
                    connection,
                    transaction,
                    reviewedIntent,
                    reconciliationId.Value,
                    review.ActionTime,
                    cancellationToken).ConfigureAwait(false);
                await InsertReservationAsync(
                    connection,
                    transaction,
                    reviewedIntent,
                    reservation,
                    reconciliationId.Value,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (reservation is not null || reconciliationId is not null)
            {
                throw new InvalidDataException("A rejected review cannot reserve exposure.");
            }

            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO review_approvals (
                    approval_id, intent_id, reconciliation_id, reservation_id,
                    operator_id, result, action_time,
                    intent_digest, expires_at, operator_note, content_digest)
                VALUES (
                    @approval_id, @intent_id, @reconciliation_id, @reservation_id,
                    @operator_id, @result, @action_time,
                    @intent_digest, @expires_at, @operator_note, @content_digest)
                """,
                connection,
                transaction))
            {
                insert.Parameters.AddWithValue("approval_id", review.Id.Value);
                insert.Parameters.AddWithValue("intent_id", review.IntentId.Value);
                AddNullable(insert, "reconciliation_id", reconciliationId?.Value);
                AddNullable(insert, "reservation_id", reservation?.Id.Value);
                insert.Parameters.AddWithValue("operator_id", review.OperatorId.Value);
                insert.Parameters.AddWithValue("result", review.Result.ToString());
                insert.Parameters.AddWithValue("action_time", review.ActionTime);
                insert.Parameters.AddWithValue("intent_digest", review.IntentDigest);
                insert.Parameters.AddWithValue("expires_at", review.ExpiresAt);
                insert.Parameters.Add("operator_note", NpgsqlDbType.Text).Value =
                    (object?)review.OperatorNote ?? DBNull.Value;
                insert.Parameters.AddWithValue("content_digest", review.ContentDigest);
                _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var update = new NpgsqlCommand(
                """
                UPDATE execution_intents
                SET state = @state
                WHERE intent_id = @intent_id
                  AND state = @expected_state
                  AND content_digest = @content_digest
                """,
                connection,
                transaction))
            {
                update.Parameters.AddWithValue("state", reviewedIntent.State.ToString());
                update.Parameters.AddWithValue("intent_id", reviewedIntent.Id.Value);
                update.Parameters.AddWithValue("expected_state", expectedState.ToString());
                update.Parameters.AddWithValue("content_digest", reviewedIntent.ContentDigest);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new PaperReviewIntentConcurrencyException();
                }
            }

            await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.REVIEW_RECORDED,
                review.ActionTime,
                review.OperatorId,
                reviewedIntent.GoalVersionId,
                reviewedIntent.SessionId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["approvalId"] = review.Id.Value.ToString("D"),
                    ["intentDigest"] = review.IntentDigest,
                    ["intentId"] = review.IntentId.Value.ToString("D")
                },
                null,
                review.Result.ToString(),
                "Operator review recorded for the exact Execution intent digest.",
                "paper-review",
                cancellationToken).ConfigureAwait(false);
            await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.EXECUTION_INTENT_STATE_CHANGED,
                review.ActionTime,
                review.OperatorId,
                reviewedIntent.GoalVersionId,
                reviewedIntent.SessionId,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["approvalId"] = review.Id.Value.ToString("D"),
                    ["intentId"] = review.IntentId.Value.ToString("D")
                },
                expectedState.ToString(),
                reviewedIntent.State.ToString(),
                "Execution intent review state recorded.",
                "paper-review",
                cancellationToken).ConfigureAwait(false);
            if (reservation is not null)
            {
                await PostgresAuditWriter.AppendAsync(
                    connection,
                    transaction,
                    AuditEventType.EXPOSURE_RESERVED,
                    reservation.CreatedAt,
                    review.OperatorId,
                    reservation.GoalVersionId,
                    reviewedIntent.SessionId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["intentId"] = reservation.IntentId.Value.ToString("D"),
                        ["reconciliationId"] = reconciliationId!.Value.Value.ToString("D"),
                        ["reservationId"] = reservation.Id.Value.ToString("D")
                    },
                    null,
                    reservation.State.ToString(),
                    "Worst-case paper exposure reserved before submission.",
                    "paper-exposure-reservation",
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<ReviewApproval?> FindReviewAsync(
        ExecutionIntentId intentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT approval_id, intent_id, operator_id, result, action_time,
                   intent_digest, expires_at, operator_note, content_digest
            FROM review_approvals
            WHERE intent_id = @intent_id
            """,
            connection);
        command.Parameters.AddWithValue("intent_id", intentId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReviewApproval.Restore(
            new ReviewApprovalId(reader.GetGuid(0)),
            new ExecutionIntentId(reader.GetGuid(1)),
            new OperatorId(reader.GetGuid(2)),
            Enum.Parse<ReviewResult>(reader.GetString(3), ignoreCase: false),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8));
    }

    public async ValueTask<ReconciliationId?> FindReviewReconciliationAsync(
        ExecutionIntentId intentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT reconciliation_id FROM review_approvals WHERE intent_id = @intent_id",
            connection);
        command.Parameters.AddWithValue("intent_id", intentId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : new ReconciliationId((Guid)value);
    }

    public async ValueTask<int> ExpireDueAsync(
        DateTimeOffset at,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        if (at.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The review expiry time must use UTC.", nameof(at));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = new List<ExpiryCandidate>();
            await using (var select = new NpgsqlCommand(
                ExpirySelectSql +
                """
                 WHERE (intent.state = 'REVIEW_PENDING' AND intent.expires_at <= @at)
                    OR (intent.state = 'APPROVED' AND review.expires_at <= @at)
                 ORDER BY intent.expires_at, intent.intent_id
                 FOR UPDATE OF intent SKIP LOCKED
                """,
                connection,
                transaction))
            {
                select.Parameters.AddWithValue("at", at);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var intent = ReadIntent(reader);
                    ReviewApproval? review = null;
                    if (!reader.IsDBNull(21))
                    {
                        review = ReadReview(reader, 21);
                    }

                    candidates.Add(new ExpiryCandidate(intent, review));
                }
            }

            foreach (var candidate in candidates)
            {
                var expired = candidate.Intent.State == ExecutionIntentState.APPROVED
                    ? candidate.Intent.ExpireApproval(
                        at,
                        candidate.Review
                            ?? throw new InvalidDataException(
                                "The approved Execution intent has no Review approval."))
                    : candidate.Intent.Expire(at);

                await using (var update = new NpgsqlCommand(
                    """
                    UPDATE execution_intents
                    SET state = 'EXPIRED'
                    WHERE intent_id = @intent_id
                      AND state = @expected_state
                      AND content_digest = @content_digest
                    """,
                    connection,
                    transaction))
                {
                    update.Parameters.AddWithValue("intent_id", expired.Id.Value);
                    update.Parameters.AddWithValue(
                        "expected_state", candidate.Intent.State.ToString());
                    update.Parameters.AddWithValue("content_digest", expired.ContentDigest);
                    if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    {
                        throw new PaperReviewIntentConcurrencyException();
                    }
                }

                Guid? releasedReservationId;
                await using (var release = new NpgsqlCommand(
                    """
                    UPDATE exposure_reservations
                    SET remaining_amount = 0,
                        state = 'RELEASED',
                        released_at = @at,
                        release_reason = @reason,
                        revision = revision + 1
                    WHERE intent_id = @intent_id
                      AND state = 'RESERVED'
                    RETURNING reservation_id
                    """,
                    connection,
                    transaction))
                {
                    release.Parameters.AddWithValue("at", at);
                    release.Parameters.AddWithValue(
                        "reason", "Execution intent approval expired.");
                    release.Parameters.AddWithValue("intent_id", expired.Id.Value);
                    var value = await release.ExecuteScalarAsync(cancellationToken)
                        .ConfigureAwait(false);
                    releasedReservationId = value is null or DBNull ? null : (Guid)value;
                }

                if (candidate.Review is not null)
                {
                    await PostgresAuditWriter.AppendAsync(
                        connection,
                        transaction,
                        AuditEventType.REVIEW_RECORDED,
                        at,
                        actorId,
                        expired.GoalVersionId,
                        expired.SessionId,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["approvalId"] = candidate.Review.Id.Value.ToString("D"),
                            ["intentDigest"] = expired.ContentDigest,
                            ["intentId"] = expired.Id.Value.ToString("D")
                        },
                        candidate.Review.Result.ToString(),
                        ExecutionIntentState.EXPIRED.ToString(),
                        "Review approval expiry recorded.",
                        "paper-review-expiry",
                        cancellationToken).ConfigureAwait(false);
                }

                await PostgresAuditWriter.AppendAsync(
                    connection,
                    transaction,
                    AuditEventType.EXECUTION_INTENT_STATE_CHANGED,
                    at,
                    actorId,
                    expired.GoalVersionId,
                    expired.SessionId,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["intentId"] = expired.Id.Value.ToString("D")
                    },
                    candidate.Intent.State.ToString(),
                    expired.State.ToString(),
                    "Execution intent expiry recorded.",
                    "paper-review-expiry",
                    cancellationToken).ConfigureAwait(false);

                if (releasedReservationId.HasValue)
                {
                    await PostgresAuditWriter.AppendAsync(
                        connection,
                        transaction,
                        AuditEventType.EXPOSURE_RELEASED,
                        at,
                        actorId,
                        expired.GoalVersionId,
                        expired.SessionId,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["intentId"] = expired.Id.Value.ToString("D"),
                            ["reservationId"] = releasedReservationId.Value.ToString("D")
                        },
                        ExposureReservationState.RESERVED.ToString(),
                        ExposureReservationState.RELEASED.ToString(),
                        "Unused paper exposure released after Review approval expiry.",
                        "paper-review-expiry",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return candidates.Count;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask InsertRiskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RiskEvaluation risk,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO risk_evaluations (
                risk_evaluation_id, intent_id, decision_id, goal_version_id,
                session_id, risk_policy_version, allocation_entry_id,
                reconciliation_id,
                position_exposure, open_order_exposure, requested_exposure,
                buying_power, currency, checks, result, evaluated_at, content_digest)
            VALUES (
                @risk_evaluation_id, @intent_id, @decision_id, @goal_version_id,
                @session_id, @risk_policy_version, @allocation_entry_id,
                @reconciliation_id,
                @position_exposure, @open_order_exposure, @requested_exposure,
                @buying_power, @currency, @checks, @result, @evaluated_at, @content_digest)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("risk_evaluation_id", risk.Id.Value);
        command.Parameters.AddWithValue("intent_id", risk.IntentId.Value);
        command.Parameters.AddWithValue("decision_id", risk.DecisionId.Value);
        command.Parameters.AddWithValue("goal_version_id", risk.GoalVersionId.Value);
        command.Parameters.AddWithValue("session_id", risk.SessionId.Value);
        command.Parameters.Add("risk_policy_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(risk.RiskPolicyVersion);
        command.Parameters.AddWithValue("allocation_entry_id", risk.AllocationEntryId.Value);
        command.Parameters.AddWithValue("reconciliation_id", risk.ReconciliationId.Value);
        command.Parameters.AddWithValue("position_exposure", risk.PositionExposure);
        command.Parameters.AddWithValue("open_order_exposure", risk.OpenOrderExposure);
        command.Parameters.AddWithValue("requested_exposure", risk.RequestedExposure);
        command.Parameters.AddWithValue("buying_power", risk.BuyingPower);
        command.Parameters.AddWithValue("currency", risk.Currency);
        command.Parameters.Add("checks", NpgsqlDbType.Jsonb).Value = PersistenceJson.SerializeRiskChecks(risk.Checks);
        command.Parameters.AddWithValue("result", risk.Result.ToString());
        command.Parameters.AddWithValue("evaluated_at", risk.EvaluatedAt);
        command.Parameters.AddWithValue("content_digest", risk.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertIntentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExecutionIntent intent,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO execution_intents (
                intent_id, decision_id, goal_version_id, session_id, mode,
                broker_connection_version, environment, endpoint_identity,
                account_identifier, instrument_identifier, side, order_type,
                quantity, limit_price, time_in_force, expires_at,
                risk_evaluation_id, idempotency_key, created_at, state, content_digest)
            VALUES (
                @intent_id, @decision_id, @goal_version_id, @session_id, @mode,
                @broker_connection_version, @environment, @endpoint_identity,
                @account_identifier, @instrument_identifier, @side, @order_type,
                @quantity, @limit_price, @time_in_force, @expires_at,
                @risk_evaluation_id, @idempotency_key, @created_at, @state, @content_digest)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("intent_id", intent.Id.Value);
        command.Parameters.AddWithValue("decision_id", intent.DecisionId.Value);
        command.Parameters.AddWithValue("goal_version_id", intent.GoalVersionId.Value);
        command.Parameters.AddWithValue("session_id", intent.SessionId.Value);
        command.Parameters.AddWithValue("mode", intent.Mode.ToWireValue());
        command.Parameters.Add("broker_connection_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(intent.AccountBinding.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", intent.AccountBinding.Environment.ToString());
        command.Parameters.AddWithValue("endpoint_identity", intent.AccountBinding.EndpointIdentity);
        command.Parameters.AddWithValue("account_identifier", intent.AccountBinding.AccountIdentifier);
        command.Parameters.AddWithValue("instrument_identifier", intent.InstrumentIdentifier);
        command.Parameters.AddWithValue("side", intent.Side.ToString());
        command.Parameters.AddWithValue("order_type", intent.OrderType.ToString());
        command.Parameters.AddWithValue("quantity", intent.Quantity);
        command.Parameters.AddWithValue("limit_price", intent.LimitPrice);
        command.Parameters.AddWithValue("time_in_force", intent.TimeInForce.ToString());
        command.Parameters.AddWithValue("expires_at", intent.ExpiresAt);
        command.Parameters.AddWithValue("risk_evaluation_id", intent.RiskEvaluationId.Value);
        command.Parameters.AddWithValue("idempotency_key", intent.IdempotencyKey);
        command.Parameters.AddWithValue("created_at", intent.CreatedAt);
        command.Parameters.AddWithValue("state", intent.State.ToString());
        command.Parameters.AddWithValue("content_digest", intent.ContentDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AppendRiskAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RiskEvaluation risk,
        OperatorId actorId,
        ReconciliationId reconciliationId,
        CancellationToken cancellationToken) => await PostgresAuditWriter.AppendAsync(
        connection,
        transaction,
        AuditEventType.RISK_EVALUATED,
        risk.EvaluatedAt,
        actorId,
        risk.GoalVersionId,
        risk.SessionId,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["intentId"] = risk.IntentId.Value.ToString("D"),
            ["reconciliationId"] = reconciliationId.Value.ToString("D"),
            ["riskEvaluationId"] = risk.Id.Value.ToString("D"),
            ["riskEvaluationDigest"] = risk.ContentDigest
        },
        null,
        risk.Result.ToString(),
        "Deterministic risk evaluation recorded.",
        "paper-risk",
        cancellationToken).ConfigureAwait(false);

    private static async ValueTask AppendIntentAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExecutionIntent intent,
        OperatorId actorId,
        CancellationToken cancellationToken) => await PostgresAuditWriter.AppendAsync(
        connection,
        transaction,
        AuditEventType.EXECUTION_INTENT_CREATED,
        intent.CreatedAt,
        actorId,
        intent.GoalVersionId,
        intent.SessionId,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["decisionId"] = intent.DecisionId.Value.ToString("D"),
            ["intentDigest"] = intent.ContentDigest,
            ["intentId"] = intent.Id.Value.ToString("D"),
            ["riskEvaluationId"] = intent.RiskEvaluationId.Value.ToString("D")
        },
        null,
        intent.State.ToString(),
        "Immutable paper Execution intent recorded.",
        "paper-intent",
        cancellationToken).ConfigureAwait(false);

    private static async ValueTask AppendReservationAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExecutionIntent intent,
        ExposureReservation reservation,
        ReconciliationId reconciliationId,
        OperatorId actorId,
        CancellationToken cancellationToken) => await PostgresAuditWriter.AppendAsync(
        connection,
        transaction,
        AuditEventType.EXPOSURE_RESERVED,
        reservation.CreatedAt,
        actorId,
        reservation.GoalVersionId,
        intent.SessionId,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["intentId"] = reservation.IntentId.Value.ToString("D"),
            ["reconciliationId"] = reconciliationId.Value.ToString("D"),
            ["reservationId"] = reservation.Id.Value.ToString("D")
        },
        null,
        reservation.State.ToString(),
        "Worst-case paper exposure reserved before automatic submission.",
        "paper-exposure-reservation",
        cancellationToken).ConfigureAwait(false);

    private static async ValueTask EnsureCurrentSafetyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExecutionIntent intent,
        ReconciliationId reconciliationId,
        DateTimeOffset actionTime,
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
                      AND reconciliation.completed_at <= @action_time
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
                        OR (scope_type = 'GOAL' AND scope_identifier = @goal_scope)))
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId.Value);
        command.Parameters.AddWithValue("goal_version_id", intent.GoalVersionId.Value);
        command.Parameters.Add("broker_connection_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(intent.AccountBinding.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", intent.AccountBinding.Environment.ToString());
        command.Parameters.AddWithValue("endpoint_identity", intent.AccountBinding.EndpointIdentity);
        command.Parameters.AddWithValue("account_identifier", intent.AccountBinding.AccountIdentifier);
        command.Parameters.AddWithValue("action_time", actionTime);
        command.Parameters.AddWithValue("connection_digest", intent.AccountBinding.BrokerConnectionVersion.Digest);
        command.Parameters.AddWithValue("goal_scope", intent.GoalVersionId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!reader.GetBoolean(0) || reader.GetInt64(1) != 4)
        {
            throw new InvalidOperationException("Current durable execution safety evidence is invalid.");
        }
    }

    private static async ValueTask InsertReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExecutionIntent intent,
        ExposureReservation reservation,
        ReconciliationId reconciliationId,
        CancellationToken cancellationToken)
    {
        if (reservation.IntentId != intent.Id
            || reservation.GoalVersionId != intent.GoalVersionId
            || reservation.AccountBinding != intent.AccountBinding
            || reservation.State != ExposureReservationState.RESERVED
            || !reservation.HasValidContentDigest())
        {
            throw new InvalidDataException("The exposure reservation does not match the Execution intent.");
        }

        decimal allocationAmount;
        string allocationCurrency;
        await using (var allocationCommand = new NpgsqlCommand(
            """
            SELECT allocation_amount, currency
            FROM account_allocation_entries
            WHERE allocation_entry_id = @allocation_entry_id
              AND goal_version_id = @goal_version_id
              AND state = 'ACTIVE'
              AND broker_connection_version = @broker_connection_version
              AND environment = @environment
              AND endpoint_identity = @endpoint_identity
              AND account_identifier = @account_identifier
            FOR UPDATE
            """,
            connection,
            transaction))
        {
            allocationCommand.Parameters.AddWithValue("allocation_entry_id", reservation.AllocationEntryId.Value);
            allocationCommand.Parameters.AddWithValue("goal_version_id", reservation.GoalVersionId.Value);
            allocationCommand.Parameters.Add("broker_connection_version", NpgsqlDbType.Jsonb).Value =
                PersistenceJson.SerializeVersion(reservation.AccountBinding.BrokerConnectionVersion);
            allocationCommand.Parameters.AddWithValue("environment", reservation.AccountBinding.Environment.ToString());
            allocationCommand.Parameters.AddWithValue("endpoint_identity", reservation.AccountBinding.EndpointIdentity);
            allocationCommand.Parameters.AddWithValue("account_identifier", reservation.AccountBinding.AccountIdentifier);
            await using var reader = await allocationCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The exact account allocation is unavailable.");
            }

            allocationAmount = reader.GetDecimal(0);
            allocationCurrency = reader.GetString(1);
        }

        decimal reconciledExposure;
        await using (var reconciliationCommand = new NpgsqlCommand(
            """
            SELECT position_exposure + open_order_exposure
            FROM reconciliations
            WHERE reconciliation_id = @reconciliation_id
              AND result = 'CURRENT'
            FOR SHARE
            """,
            connection,
            transaction))
        {
            reconciliationCommand.Parameters.AddWithValue("reconciliation_id", reconciliationId.Value);
            var value = await reconciliationCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null or DBNull)
            {
                throw new InvalidOperationException("Current reconciliation exposure is unavailable.");
            }

            reconciledExposure = (decimal)value;
        }

        decimal activeReservations;
        await using (var reservationCommand = new NpgsqlCommand(
            """
            SELECT COALESCE(sum(remaining_amount), 0)
            FROM exposure_reservations
            WHERE allocation_entry_id = @allocation_entry_id
              AND state = 'RESERVED'
            """,
            connection,
            transaction))
        {
            reservationCommand.Parameters.AddWithValue(
                "allocation_entry_id",
                reservation.AllocationEntryId.Value);
            activeReservations = (decimal)(await reservationCommand.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) ?? 0m);
        }

        if (!string.Equals(reservation.Currency, allocationCurrency, StringComparison.Ordinal)
            || reconciledExposure + activeReservations + reservation.Amount > allocationAmount)
        {
            throw new InvalidOperationException("The durable exposure reservation exceeds account allocation.");
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO exposure_reservations (
                reservation_id, intent_id, goal_version_id, allocation_entry_id,
                broker_connection_version, environment, endpoint_identity,
                account_identifier, amount, remaining_amount, currency, created_at, state,
                released_at, release_reason, revision, content_digest)
            VALUES (
                @reservation_id, @intent_id, @goal_version_id, @allocation_entry_id,
                @broker_connection_version, @environment, @endpoint_identity,
                @account_identifier, @amount, @amount, @currency, @created_at, @state,
                NULL, NULL, @revision, @content_digest)
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("reservation_id", reservation.Id.Value);
        insert.Parameters.AddWithValue("intent_id", reservation.IntentId.Value);
        insert.Parameters.AddWithValue("goal_version_id", reservation.GoalVersionId.Value);
        insert.Parameters.AddWithValue("allocation_entry_id", reservation.AllocationEntryId.Value);
        insert.Parameters.Add("broker_connection_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(reservation.AccountBinding.BrokerConnectionVersion);
        insert.Parameters.AddWithValue("environment", reservation.AccountBinding.Environment.ToString());
        insert.Parameters.AddWithValue("endpoint_identity", reservation.AccountBinding.EndpointIdentity);
        insert.Parameters.AddWithValue("account_identifier", reservation.AccountBinding.AccountIdentifier);
        insert.Parameters.AddWithValue("amount", reservation.Amount);
        insert.Parameters.AddWithValue("currency", reservation.Currency);
        insert.Parameters.AddWithValue("created_at", reservation.CreatedAt);
        insert.Parameters.AddWithValue("state", reservation.State.ToString());
        insert.Parameters.AddWithValue("revision", reservation.Revision);
        insert.Parameters.AddWithValue("content_digest", reservation.ContentDigest);
        _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static RiskEvaluation ReadRisk(NpgsqlDataReader reader) => RiskEvaluation.Restore(
        new RiskEvaluationId(reader.GetGuid(0)),
        new ExecutionIntentId(reader.GetGuid(1)),
        new DecisionId(reader.GetGuid(2)),
        new GoalVersionId(reader.GetGuid(3)),
        new SessionId(reader.GetGuid(4)),
        PersistenceJson.DeserializeVersion(reader.GetString(5)),
        new AccountAllocationEntryId(reader.GetGuid(6)),
        new ReconciliationId(reader.GetGuid(7)),
        reader.GetDecimal(8),
        reader.GetDecimal(9),
        reader.GetDecimal(10),
        reader.GetDecimal(11),
        reader.GetString(12),
        PersistenceJson.DeserializeRiskChecks(reader.GetString(13)),
        Enum.Parse<RiskEvaluationResult>(reader.GetString(14), ignoreCase: false),
        reader.GetFieldValue<DateTimeOffset>(15),
        reader.GetString(16));

    private static ExecutionIntent ReadIntent(NpgsqlDataReader reader) => ExecutionIntent.Restore(
        new ExecutionIntentId(reader.GetGuid(0)),
        new DecisionId(reader.GetGuid(1)),
        new GoalVersionId(reader.GetGuid(2)),
        new SessionId(reader.GetGuid(3)),
        ExecutionModeRules.Parse(reader.GetString(4)),
        new IntentAccountBinding(
            PersistenceJson.DeserializeVersion(reader.GetString(5)),
            Enum.Parse<BrokerEnvironment>(reader.GetString(6), ignoreCase: false),
            reader.GetString(7),
            reader.GetString(8)),
        reader.GetString(9),
        Enum.Parse<OrderSide>(reader.GetString(10), ignoreCase: false),
        Enum.Parse<IntentOrderType>(reader.GetString(11), ignoreCase: false),
        reader.GetDecimal(12),
        reader.GetDecimal(13),
        Enum.Parse<IntentTimeInForce>(reader.GetString(14), ignoreCase: false),
        reader.GetFieldValue<DateTimeOffset>(15),
        new RiskEvaluationId(reader.GetGuid(16)),
        reader.GetString(17),
        reader.GetFieldValue<DateTimeOffset>(18),
        Enum.Parse<ExecutionIntentState>(reader.GetString(19), ignoreCase: false),
        reader.GetString(20));

    private static ReviewApproval ReadReview(NpgsqlDataReader reader, int offset) =>
        ReviewApproval.Restore(
            new ReviewApprovalId(reader.GetGuid(offset)),
            new ExecutionIntentId(reader.GetGuid(offset + 1)),
            new OperatorId(reader.GetGuid(offset + 2)),
            Enum.Parse<ReviewResult>(reader.GetString(offset + 3), ignoreCase: false),
            reader.GetFieldValue<DateTimeOffset>(offset + 4),
            reader.GetString(offset + 5),
            reader.GetFieldValue<DateTimeOffset>(offset + 6),
            reader.IsDBNull(offset + 7) ? null : reader.GetString(offset + 7),
            reader.GetString(offset + 8));

    private static void AddNullable(NpgsqlCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private const string RiskSelectSql = """
        SELECT risk_evaluation_id, intent_id, decision_id, goal_version_id,
               session_id, risk_policy_version::text, allocation_entry_id,
               reconciliation_id,
               position_exposure, open_order_exposure, requested_exposure,
               buying_power, currency, checks::text, result, evaluated_at, content_digest
        FROM risk_evaluations
        """;

    private const string IntentSelectSql = """
        SELECT intent_id, decision_id, goal_version_id, session_id, mode,
               broker_connection_version::text, environment, endpoint_identity,
               account_identifier, instrument_identifier, side, order_type,
               quantity, limit_price, time_in_force, expires_at,
               risk_evaluation_id, idempotency_key, created_at, state, content_digest
        FROM execution_intents
        """;

    private const string ExpirySelectSql = """
        SELECT intent.intent_id, intent.decision_id, intent.goal_version_id,
               intent.session_id, intent.mode,
               intent.broker_connection_version::text, intent.environment,
               intent.endpoint_identity, intent.account_identifier,
               intent.instrument_identifier, intent.side, intent.order_type,
               intent.quantity, intent.limit_price, intent.time_in_force,
               intent.expires_at, intent.risk_evaluation_id,
               intent.idempotency_key, intent.created_at, intent.state,
               intent.content_digest,
               review.approval_id, review.intent_id, review.operator_id, review.result,
               review.action_time, review.intent_digest, review.expires_at,
               review.operator_note, review.content_digest
        FROM execution_intents AS intent
        LEFT JOIN review_approvals AS review
          ON review.intent_id = intent.intent_id
        """;

    private sealed record ExpiryCandidate(
        ExecutionIntent Intent,
        ReviewApproval? Review);
}
