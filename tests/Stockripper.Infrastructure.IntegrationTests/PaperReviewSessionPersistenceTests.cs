using Npgsql;
using Stockripper.Application.Brokers;
using Stockripper.Application.Data;
using Stockripper.Application.Decisions;
using Stockripper.Application.Execution;
using Stockripper.Application.Goals;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;
using Stockripper.Infrastructure.Persistence;
using Stockripper.Testing;

namespace Stockripper.Infrastructure.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class PaperReviewSessionPersistenceTests(PostgresServerFixture server)
{
    [Fact]
    public async Task Ready_preflight_survives_restart_and_restart_locks_session()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);

        var outcome = await context.Service.StartAsync(
            context.Goal.Id,
            context.Activation.Id,
            context.Snapshot.Id,
            context.Actor);

        Assert.Equal(SessionState.READY, outcome.Session.State);
        await database.ReopenApplicationAsync();
        var restoredSession = await database.SessionPersistence.FindAsync(outcome.Session.Id, default);
        var restoredPreflight = await database.ExecutablePreflightPersistence.FindAsync(
            outcome.Session.Id,
            default);
        Assert.Equal(outcome.Session, restoredSession);
        Assert.NotNull(restoredPreflight);
        Assert.Equal(outcome.Preflight.Id, restoredPreflight.Id);
        Assert.Equal(outcome.Preflight.ContentDigest, restoredPreflight.ContentDigest);
        Assert.Equal(outcome.Preflight.Result, restoredPreflight.Result);
        Assert.Equal(
            outcome.Preflight.ObservedCapabilities.ToArray(),
            restoredPreflight.ObservedCapabilities.ToArray());
        Assert.Equal(outcome.Preflight.Checks.ToArray(), restoredPreflight.Checks.ToArray());
        Assert.True(restoredPreflight.HasValidContentDigest());

        context.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await new ExecutableSessionRecoveryService(
            database.SessionPersistence,
            database.SessionPersistence,
            context.Clock).RecoverAsync(context.Actor);
        var locked = await database.SessionPersistence.FindAsync(outcome.Session.Id, default);
        Assert.Equal(1, recovered);
        Assert.NotNull(locked);
        Assert.Equal(SessionState.LOCKED, locked.State);
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Failed_preflight_is_durable_and_immutable()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database, wrongAccount: true);

        var outcome = await context.Service.StartAsync(
            context.Goal.Id,
            context.Activation.Id,
            context.Snapshot.Id,
            context.Actor);

        Assert.Equal(SessionState.FAILED, outcome.Session.State);
        Assert.Equal(ExecutablePreflightResult.FAILED, outcome.Preflight.Result);
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "UPDATE executable_session_preflights SET result = 'PASSED'"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "DELETE FROM executable_session_preflights"));
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Missing_durable_kill_switch_scope_blocks_session_creation()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database, configureSafety: false);

        await Assert.ThrowsAsync<PaperReviewSessionPreflightException>(() => context.Service.StartAsync(
            context.Goal.Id,
            context.Activation.Id,
            context.Snapshot.Id,
            context.Actor).AsTask());

        Assert.Equal(0L, await ExecuteScalarInt64Async(
            database.DataSource,
            "SELECT count(*) FROM sessions"));
    }

    [Fact]
    public async Task Kill_switch_survives_restart_and_requires_reconciliation_before_clear()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var accountBinding = ExecutionSafetyService.ToAccountBinding(context.Goal);
        var scopes = PaperExecutionSafetyScopes.Required(context.Goal.Id, accountBinding);
        var switches = await database.ExecutionSafetyPersistence.FindKillSwitchesAsync(scopes, default);
        var accountSwitch = switches.Single(value => value.ScopeType == KillSwitchScopeType.ACCOUNT);
        var safetyService = new ExecutionSafetyService(
            database.GoalPersistence,
            database.ExecutionSafetyPersistence,
            context.Clock);
        var triggered = await safetyService.TriggerAsync(
            accountSwitch.Id,
            context.Actor,
            "Operator stopped paper execution for reconciliation.");

        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            $"UPDATE kill_switches SET state = 'CLEAR', revision = revision + 1 WHERE kill_switch_id = '{triggered.Id.Value:D}'"));
        await Assert.ThrowsAsync<PaperReviewSessionPreflightException>(() => context.Service.StartAsync(
            context.Goal.Id,
            context.Activation.Id,
            context.Snapshot.Id,
            context.Actor).AsTask());

        await database.ReopenApplicationAsync();
        var restored = await database.ExecutionSafetyPersistence.FindKillSwitchAsync(triggered.Id, default);
        Assert.NotNull(restored);
        Assert.Equal(KillSwitchState.TRIGGERED, restored.State);

        context.Clock.Advance(TimeSpan.FromMinutes(1));
        safetyService = new ExecutionSafetyService(
            database.GoalPersistence,
            database.ExecutionSafetyPersistence,
            context.Clock);
        var reconciliation = await CreateReconciliationRunner(
            database, context).ReconcileAsync(context.Goal.Id, context.Actor);
        var recovery = await safetyService.RequireRecoveryAsync(
            triggered.Id,
            reconciliation.Id,
            context.Actor);
        Assert.Equal(KillSwitchState.RECOVERY_REQUIRED, recovery.State);
        var cleared = await safetyService.ClearAsync(triggered.Id, context.Actor);
        Assert.Equal(KillSwitchState.CLEAR, cleared.State);

        await database.ReopenApplicationAsync();
        restored = await database.ExecutionSafetyPersistence.FindKillSwitchAsync(triggered.Id, default);
        Assert.NotNull(restored);
        Assert.Equal(KillSwitchState.CLEAR, restored.State);
        Assert.Equal(reconciliation.Id, restored.RecoveryReconciliationId);
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Newer_reconciliation_difference_invalidates_current_truth_and_locks_execution()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var sessionOutcome = await context.Service.StartAsync(
            context.Goal.Id, context.Activation.Id, context.Snapshot.Id, context.Actor);
        var binding = ExecutionSafetyService.ToAccountBinding(context.Goal);
        Assert.NotNull(await database.ExecutionSafetyPersistence
            .FindLatestCurrentReconciliationAsync(context.Goal.Id, binding, default));
        var difference = await CreateReconciliationRunner(
            database, context, includeExternalOrder: true).ReconcileAsync(
            context.Goal.Id, context.Actor);

        Assert.Equal(ReconciliationResult.DIFFERENCE_LOCKED, difference.Result);
        Assert.Null(await database.ExecutionSafetyPersistence
            .FindLatestCurrentReconciliationAsync(context.Goal.Id, binding, default));
        var session = await database.SessionPersistence.FindAsync(
            sessionOutcome.Session.Id, default);
        Assert.NotNull(session);
        Assert.Equal(SessionState.LOCKED, session.State);
        var switches = await database.ExecutionSafetyPersistence.FindKillSwitchesAsync(
            PaperExecutionSafetyScopes.Required(context.Goal.Id, binding), default);
        Assert.All(switches.Where(item => item.ScopeType is KillSwitchScopeType.ACCOUNT
            or KillSwitchScopeType.GOAL),
            item => Assert.Equal(KillSwitchState.TRIGGERED, item.State));
        var operatorSnapshot = await new PostgresPaperOperatorReadModel(database.DataSource)
            .ReadLatestAsync();
        Assert.NotNull(operatorSnapshot);
        Assert.Equal(ReconciliationResult.DIFFERENCE_LOCKED.ToString(),
            operatorSnapshot.ReconciliationState);
        Assert.Contains("Broker reconciliation is not current.",
            operatorSnapshot.Alerts);
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Intent_risk_and_exact_review_survive_restart_and_reject_tampering()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var sessionOutcome = await context.Service.StartAsync(
            context.Goal.Id,
            context.Activation.Id,
            context.Snapshot.Id,
            context.Actor);
        var running = sessionOutcome.Session.TransitionTo(SessionState.RUNNING, context.Clock.GetUtcNow());
        await database.SessionPersistence.UpdateAsync(
            running,
            sessionOutcome.Session.State,
            sessionOutcome.Session.Revision,
            context.Actor,
            context.Clock.GetUtcNow(),
            default);
        var decision = Decision.Create(
            DecisionId.New(),
            running.Id,
            context.Goal.Id,
            context.Goal.StrategyVersion,
            context.Snapshot.Id,
            context.Clock.GetUtcNow(),
            DecisionAction.BUY,
            "AAPL",
            10m,
            100m,
            "Buy inside exact paper authority.",
            0.8m,
            [new DecisionGuardResult("EXECUTABLE_OUTPUT", true, "Executable output is valid.")]);
        await database.DecisionPersistence.InsertAsync(decision, context.Actor, default);
        var riskProbe = new FakeIntentRiskProbe(new IntentRiskObservation(
            new BrokerConnectionObservation(
                BrokerEnvironment.PAPER,
                context.Goal.ExecutionBinding!.EndpointIdentity,
                context.Goal.ExecutionBinding.ExpectedAccountIdentifier,
                context.Goal.ExecutionBinding.RequiredCapabilities,
                context.Clock.GetUtcNow()),
            true,
            true,
            true,
            context.Clock.GetUtcNow(),
            0m,
            0m,
            25_000m,
            "USD"));
        var intentService = new PaperReviewIntentService(
            database.GoalPersistence,
            database.ActivationPersistence,
            database.AccountAllocationPersistence,
            database.SessionPersistence,
            database.ExecutablePreflightPersistence,
            database.DecisionPersistence,
            riskProbe,
            database.ExecutionSafetyPersistence,
            CreateReconciliationRunner(database, context),
            database.PaperReviewIntentPersistence,
            new FixedPaperRiskPolicyProvider(),
            context.Clock);
        var intentOutcome = await intentService.CreateAsync(
            decision.Id,
            10m,
            100m,
            context.Clock.GetUtcNow().AddMinutes(5),
            context.Actor);

        Assert.Equal(ExecutionIntentState.REVIEW_PENDING, intentOutcome.Intent.State);
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "UPDATE execution_intents SET state = 'APPROVED'"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "UPDATE execution_intents SET limit_price = 101"));
        var reviewService = new PaperReviewService(
            database.PaperReviewIntentPersistence,
            database.SessionPersistence,
            database.GoalPersistence,
            database.ActivationPersistence,
            database.AccountAllocationPersistence,
            database.ExecutablePreflightPersistence,
            riskProbe,
            database.ExecutionSafetyPersistence,
            CreateReconciliationRunner(database, context),
            new FixedPaperRiskPolicyProvider(),
            context.Clock,
            TimeSpan.FromMinutes(2));
        var review = await reviewService.ReviewAsync(
            intentOutcome.Intent.Id,
            intentOutcome.Intent.ContentDigest,
            ReviewResult.APPROVED,
            context.Actor,
            "Approve the exact bounded paper intent.");

        await database.ReopenApplicationAsync();
        var restoredIntent = await database.PaperReviewIntentPersistence.FindIntentAsync(
            intentOutcome.Intent.Id,
            default);
        var restoredRisk = await database.PaperReviewIntentPersistence.FindRiskEvaluationAsync(
            intentOutcome.RiskEvaluation.Id,
            default);
        var restoredReview = await database.PaperReviewIntentPersistence.FindReviewAsync(
            intentOutcome.Intent.Id,
            default);
        var restoredReservation = await database.ExecutionSafetyPersistence.FindReservationAsync(
            intentOutcome.Intent.Id,
            default);
        Assert.NotNull(restoredIntent);
        Assert.NotNull(restoredRisk);
        Assert.NotNull(restoredReview);
        Assert.NotNull(restoredReservation);
        Assert.Equal(ExecutionIntentState.APPROVED, restoredIntent.State);
        Assert.Equal(intentOutcome.Intent.ContentDigest, restoredIntent.ContentDigest);
        Assert.Equal(intentOutcome.RiskEvaluation.ContentDigest, restoredRisk.ContentDigest);
        Assert.Equal(review.ContentDigest, restoredReview.ContentDigest);
        Assert.True(restoredIntent.HasValidContentDigest());
        Assert.True(restoredRisk.HasValidContentDigest());
        Assert.True(restoredReview.HasValidContentDigest());
        Assert.True(restoredReservation.HasValidContentDigest());
        var reviewReconciliationId = await database.PaperReviewIntentPersistence
            .FindReviewReconciliationAsync(intentOutcome.Intent.Id, default);
        Assert.NotNull(reviewReconciliationId);
        var reviewReconciliation = await database.ExecutionSafetyPersistence
            .FindReconciliationAsync(reviewReconciliationId.Value, default);
        Assert.NotNull(reviewReconciliation);
        Assert.StartsWith("orders:sha256:", reviewReconciliation.ComparedOrderRange);
        Assert.StartsWith("fills:sha256:", reviewReconciliation.ComparedFillRange);
        Assert.StartsWith(
            "positions:sha256:", reviewReconciliation.ComparedPositionSnapshot);
        Assert.Null(reviewReconciliation.OperatorAction);
        Assert.True(reviewReconciliation.HasValidContentDigest());
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "UPDATE review_approvals SET intent_digest = repeat('0', 64)"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "DELETE FROM risk_evaluations"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "UPDATE reconciliations SET position_exposure = position_exposure + 1"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "DELETE FROM exposure_reservations"));
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Prepared_order_survives_restart_and_unknown_result_recovers_without_resubmission()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var (intent, riskProbe) = await CreateApprovedIntentAsync(database, context);
        var fake = new DeterministicPaperBrokerOrderAdapter(context.Clock);
        fake.Enqueue(PaperBrokerSendBehavior.TIMEOUT_AFTER_ACCEPTANCE);
        var service = CreateOrderService(database, context, riskProbe, fake);

        var preparation = await service.PrepareAsync(intent.Id, context.Actor);
        Assert.NotNull(preparation.Order);
        Assert.Equal(OrderState.SUBMITTING, preparation.Order.State);
        await database.ReopenApplicationAsync();
        var restored = await database.PaperOrderPersistence.FindOrderAsync(preparation.Order.Id, default);
        Assert.NotNull(restored);
        Assert.Equal(preparation.Order.BrokerClientOrderId, restored.BrokerClientOrderId);
        var restoredEvaluation = await database.PaperOrderPersistence.FindEvaluationAsync(
            restored.PreSubmitEvaluationId, default);
        Assert.NotNull(restoredEvaluation);
        Assert.True(restoredEvaluation.HasValidContentDigest());
        Assert.Equal(
            preparation.Evaluation.ReconciliationId,
            restoredEvaluation.ReconciliationId);

        service = CreateOrderService(database, context, riskProbe, fake);
        var unknown = await service.SendPreparedAsync(restored.Id, context.Actor);
        Assert.Equal(OrderState.SUBMISSION_UNKNOWN, unknown.State);
        await database.ReopenApplicationAsync();
        service = CreateOrderService(database, context, riskProbe, fake);
        var recovered = Assert.Single(await service.RecoverAsync(context.Actor));

        Assert.Equal(OrderState.ACKNOWLEDGED, recovered.State);
        Assert.Equal(1, fake.SendCallCount);
        Assert.Equal(1, fake.ExternalOrderCount);
        var restoredIntent = await database.PaperReviewIntentPersistence.FindIntentAsync(intent.Id, default);
        Assert.NotNull(restoredIntent);
        Assert.Equal(ExecutionIntentState.SUBMITTED, restoredIntent.State);
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            database.DataSource,
            "UPDATE orders SET broker_client_order_id = 'different-client-id'"));
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Revocation_keeps_account_allocation_while_order_exposure_is_reserved()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var (intent, riskProbe) = await CreateApprovedIntentAsync(database, context);
        var fake = new DeterministicPaperBrokerOrderAdapter(context.Clock);
        var orderService = CreateOrderService(database, context, riskProbe, fake);
        var preparation = await orderService.PrepareAsync(intent.Id, context.Actor);
        var acknowledged = await orderService.SendPreparedAsync(
            preparation.Order!.Id, context.Actor);

        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var revoked = await new GoalActivationService(
            database.GoalPersistence,
            database.ActivationPersistence,
            context.Clock).RevokeAsync(
                context.Activation.Id,
                context.Actor,
                "The Operator revoked paper authority.");

        Assert.Equal(GoalActivationState.REVOKED, revoked.State);
        Assert.Equal(OrderState.ACKNOWLEDGED, acknowledged.State);
        var allocation = await database.AccountAllocationPersistence
            .FindByActivationAsync(context.Activation.Id, default);
        Assert.NotNull(allocation);
        Assert.Equal(AccountAllocationState.ACTIVE, allocation.State);
        Assert.Equal(1_000m, await ExecuteScalarDecimalAsync(
            database.DataSource,
            "SELECT remaining_amount FROM exposure_reservations"));
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Duplicate_and_late_fill_events_survive_restart_without_exposure_regression()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var (intent, riskProbe) = await CreateApprovedIntentAsync(database, context);
        var fake = new DeterministicPaperBrokerOrderAdapter(context.Clock);
        var orderService = CreateOrderService(database, context, riskProbe, fake);
        var preparation = await orderService.PrepareAsync(intent.Id, context.Actor);
        var acknowledged = await orderService.SendPreparedAsync(preparation.Order!.Id, context.Actor);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var eventService = new PaperBrokerEventService(database.PaperOrderPersistence,
            database.PaperBrokerEventPersistence, context.Clock);
        var partialUpdate = new PaperBrokerExecutionUpdate(acknowledged.Id,
            acknowledged.AccountBinding, acknowledged.BrokerClientOrderId,
            acknowledged.BrokerOrderId!, "stream-event-partial", BrokerExecutionEventKind.PARTIALLY_FILLED,
            4m, context.Clock.GetUtcNow(), "payload/stream-event-partial",
            new("broker-fill-1", 4m, 99m, null, null, context.Clock.GetUtcNow()));

        var partial = await eventService.IngestAsync(partialUpdate, context.Actor);
        var duplicate = await eventService.IngestAsync(partialUpdate, context.Actor);
        Assert.Equal(PaperBrokerEventDisposition.APPLIED, partial.Disposition);
        Assert.Equal(PaperBrokerEventDisposition.DUPLICATE, duplicate.Disposition);
        var conflictingReference = partialUpdate with { SourcePayloadReference = "payload/conflict" };
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await eventService.IngestAsync(conflictingReference, context.Actor));
        Assert.Equal(600m, await ExecuteScalarDecimalAsync(database.DataSource,
            "SELECT remaining_amount FROM exposure_reservations"));
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var filledUpdate = new PaperBrokerExecutionUpdate(acknowledged.Id,
            acknowledged.AccountBinding, acknowledged.BrokerClientOrderId,
            acknowledged.BrokerOrderId!, "stream-event-filled", BrokerExecutionEventKind.FILLED,
            10m, context.Clock.GetUtcNow(), "payload/stream-event-filled",
            new("broker-fill-2", 6m, 99.50m, 0.10m, "USD", context.Clock.GetUtcNow()));
        _ = await eventService.IngestAsync(filledUpdate, context.Actor);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var lateUpdate = partialUpdate with
        {
            EventReference = "stream-event-late-partial",
            BrokerEventTime = context.Clock.GetUtcNow().AddSeconds(-2),
            SourcePayloadReference = "payload/stream-event-late-partial"
        };
        var late = await eventService.IngestAsync(lateUpdate, context.Actor);
        Assert.Equal(PaperBrokerEventDisposition.RECORDED_NO_CHANGE, late.Disposition);

        await database.ReopenApplicationAsync();
        var restored = await database.PaperOrderPersistence.FindOrderAsync(acknowledged.Id, default);
        Assert.NotNull(restored);
        Assert.Equal(OrderState.FILLED, restored.State);
        Assert.Equal(10m, restored.FilledQuantity);
        Assert.Equal(2, await ExecuteScalarInt64Async(database.DataSource, "SELECT count(*) FROM fills"));
        Assert.Equal(3, await ExecuteScalarInt64Async(database.DataSource, "SELECT count(*) FROM broker_execution_events"));
        Assert.Equal(0m, await ExecuteScalarDecimalAsync(database.DataSource,
            "SELECT remaining_amount FROM exposure_reservations"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database.DataSource,
            "UPDATE fills SET price = price + 1"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database.DataSource,
            "DELETE FROM broker_execution_events"));
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Post_fill_position_evaluation_is_idempotent_append_only_and_restart_safe()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var (intent, riskProbe) = await CreateApprovedIntentAsync(database, context);
        var fake = new DeterministicPaperBrokerOrderAdapter(context.Clock);
        var orderService = CreateOrderService(database, context, riskProbe, fake);
        var preparation = await orderService.PrepareAsync(intent.Id, context.Actor);
        var acknowledged = await orderService.SendPreparedAsync(preparation.Order!.Id, context.Actor);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var eventService = new PaperBrokerEventService(database.PaperOrderPersistence,
            database.PaperBrokerEventPersistence, context.Clock);
        var brokerTime = context.Clock.GetUtcNow();
        var partial = await eventService.IngestAsync(new PaperBrokerExecutionUpdate(
            acknowledged.Id, acknowledged.AccountBinding, acknowledged.BrokerClientOrderId,
            acknowledged.BrokerOrderId!, "post-fill-event-1",
            BrokerExecutionEventKind.PARTIALLY_FILLED, 4m, brokerTime,
            "payload/post-fill-event-1",
            new("post-fill-broker-fill-1", 4m, 99m, null, null, brokerTime)), context.Actor);
        Assert.NotNull(partial.Fill);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var observation = new PaperPositionObservation(partial.Fill.Id,
            acknowledged.AccountBinding, "position-observation-1", context.Clock.GetUtcNow(),
            "payload/position-observation-1", [new("AAPL", 4m, 99m, 396m, "USD")]);
        var safetyService = new PaperPostFillSafetyService(
            database.PaperPostFillSafetyPersistence, context.Clock);

        var wrongBinding = new IntentAccountBinding(
            acknowledged.AccountBinding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER,
            acknowledged.AccountBinding.EndpointIdentity,
            "wrong-paper-account");
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await safetyService.EvaluateAsync(observation with
            {
                ObservedBinding = wrongBinding,
                ObservationReference = "position-observation-wrong-account",
                SourcePayloadReference = "payload/position-observation-wrong-account"
            }, context.Actor));
        Assert.Equal(0L, await ExecuteScalarInt64Async(database.DataSource,
            "SELECT count(*) FROM position_snapshot_batches"));

        var outcome = await safetyService.EvaluateAsync(observation, context.Actor);
        Assert.Equal(PostFillEvaluationResult.PASSED, outcome.Evaluation.Result);
        Assert.Equal(996m, outcome.Evaluation.TotalExposure);
        Assert.Null(outcome.SafetyResponse);
        await database.ReopenApplicationAsync();
        safetyService = new PaperPostFillSafetyService(
            database.PaperPostFillSafetyPersistence, context.Clock);
        var duplicate = await safetyService.EvaluateAsync(observation, context.Actor);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(outcome.Evaluation.Id, duplicate.Evaluation.Id);
        Assert.Equal(1L, await ExecuteScalarInt64Async(database.DataSource,
            "SELECT count(*) FROM position_snapshot_batches"));
        Assert.Equal(1L, await ExecuteScalarInt64Async(database.DataSource,
            "SELECT count(*) FROM post_fill_evaluations"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database.DataSource,
            "UPDATE position_snapshots SET market_value = market_value + 1"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database.DataSource,
            "DELETE FROM post_fill_evaluations"));
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Post_fill_allocation_violation_locks_session_and_triggers_safety_scopes()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var (intent, riskProbe) = await CreateApprovedIntentAsync(database, context);
        var fake = new DeterministicPaperBrokerOrderAdapter(context.Clock);
        var orderService = CreateOrderService(database, context, riskProbe, fake);
        var preparation = await orderService.PrepareAsync(intent.Id, context.Actor);
        var acknowledged = await orderService.SendPreparedAsync(preparation.Order!.Id, context.Actor);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var brokerTime = context.Clock.GetUtcNow();
        var eventService = new PaperBrokerEventService(database.PaperOrderPersistence,
            database.PaperBrokerEventPersistence, context.Clock);
        var partial = await eventService.IngestAsync(new PaperBrokerExecutionUpdate(
            acknowledged.Id, acknowledged.AccountBinding, acknowledged.BrokerClientOrderId,
            acknowledged.BrokerOrderId!, "post-fill-event-violation",
            BrokerExecutionEventKind.PARTIALLY_FILLED, 4m, brokerTime,
            "payload/post-fill-event-violation",
            new("post-fill-broker-fill-violation", 4m, 99m, null, null, brokerTime)), context.Actor);
        Assert.NotNull(partial.Fill);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var cancellationService = new PaperCancellationService(
            database.PaperOrderPersistence, database.PaperCancellationPersistence,
            fake, database.ExecutionSafetyPersistence, context.Clock);
        var safetyService = new PaperPostFillSafetyService(
            database.PaperPostFillSafetyPersistence, context.Clock,
            database.ExecutionSafetyPersistence, cancellationService);

        var outcome = await safetyService.EvaluateAsync(new PaperPositionObservation(
            partial.Fill.Id, acknowledged.AccountBinding, "position-observation-violation",
            context.Clock.GetUtcNow(), "payload/position-observation-violation",
            [new("AAPL", 4m, 99m, 25_000m, "USD")]), context.Actor);

        Assert.Equal(PostFillEvaluationResult.VIOLATION_LOCKED, outcome.Evaluation.Result);
        Assert.NotNull(outcome.SafetyResponse);
        Assert.Equal(PostFillReconciliationState.REQUESTED,
            outcome.SafetyResponse.ReconciliationState);
        Assert.Equal(OperatorNotificationState.PENDING,
            outcome.SafetyResponse.NotificationState);
        await database.ReopenApplicationAsync();
        var session = await database.SessionPersistence.FindAsync(outcome.Evaluation.SessionId, default);
        Assert.NotNull(session);
        Assert.Equal(SessionState.LOCKED, session.State);
        Assert.Equal(2L, await ExecuteScalarInt64Async(database.DataSource,
            "SELECT count(*) FROM kill_switches WHERE state = 'TRIGGERED' AND scope_type IN ('ACCOUNT', 'GOAL')"));
        Assert.Equal(1, fake.CancellationCallCount);
        Assert.Equal(1L, await ExecuteScalarInt64Async(database.DataSource,
            "SELECT count(*) FROM cancellation_requests WHERE state = 'REQUESTED'"));
        Assert.Equal(1L, await ExecuteScalarInt64Async(database.DataSource,
            "SELECT count(*) FROM post_fill_safety_responses WHERE reconciliation_state = 'REQUESTED' AND notification_state = 'PENDING'"));
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Detailed_reconciliation_compares_durable_order_fill_position_and_reservation()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var (intent, riskProbe) = await CreateApprovedIntentAsync(database, context);
        var fake = new DeterministicPaperBrokerOrderAdapter(context.Clock);
        var orderService = CreateOrderService(database, context, riskProbe, fake);
        var preparation = await orderService.PrepareAsync(intent.Id, context.Actor);
        var acknowledged = await orderService.SendPreparedAsync(
            preparation.Order!.Id, context.Actor);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var brokerTime = context.Clock.GetUtcNow();
        var ingestion = await new PaperBrokerEventService(database.PaperOrderPersistence,
            database.PaperBrokerEventPersistence, context.Clock).IngestAsync(
            new PaperBrokerExecutionUpdate(acknowledged.Id, acknowledged.AccountBinding,
                acknowledged.BrokerClientOrderId, acknowledged.BrokerOrderId!,
                "reconcile-partial-event", BrokerExecutionEventKind.PARTIALLY_FILLED,
                4m, brokerTime, "payload/reconcile-partial-event",
                new("reconcile-fill-1", 4m, 99m, null, null, brokerTime)),
            context.Actor);
        var observedOrder = new PaperReconciliationOrder(null,
            ingestion.Order.BrokerClientOrderId, ingestion.Order.BrokerOrderId,
            ingestion.Order.Request.InstrumentIdentifier, ingestion.Order.Request.Side,
            ingestion.Order.Request.Quantity, ingestion.Order.Request.LimitPrice,
            ingestion.Order.FilledQuantity, ingestion.Order.State);
        var snapshot = new PaperBrokerReconciliationSnapshot(
            ingestion.Order.AccountBinding, context.Clock.GetUtcNow(), 25_000m, "USD",
            [observedOrder],
            [new("reconcile-fill-1", ingestion.Order.BrokerOrderId!, "AAPL", 4m,
                99m, brokerTime)],
            [new("AAPL", 4m, 396m, "USD")]);
        var service = new PaperReconciliationService(database.GoalPersistence,
            new PostgresPaperReconciliationPersistence(database.DataSource),
            database.ExecutionSafetyPersistence, new SnapshotAdapter(snapshot),
            context.Clock);

        var result = await service.ReconcileAsync(context.Goal.Id, context.Actor);

        Assert.Equal(ReconciliationResult.CURRENT, result.Result);
        Assert.Empty(result.Differences);
        Assert.Equal(396m, result.PositionExposure);
        Assert.Equal(600m, result.OpenOrderExposure);
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Reconciliation_applies_authoritative_cancellation_after_unknown_request_outcome()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var (intent, riskProbe) = await CreateApprovedIntentAsync(database, context);
        var fake = new DeterministicPaperBrokerOrderAdapter(context.Clock);
        var orderService = CreateOrderService(database, context, riskProbe, fake);
        var preparation = await orderService.PrepareAsync(intent.Id, context.Actor);
        var acknowledged = await orderService.SendPreparedAsync(
            preparation.Order!.Id, context.Actor);
        fake.EnqueueCancellation(PaperBrokerCancellationBehavior.TIMEOUT_AFTER_ACCEPTANCE);
        var cancellationService = new PaperCancellationService(
            database.PaperOrderPersistence, database.PaperCancellationPersistence, fake,
            database.ExecutionSafetyPersistence, context.Clock);

        var unknown = await cancellationService.CancelAsync(acknowledged.Id, context.Actor,
            "The Operator requested cancellation.");

        Assert.Equal(CancellationRequestState.RECONCILIATION_REQUIRED, unknown.State);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var brokerTime = context.Clock.GetUtcNow();
        fake.SetOrderState(acknowledged.BrokerClientOrderId, OrderState.CANCELED, 0m);
        fake.AddExecutionUpdate(new PaperBrokerExecutionUpdate(
            acknowledged.Id, acknowledged.AccountBinding,
            acknowledged.BrokerClientOrderId, acknowledged.BrokerOrderId!,
            "reconciled-cancellation", BrokerExecutionEventKind.CANCELED, 0m,
            brokerTime, "payload/reconciled-cancellation", null));
        var eventService = new PaperBrokerEventService(database.PaperOrderPersistence,
            database.PaperBrokerEventPersistence, context.Clock);
        var reconciliationService = new PaperReconciliationService(
            database.GoalPersistence,
            new PostgresPaperReconciliationPersistence(database.DataSource),
            database.ExecutionSafetyPersistence, fake, context.Clock, eventService);

        var result = await reconciliationService.ReconcileAsync(
            context.Goal.Id, context.Actor);

        Assert.Equal(ReconciliationResult.CURRENT, result.Result);
        Assert.Equal(["Applied Broker execution event reconciled-cancellation."],
            result.Corrections);
        var order = await database.PaperOrderPersistence.FindOrderAsync(
            acknowledged.Id, default);
        Assert.NotNull(order);
        Assert.Equal(OrderState.CANCELED, order.State);
        var resolved = await database.PaperCancellationPersistence.FindByOrderAsync(
            acknowledged.Id, default);
        Assert.NotNull(resolved);
        Assert.Equal(CancellationRequestState.CONFIRMED, resolved.State);
        Assert.Equal(0m, await ExecuteScalarDecimalAsync(database.DataSource,
            "SELECT remaining_amount FROM exposure_reservations"));
        var canceledIntent = await database.PaperReviewIntentPersistence.FindIntentAsync(
            intent.Id, default);
        Assert.NotNull(canceledIntent);
        Assert.Equal(ExecutionIntentState.CANCELED, canceledIntent.State);
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Cancellation_confirmation_releases_only_remaining_exposure_and_survives_restart()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var (intent, riskProbe) = await CreateApprovedIntentAsync(database, context);
        var fake = new DeterministicPaperBrokerOrderAdapter(context.Clock);
        var orderService = CreateOrderService(database, context, riskProbe, fake);
        var preparation = await orderService.PrepareAsync(intent.Id, context.Actor);
        var acknowledged = await orderService.SendPreparedAsync(preparation.Order!.Id, context.Actor);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var eventService = new PaperBrokerEventService(database.PaperOrderPersistence,
            database.PaperBrokerEventPersistence, context.Clock);
        var partialTime = context.Clock.GetUtcNow();
        _ = await eventService.IngestAsync(new PaperBrokerExecutionUpdate(
            acknowledged.Id, acknowledged.AccountBinding, acknowledged.BrokerClientOrderId,
            acknowledged.BrokerOrderId!, "cancel-partial-fill",
            BrokerExecutionEventKind.PARTIALLY_FILLED, 4m, partialTime,
            "payload/cancel-partial-fill",
            new("cancel-fill-1", 4m, 99m, null, null, partialTime)), context.Actor);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var cancellationService = new PaperCancellationService(database.PaperOrderPersistence,
            database.PaperCancellationPersistence, fake,
            database.ExecutionSafetyPersistence, context.Clock);

        var requested = await cancellationService.CancelAsync(acknowledged.Id, context.Actor,
            "The Operator requested cancellation.");

        Assert.Equal(CancellationRequestState.REQUESTED, requested.State);
        Assert.Equal(600m, await ExecuteScalarDecimalAsync(database.DataSource,
            "SELECT remaining_amount FROM exposure_reservations"));
        var pending = await database.PaperOrderPersistence.FindOrderAsync(acknowledged.Id, default);
        Assert.NotNull(pending);
        Assert.Equal(OrderState.CANCEL_PENDING, pending.State);
        await database.ReopenApplicationAsync();
        cancellationService = new PaperCancellationService(database.PaperOrderPersistence,
            database.PaperCancellationPersistence, fake,
            database.ExecutionSafetyPersistence, context.Clock);
        var duplicate = await cancellationService.CancelAsync(acknowledged.Id, context.Actor,
            "The Operator repeated cancellation.");
        Assert.Equal(requested.Id, duplicate.Id);
        Assert.Equal(1, fake.CancellationCallCount);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        eventService = new PaperBrokerEventService(database.PaperOrderPersistence,
            database.PaperBrokerEventPersistence, context.Clock);
        var canceled = await eventService.IngestAsync(new PaperBrokerExecutionUpdate(
            acknowledged.Id, acknowledged.AccountBinding, acknowledged.BrokerClientOrderId,
            acknowledged.BrokerOrderId!, "cancel-confirmed",
            BrokerExecutionEventKind.CANCELED, 4m, context.Clock.GetUtcNow(),
            "payload/cancel-confirmed", null), context.Actor);

        Assert.Equal(OrderState.CANCELED, canceled.Order.State);
        var resolved = await database.PaperCancellationPersistence.FindByOrderAsync(
            acknowledged.Id, default);
        Assert.NotNull(resolved);
        Assert.Equal(CancellationRequestState.CONFIRMED, resolved.State);
        Assert.Equal(0m, await ExecuteScalarDecimalAsync(database.DataSource,
            "SELECT remaining_amount FROM exposure_reservations"));
        var canceledIntent = await database.PaperReviewIntentPersistence.FindIntentAsync(intent.Id, default);
        Assert.NotNull(canceledIntent);
        Assert.Equal(ExecutionIntentState.CANCELED, canceledIntent.State);
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database.DataSource,
            "UPDATE cancellation_requests SET account_identifier = 'wrong-paper-account'"));
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Kill_switch_requests_cancellation_and_fill_race_supersedes_it()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var (intent, riskProbe) = await CreateApprovedIntentAsync(database, context);
        var fake = new DeterministicPaperBrokerOrderAdapter(context.Clock);
        var orderService = CreateOrderService(database, context, riskProbe, fake);
        var preparation = await orderService.PrepareAsync(intent.Id, context.Actor);
        var acknowledged = await orderService.SendPreparedAsync(preparation.Order!.Id, context.Actor);
        var cancellationService = new PaperCancellationService(database.PaperOrderPersistence,
            database.PaperCancellationPersistence, fake,
            database.ExecutionSafetyPersistence, context.Clock);
        var safetyService = new ExecutionSafetyService(database.GoalPersistence,
            database.ExecutionSafetyPersistence, context.Clock, cancellationService);
        var scopes = await database.ExecutionSafetyPersistence.FindKillSwitchesAsync(
            PaperExecutionSafetyScopes.Required(context.Goal.Id, acknowledged.AccountBinding), default);
        var accountSwitch = Assert.Single(scopes,
            item => item.ScopeType == KillSwitchScopeType.ACCOUNT);
        context.Clock.Advance(TimeSpan.FromSeconds(1));

        var triggered = await safetyService.TriggerAsync(accountSwitch.Id, context.Actor,
            "The Operator stopped account execution.");

        Assert.Equal(KillSwitchState.TRIGGERED, triggered.State);
        Assert.Equal(1, fake.ExternalCancellationCount);
        var request = await database.PaperCancellationPersistence.FindByOrderAsync(
            acknowledged.Id, default);
        Assert.NotNull(request);
        Assert.Equal(CancellationRequestState.REQUESTED, request.State);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var eventService = new PaperBrokerEventService(database.PaperOrderPersistence,
            database.PaperBrokerEventPersistence, context.Clock);
        var fillTime = context.Clock.GetUtcNow();
        var filled = await eventService.IngestAsync(new PaperBrokerExecutionUpdate(
            acknowledged.Id, acknowledged.AccountBinding, acknowledged.BrokerClientOrderId,
            acknowledged.BrokerOrderId!, "filled-during-cancel",
            BrokerExecutionEventKind.FILLED, 10m, fillTime,
            "payload/filled-during-cancel",
            new("cancel-race-fill", 10m, 100m, null, null, fillTime)), context.Actor);

        Assert.Equal(OrderState.FILLED, filled.Order.State);
        var superseded = await database.PaperCancellationPersistence.FindByOrderAsync(
            acknowledged.Id, default);
        Assert.NotNull(superseded);
        Assert.Equal(CancellationRequestState.SUPERSEDED, superseded.State);
        Assert.Equal(0m, await ExecuteScalarDecimalAsync(database.DataSource,
            "SELECT remaining_amount FROM exposure_reservations"));
        var submittedIntent = await database.PaperReviewIntentPersistence.FindIntentAsync(intent.Id, default);
        Assert.NotNull(submittedIntent);
        Assert.Equal(ExecutionIntentState.SUBMITTED, submittedIntent.State);
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Restart_after_cancellation_preparation_reuses_one_external_request()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var (intent, riskProbe) = await CreateApprovedIntentAsync(database, context);
        var fake = new DeterministicPaperBrokerOrderAdapter(context.Clock);
        var orderService = CreateOrderService(database, context, riskProbe, fake);
        var preparation = await orderService.PrepareAsync(intent.Id, context.Actor);
        var acknowledged = await orderService.SendPreparedAsync(preparation.Order!.Id, context.Actor);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var request = CancellationRequest.Prepare(CancellationRequestId.New(), acknowledged,
            "The Operator requested cancellation.", context.Clock.GetUtcNow());
        var pending = acknowledged.RequestCancellation(context.Clock.GetUtcNow());

        var durable = await database.PaperCancellationPersistence.PrepareAsync(request,
            pending, acknowledged.State, context.Actor, default);

        Assert.False(durable.Duplicate);
        Assert.Equal(CancellationRequestState.PREPARED, durable.Request.State);
        Assert.Equal(1_000m, await ExecuteScalarDecimalAsync(database.DataSource,
            "SELECT remaining_amount FROM exposure_reservations"));
        await database.ReopenApplicationAsync();
        var cancellationService = new PaperCancellationService(database.PaperOrderPersistence,
            database.PaperCancellationPersistence, fake,
            database.ExecutionSafetyPersistence, context.Clock);
        var recovered = Assert.Single(await cancellationService.RecoverAsync(context.Actor));

        Assert.Equal(request.Id, recovered.Id);
        Assert.Equal(CancellationRequestState.REQUESTED, recovered.State);
        Assert.Equal(1, fake.CancellationCallCount);
        Assert.Equal(1, fake.ExternalCancellationCount);
        Assert.Empty(await cancellationService.RecoverAsync(context.Actor));
        Assert.Equal(1, fake.CancellationCallCount);
        Assert.Equal(1_000m, await ExecuteScalarDecimalAsync(database.DataSource,
            "SELECT remaining_amount FROM exposure_reservations"));
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Concurrent_approvals_cannot_overbook_shared_account_allocation()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var sessionOutcome = await context.Service.StartAsync(
            context.Goal.Id,
            context.Activation.Id,
            context.Snapshot.Id,
            context.Actor);
        var running = sessionOutcome.Session.TransitionTo(SessionState.RUNNING, context.Clock.GetUtcNow());
        await database.SessionPersistence.UpdateAsync(
            running,
            sessionOutcome.Session.State,
            sessionOutcome.Session.Revision,
            context.Actor,
            context.Clock.GetUtcNow(),
            default);
        var decisions = Enumerable.Range(0, 3).Select(_ => Decision.Create(
            DecisionId.New(),
            running.Id,
            context.Goal.Id,
            context.Goal.StrategyVersion,
            context.Snapshot.Id,
            context.Clock.GetUtcNow(),
            DecisionAction.BUY,
            "AAPL",
            45m,
            100m,
            "Buy inside exact paper authority.",
            0.8m,
            [new DecisionGuardResult("EXECUTABLE_OUTPUT", true, "Executable output is valid.")])).ToArray();
        foreach (var decision in decisions)
        {
            await database.DecisionPersistence.InsertAsync(decision, context.Actor, default);
        }

        var riskProbe = CreateRiskProbe(context);
        var intentService = new PaperReviewIntentService(
            database.GoalPersistence,
            database.ActivationPersistence,
            database.AccountAllocationPersistence,
            database.SessionPersistence,
            database.ExecutablePreflightPersistence,
            database.DecisionPersistence,
            riskProbe,
            database.ExecutionSafetyPersistence,
            CreateReconciliationRunner(database, context),
            database.PaperReviewIntentPersistence,
            new FixedPaperRiskPolicyProvider(),
            context.Clock);
        var intents = new List<ExecutionIntent>();
        foreach (var decision in decisions)
        {
            var outcome = await intentService.CreateAsync(
                decision.Id,
                45m,
                100m,
                context.Clock.GetUtcNow().AddMinutes(5),
                context.Actor);
            Assert.Equal(RiskEvaluationResult.ACCEPTED, outcome.RiskEvaluation.Result);
            intents.Add(outcome.Intent);
        }

        var reviewService = new PaperReviewService(
            database.PaperReviewIntentPersistence,
            database.SessionPersistence,
            database.GoalPersistence,
            database.ActivationPersistence,
            database.AccountAllocationPersistence,
            database.ExecutablePreflightPersistence,
            riskProbe,
            database.ExecutionSafetyPersistence,
            CreateReconciliationRunner(database, context),
            new FixedPaperRiskPolicyProvider(),
            context.Clock,
            TimeSpan.FromMinutes(2));
        var attempts = intents.Select(intent => CaptureAsync(async () => await reviewService.ReviewAsync(
            intent.Id,
            intent.ContentDigest,
            ReviewResult.APPROVED,
            context.Actor))).ToArray();
        var errors = await Task.WhenAll(attempts);

        Assert.Equal(2, errors.Count(error => error is null));
        Assert.Single(errors, error => error is not null);
        Assert.Equal(2L, await ExecuteScalarInt64Async(
            database.DataSource,
            "SELECT count(*) FROM exposure_reservations WHERE state = 'RESERVED'"));
        Assert.Equal(2L, await ExecuteScalarInt64Async(
            database.DataSource,
            "SELECT count(*) FROM execution_intents WHERE state = 'APPROVED'"));
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    [Fact]
    public async Task Paper_auto_executes_fills_reconciles_and_completes_without_review()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database, mode: ExecutionMode.PAPER_AUTO);
        var sessionOutcome = await context.Service.StartAsync(
            context.Goal.Id, context.Activation.Id, context.Snapshot.Id, context.Actor);
        var running = sessionOutcome.Session.TransitionTo(
            SessionState.RUNNING, context.Clock.GetUtcNow());
        await database.SessionPersistence.UpdateAsync(running, sessionOutcome.Session.State,
            sessionOutcome.Session.Revision, context.Actor, context.Clock.GetUtcNow(), default);
        var decision = Decision.Create(DecisionId.New(), running.Id, context.Goal.Id,
            context.Goal.StrategyVersion, context.Snapshot.Id, context.Clock.GetUtcNow(),
            DecisionAction.BUY, "AAPL", 10m, 100m,
            "Buy inside exact automatic paper authority.", 0.8m,
            [new DecisionGuardResult("EXECUTABLE_OUTPUT", true,
                "Executable output is valid.")]);
        await database.DecisionPersistence.InsertAsync(decision, context.Actor, default);
        var riskProbe = CreateRiskProbe(context);
        var intentService = new PaperReviewIntentService(database.GoalPersistence,
            database.ActivationPersistence, database.AccountAllocationPersistence,
            database.SessionPersistence, database.ExecutablePreflightPersistence,
            database.DecisionPersistence, riskProbe, database.ExecutionSafetyPersistence,
            CreateReconciliationRunner(database, context),
            database.PaperReviewIntentPersistence, new FixedPaperRiskPolicyProvider(),
            context.Clock);

        var intentOutcome = await intentService.CreateAsync(decision.Id, 10m, 100m,
            context.Clock.GetUtcNow().AddMinutes(5), context.Actor);

        Assert.Equal(ExecutionIntentState.RISK_ACCEPTED, intentOutcome.Intent.State);
        Assert.Null(await database.PaperReviewIntentPersistence.FindReviewAsync(
            intentOutcome.Intent.Id, default));
        Assert.NotNull(await database.ExecutionSafetyPersistence.FindReservationAsync(
            intentOutcome.Intent.Id, default));
        var fake = new DeterministicPaperBrokerOrderAdapter(context.Clock);
        var orderService = CreateOrderService(database, context, riskProbe, fake);
        var preparation = await orderService.PrepareAsync(intentOutcome.Intent.Id, context.Actor);
        var acknowledged = await orderService.SendPreparedAsync(
            preparation.Order!.Id, context.Actor);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var brokerTime = context.Clock.GetUtcNow();
        var executionUpdate = new PaperBrokerExecutionUpdate(acknowledged.Id,
            acknowledged.AccountBinding, acknowledged.BrokerClientOrderId,
            acknowledged.BrokerOrderId!, "paper-auto-fill-event",
            BrokerExecutionEventKind.FILLED, 10m, brokerTime,
            "payload/paper-auto-fill-event",
            new("paper-auto-fill", 10m, 99m, null, null, brokerTime));
        var ingestion = await new PaperBrokerEventService(database.PaperOrderPersistence,
            database.PaperBrokerEventPersistence, context.Clock).IngestAsync(
            executionUpdate, context.Actor);
        Assert.NotNull(ingestion.Fill);
        var postFill = await new PaperPostFillSafetyService(
            database.PaperPostFillSafetyPersistence, context.Clock).EvaluateAsync(
                new PaperPositionObservation(ingestion.Fill.Id,
                    acknowledged.AccountBinding, "paper-auto-position",
                    context.Clock.GetUtcNow(), "payload/paper-auto-position",
                    [new("AAPL", 10m, 99m, 990m, "USD")]),
                context.Actor);
        Assert.Equal(PostFillEvaluationResult.PASSED, postFill.Evaluation.Result);
        var snapshot = new PaperBrokerReconciliationSnapshot(
            acknowledged.AccountBinding, context.Clock.GetUtcNow(), 24_010m, "USD",
            [new PaperReconciliationOrder(intentOutcome.Intent.Id,
                ingestion.Order.BrokerClientOrderId, ingestion.Order.BrokerOrderId,
                "AAPL", OrderSide.BUY, 10m, 100m, 10m, OrderState.FILLED)],
            [new PaperReconciliationFill("paper-auto-fill", acknowledged.BrokerOrderId!,
                "AAPL", 10m, 99m, brokerTime)],
            [new PaperReconciliationPosition("AAPL", 10m, 990m, "USD")]);
        var reconciliation = new PaperReconciliationService(database.GoalPersistence,
            new PostgresPaperReconciliationPersistence(database.DataSource),
            database.ExecutionSafetyPersistence, new SnapshotAdapter(snapshot), context.Clock);
        var lifecycle = new PaperExecutableSessionLifecycleService(
            database.SessionPersistence, database.GoalPersistence,
            database.ActivationPersistence, database.ExecutionSafetyPersistence,
            reconciliation, context.Clock);

        var completed = await lifecycle.CompleteAsync(running.Id, context.Actor);

        Assert.Equal(SessionState.COMPLETED, completed.Session.State);
        Assert.Equal(ReconciliationResult.CURRENT, completed.Reconciliation.Result);
        Assert.Equal(0m, await ExecuteScalarDecimalAsync(database.DataSource,
            "SELECT remaining_amount FROM exposure_reservations"));
        Assert.Equal(0L, await ExecuteScalarInt64Async(database.DataSource,
            "SELECT count(*) FROM review_approvals"));
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    private static async ValueTask<TestContext> CreateContextAsync(
        PostgresTestDatabase database,
        bool wrongAccount = false,
        bool configureSafety = true,
        ExecutionMode mode = ExecutionMode.PAPER_REVIEW)
    {
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var goalService = new GoalService(database.GoalPersistence, clock);
        var draft = await goalService.CreateDraftAsync(
            GoalId.New(),
            1,
            GoalTestData.CreateDefinition(mode),
            OperatorId.New());
        clock.Advance(TimeSpan.FromSeconds(1));
        var goal = await goalService.PublishDraftAsync(draft.Id, draft.Revision, OperatorId.New());
        var snapshot = await CreateSnapshotAsync(database, clock, goal);
        var actor = OperatorId.New();
        if (configureSafety)
        {
            _ = await new ExecutionSafetyService(
                database.GoalPersistence,
                database.ExecutionSafetyPersistence,
                clock).ConfigurePaperScopesAsync(goal.Id, actor);
        }
        var binding = goal.ExecutionBinding!;
        var limit = AccountAllocationLimit.Create(
            new VersionReference(
                "paper-account-limit",
                "1",
                $"digest-paper-account-limit-{Guid.NewGuid():N}"),
            binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            new Money(25_000m, "USD"),
            actor,
            clock.GetUtcNow());
        var authorityService = new PaperReviewActivationService(
            database.GoalPersistence,
            new BrokerPreflightService(new FakeBrokerConnectionProbe(
                GoalTestData.CreateObservation(goal))),
            database.AccountAllocationPersistence,
            clock);
        await authorityService.ConfigureAccountLimitAsync(limit, actor);
        var authority = await authorityService.ActivateAsync(
            goal.Id,
            limit.Reference,
            actor,
            $"Operator approved the exact {mode} authority summary.");
        var observation = CreateObservation(goal, clock.GetUtcNow(), wrongAccount);
        var expectedBinding = ExecutionSafetyService.ToAccountBinding(goal);
        var observedBinding = new IntentAccountBinding(
            expectedBinding.BrokerConnectionVersion,
            observation.Broker.Environment,
            observation.Broker.EndpointIdentity,
            observation.Broker.AccountIdentifier);
        var preflightSnapshot = new PaperBrokerReconciliationSnapshot(
            observedBinding, observation.ReconciliationObservedAt,
            observation.BuyingPower, observation.Currency, [], [], []);
        var preflightReconciliation = new PaperReconciliationService(
            database.GoalPersistence,
            new PostgresPaperReconciliationPersistence(database.DataSource),
            database.ExecutionSafetyPersistence,
            new SnapshotAdapter(preflightSnapshot),
            clock);
        var service = new PaperReviewSessionService(
            database.GoalPersistence,
            database.ActivationPersistence,
            database.SnapshotPersistence,
            database.AccountAllocationPersistence,
            database.SessionPersistence,
            database.ExecutablePreflightPersistence,
            new FakeProbe(observation),
            database.ExecutionSafetyPersistence,
            preflightReconciliation,
            clock);
        return new TestContext(goal, authority.Activation, snapshot, actor, clock, service);
    }

    private static async ValueTask<DataSnapshot> CreateSnapshotAsync(
        PostgresTestDatabase database,
        ManualTimeProvider clock,
        GoalVersion goal)
    {
        var expected = GoalTestData.CreateDataSnapshot(goal);
        return await new DataSnapshotService(database.SnapshotPersistence, clock).CreateAsync(
            expected.DataSetVersion,
            goal.DataPolicyVersion,
            expected.SchemaVersion,
            expected.RangeStart,
            expected.RangeEnd,
            expected.SourceObservations,
            expected.Payload.ToArray(),
            OperatorId.New());
    }

    private static ExecutablePreflightObservation CreateObservation(
        GoalVersion goal,
        DateTimeOffset observedAt,
        bool wrongAccount)
    {
        var binding = goal.ExecutionBinding!;
        var broker = new BrokerConnectionObservation(
            BrokerEnvironment.PAPER,
            binding.EndpointIdentity,
            wrongAccount ? "wrong-paper-account" : binding.ExpectedAccountIdentifier,
            binding.RequiredCapabilities,
            observedAt);
        return new ExecutablePreflightObservation(
            broker,
            observedAt,
            true,
            true,
            true,
            [
                goal.DataPolicyVersion,
                goal.RiskPolicyVersion,
                binding.OrderPolicyVersion,
                binding.ReconciliationPolicyVersion
            ],
            true,
            true,
            observedAt,
            true,
            true,
            observedAt,
            0m,
            0m,
            25_000m,
            "USD");
    }

    private static FakeIntentRiskProbe CreateRiskProbe(TestContext context) => new(
        new IntentRiskObservation(
            new BrokerConnectionObservation(
                BrokerEnvironment.PAPER,
                context.Goal.ExecutionBinding!.EndpointIdentity,
                context.Goal.ExecutionBinding.ExpectedAccountIdentifier,
                context.Goal.ExecutionBinding.RequiredCapabilities,
                context.Clock.GetUtcNow()),
            true,
            true,
            true,
            context.Clock.GetUtcNow(),
            0m,
            0m,
            25_000m,
            "USD"));

    private static PaperReconciliationService CreateReconciliationRunner(
        PostgresTestDatabase database,
        TestContext context,
        bool includeExternalOrder = false)
    {
        IReadOnlyList<PaperReconciliationOrder> orders = includeExternalOrder
            ?
            [
                new PaperReconciliationOrder(
                    null, "external-paper-order", "external-broker-order", "AAPL",
                    OrderSide.BUY, 1m, 100m, 0m, OrderState.ACKNOWLEDGED)
            ]
            : [];
        var snapshot = new PaperBrokerReconciliationSnapshot(
            ExecutionSafetyService.ToAccountBinding(context.Goal),
            context.Clock.GetUtcNow(), 25_000m, "USD", orders, [], []);
        return new PaperReconciliationService(
            database.GoalPersistence,
            new PostgresPaperReconciliationPersistence(database.DataSource),
            database.ExecutionSafetyPersistence,
            new SnapshotAdapter(snapshot),
            context.Clock);
    }

    private sealed class SnapshotAdapter(PaperBrokerReconciliationSnapshot snapshot)
        : IPaperBrokerReconciliationAdapter
    {
        public ValueTask<PaperBrokerReconciliationSnapshot> ObserveAsync(
            IntentAccountBinding expectedBinding, CancellationToken cancellationToken)
        {
            Assert.Equal(expectedBinding, snapshot.ObservedBinding);
            return ValueTask.FromResult(snapshot);
        }
    }

    [Fact]
    public async Task Expired_review_is_durable_audited_and_releases_unused_exposure()
    {
        await using var database = await server.CreateDatabaseAsync();
        var context = await CreateContextAsync(database);
        var (approved, _) = await CreateApprovedIntentAsync(database, context);
        var reserved = await database.ExecutionSafetyPersistence.FindReservationAsync(
            approved.Id,
            default);
        Assert.NotNull(reserved);
        Assert.Equal(ExposureReservationState.RESERVED, reserved.State);

        context.Clock.Advance(TimeSpan.FromMinutes(2));
        var service = new PaperReviewExpiryService(
            database.PaperReviewIntentPersistence,
            context.Clock);

        Assert.Equal(1, await service.ExpireDueAsync(context.Actor));
        Assert.Equal(0, await service.ExpireDueAsync(context.Actor));

        var expired = await database.PaperReviewIntentPersistence.FindIntentAsync(
            approved.Id,
            default);
        var released = await database.ExecutionSafetyPersistence.FindReservationAsync(
            approved.Id,
            default);
        Assert.NotNull(expired);
        Assert.Equal(ExecutionIntentState.EXPIRED, expired.State);
        Assert.NotNull(released);
        Assert.Equal(ExposureReservationState.RELEASED, released.State);
        Assert.Equal(0m, released.RemainingAmount);
        Assert.Equal(1L, await ExecuteScalarInt64Async(
            database.DataSource,
            "SELECT count(*) FROM audit_events WHERE event_type = 'REVIEW_RECORDED' AND resulting_state = 'EXPIRED'"));
        Assert.True((await database.AuditVerifier.VerifyAsync()).IsValid);
    }

    private static async ValueTask<(ExecutionIntent Intent, FakeIntentRiskProbe RiskProbe)>
        CreateApprovedIntentAsync(PostgresTestDatabase database, TestContext context)
    {
        var sessionOutcome = await context.Service.StartAsync(
            context.Goal.Id, context.Activation.Id, context.Snapshot.Id, context.Actor);
        var running = sessionOutcome.Session.TransitionTo(SessionState.RUNNING, context.Clock.GetUtcNow());
        await database.SessionPersistence.UpdateAsync(running, sessionOutcome.Session.State,
            sessionOutcome.Session.Revision, context.Actor, context.Clock.GetUtcNow(), default);
        var decision = Decision.Create(DecisionId.New(), running.Id, context.Goal.Id,
            context.Goal.StrategyVersion, context.Snapshot.Id, context.Clock.GetUtcNow(),
            DecisionAction.BUY, "AAPL", 10m, 100m, "Buy inside exact paper authority.", 0.8m,
            [new DecisionGuardResult("EXECUTABLE_OUTPUT", true, "Executable output is valid.")]);
        await database.DecisionPersistence.InsertAsync(decision, context.Actor, default);
        var riskProbe = CreateRiskProbe(context);
        var intentService = new PaperReviewIntentService(database.GoalPersistence,
            database.ActivationPersistence, database.AccountAllocationPersistence,
            database.SessionPersistence, database.ExecutablePreflightPersistence,
            database.DecisionPersistence, riskProbe, database.ExecutionSafetyPersistence,
            CreateReconciliationRunner(database, context),
            database.PaperReviewIntentPersistence, new FixedPaperRiskPolicyProvider(),
            context.Clock);
        var outcome = await intentService.CreateAsync(decision.Id, 10m, 100m,
            context.Clock.GetUtcNow().AddMinutes(5), context.Actor);
        var reviewService = new PaperReviewService(database.PaperReviewIntentPersistence,
            database.SessionPersistence, database.GoalPersistence, database.ActivationPersistence,
            database.AccountAllocationPersistence, database.ExecutablePreflightPersistence,
            riskProbe, database.ExecutionSafetyPersistence,
            CreateReconciliationRunner(database, context), new FixedPaperRiskPolicyProvider(),
            context.Clock, TimeSpan.FromMinutes(2));
        _ = await reviewService.ReviewAsync(outcome.Intent.Id, outcome.Intent.ContentDigest,
            ReviewResult.APPROVED, context.Actor, "Approve the exact bounded paper intent.");
        var approved = await database.PaperReviewIntentPersistence.FindIntentAsync(outcome.Intent.Id, default)
            ?? throw new InvalidOperationException("The approved intent is unavailable.");
        return (approved, riskProbe);
    }

    private static PaperOrderService CreateOrderService(PostgresTestDatabase database,
        TestContext context, IIntentRiskProbe riskProbe, IPaperBrokerOrderAdapter fake) => new(
            database.GoalPersistence,
            database.ActivationPersistence,
            database.SessionPersistence,
            database.AccountAllocationPersistence,
            database.PaperReviewIntentPersistence,
            database.ExecutionSafetyPersistence,
            riskProbe,
            database.PaperOrderPersistence,
            fake,
            new FixedPaperRiskPolicyProvider(),
            context.Clock);

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarInt64Async(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The scalar test result is unavailable."));
    }

    private static async Task<decimal> ExecuteScalarDecimalAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (decimal)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The scalar test result is unavailable."));
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class FakeProbe(ExecutablePreflightObservation observation)
        : IExecutablePreflightProbe
    {
        public ValueTask<ExecutablePreflightObservation> ObserveAsync(
            VersionReference brokerConnectionVersion,
            CancellationToken cancellationToken) => ValueTask.FromResult(observation);
    }

    private sealed class FakeIntentRiskProbe(IntentRiskObservation observation) : IIntentRiskProbe
    {
        public ValueTask<IntentRiskObservation> ObserveAsync(
            IntentAccountBinding accountBinding,
            CancellationToken cancellationToken) => ValueTask.FromResult(observation);
    }

    private sealed record TestContext(
        GoalVersion Goal,
        GoalActivation Activation,
        DataSnapshot Snapshot,
        OperatorId Actor,
        ManualTimeProvider Clock,
        PaperReviewSessionService Service);
}
