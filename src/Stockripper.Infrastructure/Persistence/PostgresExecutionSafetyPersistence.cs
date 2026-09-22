using System.Data;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Stockripper.Application.Execution;
using Stockripper.Domain.Audit;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Infrastructure.Audit;

namespace Stockripper.Infrastructure.Persistence;

public sealed class PostgresExecutionSafetyPersistence(NpgsqlDataSource dataSource)
    : IExecutionSafetyPersistence
{
    public async ValueTask ConfigureKillSwitchesAsync(
        IReadOnlyList<KillSwitch> killSwitches,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var killSwitch in killSwitches)
            {
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO kill_switches (
                        kill_switch_id, scope_type, scope_identifier, state, revision,
                        configured_at, triggered_at, trigger_reason,
                        recovery_reconciliation_id, recovery_required_at, cleared_at)
                    VALUES (
                        @kill_switch_id, @scope_type, @scope_identifier, @state, @revision,
                        @configured_at, NULL, NULL, NULL, NULL, NULL)
                    ON CONFLICT (scope_type, scope_identifier) DO NOTHING
                    """,
                    connection,
                    transaction);
                command.Parameters.AddWithValue("kill_switch_id", killSwitch.Id.Value);
                command.Parameters.AddWithValue("scope_type", killSwitch.ScopeType.ToString());
                command.Parameters.AddWithValue("scope_identifier", killSwitch.ScopeIdentifier);
                command.Parameters.AddWithValue("state", killSwitch.State.ToString());
                command.Parameters.AddWithValue("revision", killSwitch.Revision);
                command.Parameters.AddWithValue("configured_at", killSwitch.ConfiguredAt);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                {
                    await AppendKillSwitchAuditAsync(
                        connection,
                        transaction,
                        AuditEventType.KILL_SWITCH_CONFIGURED,
                        killSwitch,
                        actorId,
                        null,
                        killSwitch.State,
                        killSwitch.ConfiguredAt,
                        "Durable paper kill switch configured.",
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<KillSwitch>> FindKillSwitchesAsync(
        IReadOnlyList<KillSwitchScopeReference> scopes,
        CancellationToken cancellationToken)
    {
        if (scopes.Count == 0)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var predicates = new List<string>(scopes.Count);
        await using var command = new NpgsqlCommand { Connection = connection };
        for (var index = 0; index < scopes.Count; index++)
        {
            predicates.Add($"(scope_type = @scope_type_{index} AND scope_identifier = @scope_identifier_{index})");
            command.Parameters.AddWithValue($"scope_type_{index}", scopes[index].ScopeType.ToString());
            command.Parameters.AddWithValue($"scope_identifier_{index}", scopes[index].ScopeIdentifier);
        }

        command.CommandText = KillSwitchSelectSql + " WHERE " + string.Join(" OR ", predicates);
        var values = new List<KillSwitch>(scopes.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(ReadKillSwitch(reader));
        }

        return values;
    }

    public async ValueTask<KillSwitch?> FindKillSwitchAsync(
        KillSwitchId killSwitchId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            KillSwitchSelectSql + " WHERE kill_switch_id = @kill_switch_id",
            connection);
        command.Parameters.AddWithValue("kill_switch_id", killSwitchId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadKillSwitch(reader)
            : null;
    }

    public async ValueTask UpdateKillSwitchAsync(
        KillSwitch killSwitch,
        KillSwitchState expectedState,
        int expectedRevision,
        OperatorId actorId,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE kill_switches
                SET state = @state, revision = @revision, triggered_at = @triggered_at,
                    trigger_reason = @trigger_reason,
                    recovery_reconciliation_id = @recovery_reconciliation_id,
                    recovery_required_at = @recovery_required_at, cleared_at = @cleared_at
                WHERE kill_switch_id = @kill_switch_id
                  AND state = @expected_state
                  AND revision = @expected_revision
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("state", killSwitch.State.ToString());
            command.Parameters.AddWithValue("revision", killSwitch.Revision);
            AddNullable(command, "triggered_at", killSwitch.TriggeredAt);
            AddNullable(command, "trigger_reason", killSwitch.TriggerReason);
            AddNullable(command, "recovery_reconciliation_id", killSwitch.RecoveryReconciliationId?.Value);
            AddNullable(command, "recovery_required_at", killSwitch.RecoveryRequiredAt);
            AddNullable(command, "cleared_at", killSwitch.ClearedAt);
            command.Parameters.AddWithValue("kill_switch_id", killSwitch.Id.Value);
            command.Parameters.AddWithValue("expected_state", expectedState.ToString());
            command.Parameters.AddWithValue("expected_revision", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ExecutionSafetyConcurrencyException();
            }

            await AppendKillSwitchAuditAsync(
                connection,
                transaction,
                AuditEventType.KILL_SWITCH_STATE_CHANGED,
                killSwitch,
                actorId,
            expectedState,
            killSwitch.State,
            changedAt,
            killSwitch.State switch
            {
                KillSwitchState.TRIGGERED => killSwitch.TriggerReason!,
                KillSwitchState.RECOVERY_REQUIRED =>
                    "Current reconciliation recorded for kill-switch recovery.",
                KillSwitchState.CLEAR => "Operator cleared the reconciled kill switch.",
                _ => throw new InvalidOperationException("The kill-switch state is invalid.")
            },
            cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask InsertReconciliationAsync(
        ReconciliationRecord reconciliation,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        if (!reconciliation.HasValidContentDigest())
        {
            throw new InvalidDataException("The reconciliation digest is invalid.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO reconciliations (
                    reconciliation_id, goal_version_id, broker_connection_version,
                    environment, endpoint_identity, account_identifier,
                    reconciliation_policy_version, started_at, completed_at, observed_at,
                    position_exposure, open_order_exposure, buying_power, currency,
                    compared_order_range, compared_fill_range,
                    compared_position_snapshot, differences, corrections,
                    operator_action, result, content_digest)
                VALUES (
                    @reconciliation_id, @goal_version_id, @broker_connection_version,
                    @environment, @endpoint_identity, @account_identifier,
                    @reconciliation_policy_version, @started_at, @completed_at, @observed_at,
                    @position_exposure, @open_order_exposure, @buying_power, @currency,
                    @compared_order_range, @compared_fill_range,
                    @compared_position_snapshot, @differences, @corrections,
                    @operator_action, @result, @content_digest)
                """,
                connection,
                transaction);
            AddReconciliationParameters(command, reconciliation);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.RECONCILIATION_COMPLETED,
                reconciliation.CompletedAt,
                actorId,
                reconciliation.GoalVersionId,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reconciliationDigest"] = reconciliation.ContentDigest,
                    ["reconciliationId"] = reconciliation.Id.Value.ToString("D"),
                    ["comparedOrderRange"] = reconciliation.ComparedOrderRange,
                    ["comparedFillRange"] = reconciliation.ComparedFillRange,
                    ["comparedPositionSnapshot"] = reconciliation.ComparedPositionSnapshot,
                    ["differenceCount"] = reconciliation.Differences.Length.ToString(
                        CultureInfo.InvariantCulture),
                    ["correctionCount"] = reconciliation.Corrections.Length.ToString(
                        CultureInfo.InvariantCulture)
                },
                null,
                reconciliation.Result.ToString(),
                "Durable paper account reconciliation completed.",
                "paper-reconciliation",
                cancellationToken).ConfigureAwait(false);
            if (reconciliation.Result != ReconciliationResult.CURRENT)
            {
                await LockExecutionForReconciliationAsync(connection, transaction,
                    reconciliation, actorId, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask RecordReconciliationStartedAsync(
        ReconciliationId reconciliationId,
        GoalVersionId goalVersionId,
        IntentAccountBinding accountBinding,
        OperatorId actorId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.RECONCILIATION_STARTED,
                startedAt,
                actorId,
                goalVersionId,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reconciliationId"] = reconciliationId.Value.ToString("D"),
                    ["connectionDigest"] = accountBinding.BrokerConnectionVersion.Digest,
                    ["environment"] = accountBinding.Environment.ToString()
                },
                null,
                "STARTED",
                "Detailed paper reconciliation started.",
                "paper-reconciliation",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<ReconciliationRecord?> FindReconciliationAsync(
        ReconciliationId reconciliationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            ReconciliationSelectSql + " WHERE reconciliation_id = @reconciliation_id",
            connection);
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadReconciliation(reader)
            : null;
    }

    public async ValueTask<ReconciliationRecord?> FindLatestCurrentReconciliationAsync(
        GoalVersionId goalVersionId,
        IntentAccountBinding accountBinding,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            ReconciliationSelectSql +
            """
             WHERE goal_version_id = @goal_version_id
               AND broker_connection_version = @broker_connection_version
               AND environment = @environment
               AND endpoint_identity = @endpoint_identity
               AND account_identifier = @account_identifier
             ORDER BY sequence DESC
             LIMIT 1
            """,
            connection);
        command.Parameters.AddWithValue("goal_version_id", goalVersionId.Value);
        command.Parameters.Add("broker_connection_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(accountBinding.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", accountBinding.Environment.ToString());
        command.Parameters.AddWithValue("endpoint_identity", accountBinding.EndpointIdentity);
        command.Parameters.AddWithValue("account_identifier", accountBinding.AccountIdentifier);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var latest = ReadReconciliation(reader);
        return latest.Result == ReconciliationResult.CURRENT ? latest : null;
    }

    private static async ValueTask LockExecutionForReconciliationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ReconciliationRecord reconciliation,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        const string reason = "Reconciliation did not confirm current Broker truth.";
        var lockedSessions = new List<(SessionId Id, string PreviousState)>();
        await using (var command = new NpgsqlCommand(
            """
            WITH candidates AS (
                SELECT session_id, state
                FROM sessions
                WHERE goal_version_id = @goal_version_id
                  AND state IN ('READY', 'RUNNING', 'STOPPING', 'RECONCILING')
                FOR UPDATE
            ), updated AS (
                UPDATE sessions AS session
                SET state = 'LOCKED', revision = revision + 1
                FROM candidates
                WHERE session.session_id = candidates.session_id
                RETURNING session.session_id, candidates.state
            )
            SELECT session_id, state FROM updated
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("goal_version_id", reconciliation.GoalVersionId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lockedSessions.Add((new SessionId(reader.GetGuid(0)), reader.GetString(1)));
            }
        }

        foreach (var session in lockedSessions)
        {
            await PostgresAuditWriter.AppendAsync(connection, transaction,
                AuditEventType.SESSION_STATE_CHANGED, reconciliation.CompletedAt, actorId,
                reconciliation.GoalVersionId, session.Id,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reconciliationId"] = reconciliation.Id.Value.ToString("D")
                }, session.PreviousState, Domain.Sessions.SessionState.LOCKED.ToString(),
                reason, "paper-reconciliation", cancellationToken).ConfigureAwait(false);
        }

        var switches = new List<KillSwitch>();
        await using (var command = new NpgsqlCommand(KillSwitchSelectSql +
            """
             WHERE (scope_type = 'ACCOUNT' AND scope_identifier = @account_identifier)
                OR (scope_type = 'GOAL' AND scope_identifier = @goal_version_id)
             FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("account_identifier",
                reconciliation.AccountBinding.AccountIdentifier);
            command.Parameters.AddWithValue("goal_version_id",
                reconciliation.GoalVersionId.Value.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                switches.Add(ReadKillSwitch(reader));
            }
        }

        foreach (var current in switches.Where(item => item.State != KillSwitchState.TRIGGERED))
        {
            var triggered = current.Trigger(reconciliation.CompletedAt, reason);
            await using var command = new NpgsqlCommand(
                """
                UPDATE kill_switches
                SET state = @state, revision = @revision, triggered_at = @triggered_at,
                    trigger_reason = @trigger_reason,
                    recovery_reconciliation_id = NULL,
                    recovery_required_at = NULL, cleared_at = NULL
                WHERE kill_switch_id = @kill_switch_id
                  AND state = @expected_state AND revision = @expected_revision
                """, connection, transaction);
            command.Parameters.AddWithValue("state", triggered.State.ToString());
            command.Parameters.AddWithValue("revision", triggered.Revision);
            command.Parameters.AddWithValue("triggered_at", triggered.TriggeredAt!.Value);
            command.Parameters.AddWithValue("trigger_reason", triggered.TriggerReason!);
            command.Parameters.AddWithValue("kill_switch_id", triggered.Id.Value);
            command.Parameters.AddWithValue("expected_state", current.State.ToString());
            command.Parameters.AddWithValue("expected_revision", current.Revision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ExecutionSafetyConcurrencyException();
            }

            await AppendKillSwitchAuditAsync(connection, transaction,
                AuditEventType.KILL_SWITCH_STATE_CHANGED, triggered, actorId,
                current.State, triggered.State, reconciliation.CompletedAt,
                reason, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<ExposureReservation?> FindReservationAsync(
        ExecutionIntentId intentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            ReservationSelectSql + " WHERE intent_id = @intent_id",
            connection);
        command.Parameters.AddWithValue("intent_id", intentId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadReservation(reader)
            : null;
    }

    public async ValueTask ReleaseReservationAsync(
        ExposureReservation reservation,
        int expectedRevision,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        if (reservation.State != ExposureReservationState.RELEASED
            || !reservation.HasValidContentDigest())
        {
            throw new InvalidDataException("The reservation release is invalid.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand(
                """
                UPDATE exposure_reservations
                SET state = @state, released_at = @released_at,
                    release_reason = @release_reason, remaining_amount = 0, revision = @revision
                WHERE reservation_id = @reservation_id
                  AND state = 'RESERVED'
                  AND revision = @expected_revision
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("state", reservation.State.ToString());
            command.Parameters.AddWithValue("released_at", reservation.ReleasedAt!.Value);
            command.Parameters.AddWithValue("release_reason", reservation.ReleaseReason!);
            command.Parameters.AddWithValue("revision", reservation.Revision);
            command.Parameters.AddWithValue("reservation_id", reservation.Id.Value);
            command.Parameters.AddWithValue("expected_revision", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ExecutionSafetyConcurrencyException();
            }

            await PostgresAuditWriter.AppendAsync(
                connection,
                transaction,
                AuditEventType.EXPOSURE_RELEASED,
                reservation.ReleasedAt.Value,
                actorId,
                reservation.GoalVersionId,
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["intentId"] = reservation.IntentId.Value.ToString("D"),
                    ["reservationId"] = reservation.Id.Value.ToString("D")
                },
                ExposureReservationState.RESERVED.ToString(),
                reservation.State.ToString(),
                reservation.ReleaseReason!,
                "paper-exposure-reservation",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static void AddReconciliationParameters(NpgsqlCommand command, ReconciliationRecord value)
    {
        command.Parameters.AddWithValue("reconciliation_id", value.Id.Value);
        command.Parameters.AddWithValue("goal_version_id", value.GoalVersionId.Value);
        command.Parameters.Add("broker_connection_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(value.AccountBinding.BrokerConnectionVersion);
        command.Parameters.AddWithValue("environment", value.AccountBinding.Environment.ToString());
        command.Parameters.AddWithValue("endpoint_identity", value.AccountBinding.EndpointIdentity);
        command.Parameters.AddWithValue("account_identifier", value.AccountBinding.AccountIdentifier);
        command.Parameters.Add("reconciliation_policy_version", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeVersion(value.ReconciliationPolicyVersion);
        command.Parameters.AddWithValue("started_at", value.StartedAt);
        command.Parameters.AddWithValue("completed_at", value.CompletedAt);
        command.Parameters.AddWithValue("observed_at", value.ObservedAt);
        command.Parameters.AddWithValue("position_exposure", value.PositionExposure);
        command.Parameters.AddWithValue("open_order_exposure", value.OpenOrderExposure);
        command.Parameters.AddWithValue("buying_power", value.BuyingPower);
        command.Parameters.AddWithValue("currency", value.Currency);
        command.Parameters.AddWithValue("compared_order_range", value.ComparedOrderRange);
        command.Parameters.AddWithValue("compared_fill_range", value.ComparedFillRange);
        command.Parameters.AddWithValue(
            "compared_position_snapshot", value.ComparedPositionSnapshot);
        command.Parameters.Add("differences", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeStrings(value.Differences);
        command.Parameters.Add("corrections", NpgsqlDbType.Jsonb).Value =
            PersistenceJson.SerializeStrings(value.Corrections);
        command.Parameters.Add("operator_action", NpgsqlDbType.Text).Value =
            (object?)value.OperatorAction ?? DBNull.Value;
        command.Parameters.AddWithValue("result", value.Result.ToString());
        command.Parameters.AddWithValue("content_digest", value.ContentDigest);
    }

    private static KillSwitch ReadKillSwitch(NpgsqlDataReader reader) => KillSwitch.Restore(
        new KillSwitchId(reader.GetGuid(0)),
        Enum.Parse<KillSwitchScopeType>(reader.GetString(1), ignoreCase: false),
        reader.GetString(2),
        Enum.Parse<KillSwitchState>(reader.GetString(3), ignoreCase: false),
        reader.GetInt32(4),
        reader.GetFieldValue<DateTimeOffset>(5),
        ReadTime(reader, 6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : new ReconciliationId(reader.GetGuid(8)),
        ReadTime(reader, 9),
        ReadTime(reader, 10));

    private static ReconciliationRecord ReadReconciliation(NpgsqlDataReader reader) =>
        ReconciliationRecord.Restore(
            new ReconciliationId(reader.GetGuid(0)),
            new GoalVersionId(reader.GetGuid(1)),
            new IntentAccountBinding(
                PersistenceJson.DeserializeVersion(reader.GetString(2)),
                Enum.Parse<BrokerEnvironment>(reader.GetString(3), ignoreCase: false),
                reader.GetString(4),
                reader.GetString(5)),
            PersistenceJson.DeserializeVersion(reader.GetString(6)),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetDecimal(10),
            reader.GetDecimal(11),
            reader.GetDecimal(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            PersistenceJson.DeserializeStrings(reader.GetString(17)),
            PersistenceJson.DeserializeStrings(reader.GetString(18)),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            Enum.Parse<ReconciliationResult>(reader.GetString(20), ignoreCase: false),
            reader.GetString(21));

    internal static ExposureReservation ReadReservation(NpgsqlDataReader reader) =>
        ExposureReservation.Restore(
            new ExposureReservationId(reader.GetGuid(0)),
            new ExecutionIntentId(reader.GetGuid(1)),
            new GoalVersionId(reader.GetGuid(2)),
            new AccountAllocationEntryId(reader.GetGuid(3)),
            new IntentAccountBinding(
                PersistenceJson.DeserializeVersion(reader.GetString(4)),
                Enum.Parse<BrokerEnvironment>(reader.GetString(5), ignoreCase: false),
            reader.GetString(6),
            reader.GetString(7)),
            reader.GetDecimal(8),
            reader.GetDecimal(9),
            reader.GetString(10),
            reader.GetFieldValue<DateTimeOffset>(11),
            Enum.Parse<ExposureReservationState>(reader.GetString(12), ignoreCase: false),
            ReadTime(reader, 13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.GetInt32(15),
            reader.GetString(16));

    private static async ValueTask AppendKillSwitchAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuditEventType eventType,
        KillSwitch killSwitch,
        OperatorId actorId,
        KillSwitchState? previousState,
        KillSwitchState newState,
        DateTimeOffset changedAt,
        string reason,
        CancellationToken cancellationToken) => await PostgresAuditWriter.AppendAsync(
        connection,
        transaction,
        eventType,
        changedAt,
        actorId,
        killSwitch.ScopeType == KillSwitchScopeType.GOAL
            ? new GoalVersionId(Guid.Parse(killSwitch.ScopeIdentifier))
            : null,
        null,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["killSwitchId"] = killSwitch.Id.Value.ToString("D"),
            ["scopeType"] = killSwitch.ScopeType.ToString()
        },
        previousState?.ToString(),
        newState.ToString(),
        reason,
        "paper-kill-switch",
        cancellationToken).ConfigureAwait(false);

    private static DateTimeOffset? ReadTime(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null
        : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void AddNullable(NpgsqlCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private const string KillSwitchSelectSql = """
        SELECT kill_switch_id, scope_type, scope_identifier, state, revision,
               configured_at, triggered_at, trigger_reason,
               recovery_reconciliation_id, recovery_required_at, cleared_at
        FROM kill_switches
        """;

    private const string ReconciliationSelectSql = """
        SELECT reconciliation_id, goal_version_id, broker_connection_version::text,
               environment, endpoint_identity, account_identifier,
               reconciliation_policy_version::text, started_at, completed_at, observed_at,
               position_exposure, open_order_exposure, buying_power, currency,
               compared_order_range, compared_fill_range, compared_position_snapshot,
               differences::text, corrections::text, operator_action, result, content_digest
        FROM reconciliations
        """;

    internal const string ReservationSelectSql = """
        SELECT reservation_id, intent_id, goal_version_id, allocation_entry_id,
               broker_connection_version::text, environment, endpoint_identity,
               account_identifier, amount, remaining_amount, currency, created_at, state,
               released_at, release_reason, revision, content_digest
        FROM exposure_reservations
        """;
}

public sealed class ExecutionSafetyConcurrencyException()
    : InvalidOperationException("The durable execution safety state changed before this operation completed.");
