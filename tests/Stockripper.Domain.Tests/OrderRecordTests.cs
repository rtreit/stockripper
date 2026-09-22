using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class OrderRecordTests
{
    [Fact]
    public void Passed_evaluation_prepares_stable_paper_order_before_submission()
    {
        var authority = CreateAuthority();

        var order = OrderRecord.Prepare(
            OrderId.New(),
            authority.Intent,
            authority.Evaluation,
            authority.Now.AddSeconds(2));
        var pending = authority.Intent.BeginSubmission(authority.Evaluation);

        Assert.Equal(OrderState.SUBMITTING, order.State);
        Assert.Equal($"sr-{authority.Intent.Id.Value:N}", order.BrokerClientOrderId);
        Assert.Equal($"stockripper-order:{authority.Intent.Id.Value:D}", order.IdempotencyKey);
        Assert.Equal(ExecutionIntentState.SUBMISSION_PENDING, pending.State);
    }

    [Fact]
    public void Paper_auto_prepares_without_a_review_approval()
    {
        var authority = CreateAuthority(mode: ExecutionMode.PAPER_AUTO);

        var order = OrderRecord.Prepare(
            OrderId.New(), authority.Intent, authority.Evaluation,
            authority.Now.AddSeconds(2));
        var pending = authority.Intent.BeginSubmission(authority.Evaluation);

        Assert.Null(authority.Evaluation.ApprovalId);
        Assert.Equal(OrderState.SUBMITTING, order.State);
        Assert.Equal(ExecutionIntentState.SUBMISSION_PENDING, pending.State);
    }

    [Fact]
    public void Failed_final_check_cannot_prepare_an_order()
    {
        var authority = CreateAuthority(PreSubmitCheckCode.KILL_SWITCH_CLEAR);

        Assert.Equal(PreSubmitEvaluationResult.FAILED, authority.Evaluation.Result);
        Assert.Throws<DomainRuleViolationException>(() => OrderRecord.Prepare(
            OrderId.New(), authority.Intent, authority.Evaluation, authority.Now.AddSeconds(2)));
    }

    [Fact]
    public void Timeout_moves_order_to_unknown_and_restart_restore_preserves_identity()
    {
        var authority = CreateAuthority();
        var order = OrderRecord.Prepare(OrderId.New(), authority.Intent, authority.Evaluation,
            authority.Now.AddSeconds(2)).MarkSubmissionUnknown(authority.Now.AddSeconds(3));
        var intent = authority.Intent.BeginSubmission(authority.Evaluation).MarkSubmissionUnknown(order);

        var restoredOrder = OrderRecord.Restore(order.Id, order.IntentId, order.IntentDigest,
            order.PreSubmitEvaluationId, order.AccountBinding, order.Request, order.IdempotencyKey,
            order.BrokerClientOrderId, order.State, order.BrokerOrderId, order.BrokerEventReference,
            order.FilledQuantity, order.FirstSubmissionTime, order.LastUpdatedAt, order.TerminalAt);
        var restoredIntent = ExecutionIntent.Restore(intent.Id, intent.DecisionId, intent.GoalVersionId,
            intent.SessionId, intent.Mode, intent.AccountBinding, intent.InstrumentIdentifier, intent.Side,
            intent.OrderType, intent.Quantity, intent.LimitPrice, intent.TimeInForce, intent.ExpiresAt,
            intent.RiskEvaluationId, intent.IdempotencyKey, intent.CreatedAt, intent.State, intent.ContentDigest);

        Assert.Equal(OrderState.SUBMISSION_UNKNOWN, restoredOrder.State);
        Assert.Equal(order.BrokerClientOrderId, restoredOrder.BrokerClientOrderId);
        Assert.Equal(ExecutionIntentState.SUBMISSION_UNKNOWN, restoredIntent.State);
    }

    [Fact]
    public void Unknown_order_can_resolve_by_client_identity_without_new_order()
    {
        var authority = CreateAuthority();
        var unknown = OrderRecord.Prepare(OrderId.New(), authority.Intent, authority.Evaluation,
            authority.Now.AddSeconds(2)).MarkSubmissionUnknown(authority.Now.AddSeconds(3));

        var acknowledged = unknown.Acknowledge(
            "paper-order-17", "paper-event-17", authority.Now.AddSeconds(4));
        var submitted = authority.Intent.BeginSubmission(authority.Evaluation)
            .MarkSubmissionUnknown(unknown)
            .MarkSubmitted(acknowledged);

        Assert.Equal(OrderState.ACKNOWLEDGED, acknowledged.State);
        Assert.Equal(unknown.Id, acknowledged.Id);
        Assert.Equal(unknown.BrokerClientOrderId, acknowledged.BrokerClientOrderId);
        Assert.Equal(ExecutionIntentState.SUBMITTED, submitted.State);
    }

    [Fact]
    public void Broker_fills_advance_quantity_without_regression_from_late_events()
    {
        var authority = CreateAuthority();
        var order = OrderRecord.Prepare(OrderId.New(), authority.Intent, authority.Evaluation,
            authority.Now.AddSeconds(2));
        var partialEvent = CreateEvent(order, BrokerExecutionEventKind.PARTIALLY_FILLED,
            4m, authority.Now.AddSeconds(3), "event-partial");
        var partial = order.Apply(partialEvent);
        var filledEvent = CreateEvent(partial, BrokerExecutionEventKind.FILLED,
            10m, authority.Now.AddSeconds(4), "event-filled");
        var filled = partial.Apply(filledEvent);
        var latePartial = CreateEvent(filled, BrokerExecutionEventKind.PARTIALLY_FILLED,
            4m, authority.Now.AddSeconds(5), "event-late-partial");

        Assert.Equal(OrderState.PARTIALLY_FILLED, partial.State);
        Assert.Equal(4m, partial.FilledQuantity);
        Assert.Equal(OrderState.FILLED, filled.State);
        Assert.Equal(10m, filled.FilledQuantity);
        Assert.Equal(filled, filled.Apply(latePartial));
    }

    [Fact]
    public void Broker_event_rejects_wrong_paper_account()
    {
        var authority = CreateAuthority();
        var order = OrderRecord.Prepare(OrderId.New(), authority.Intent, authority.Evaluation,
            authority.Now.AddSeconds(2));
        var wrongBinding = new IntentAccountBinding(order.AccountBinding.BrokerConnectionVersion,
            order.AccountBinding.Environment, order.AccountBinding.EndpointIdentity, "wrong-paper-account");

        Assert.Throws<DomainRuleViolationException>(() => BrokerExecutionEvent.Create(
            BrokerExecutionEventId.New(), order, wrongBinding, order.BrokerClientOrderId,
            "broker-order", "event-wrong-account", BrokerExecutionEventKind.ACKNOWLEDGED,
            0m, authority.Now.AddSeconds(3), authority.Now.AddSeconds(3), "payload/wrong"));
    }

    [Fact]
    public void Fill_preserves_broker_identity_and_payload_reference()
    {
        var authority = CreateAuthority();
        var order = OrderRecord.Prepare(OrderId.New(), authority.Intent, authority.Evaluation,
            authority.Now.AddSeconds(2));
        var brokerEvent = CreateEvent(order, BrokerExecutionEventKind.PARTIALLY_FILLED,
            4m, authority.Now.AddSeconds(3), "event-fill");

        var fill = FillRecord.Create(FillId.New(), brokerEvent, order, "broker-fill-1",
            4m, 99.50m, 0.10m, "USD", authority.Now.AddSeconds(2.5));

        Assert.Equal("broker-fill-1", fill.BrokerFillId);
        Assert.Equal("broker-order", fill.BrokerOrderId);
        Assert.Equal("payload/event-fill", fill.SourcePayloadReference);
        Assert.Equal(64, fill.ContentDigest.Length);
    }

    [Fact]
    public void Cancellation_request_keeps_order_nonterminal_until_broker_confirmation()
    {
        var authority = CreateAuthority();
        var prepared = OrderRecord.Prepare(OrderId.New(), authority.Intent,
            authority.Evaluation, authority.Now.AddSeconds(2));
        var acknowledged = prepared.Acknowledge("broker-order", "event-ack",
            authority.Now.AddSeconds(3));

        var request = CancellationRequest.Prepare(CancellationRequestId.New(),
            acknowledged, "The Operator requested cancellation.", authority.Now.AddSeconds(4));
        var pending = acknowledged.RequestCancellation(authority.Now.AddSeconds(4));
        var accepted = request.BrokerAccepted("cancel-request-1", authority.Now.AddSeconds(5));

        Assert.Equal(OrderState.CANCEL_PENDING, pending.State);
        Assert.Null(pending.TerminalAt);
        Assert.Equal(CancellationRequestState.REQUESTED, accepted.State);
        Assert.False(accepted.IsResolved);
    }

    [Fact]
    public void Fill_race_supersedes_cancellation_without_quantity_or_state_regression()
    {
        var authority = CreateAuthority();
        var prepared = OrderRecord.Prepare(OrderId.New(), authority.Intent,
            authority.Evaluation, authority.Now.AddSeconds(2));
        var acknowledged = prepared.Acknowledge("broker-order", "event-ack",
            authority.Now.AddSeconds(3));
        var request = CancellationRequest.Prepare(CancellationRequestId.New(),
            acknowledged, "The Operator requested cancellation.", authority.Now.AddSeconds(4))
            .BrokerAccepted("cancel-request-2", authority.Now.AddSeconds(5));
        var pending = acknowledged.RequestCancellation(authority.Now.AddSeconds(4));
        var fillEvent = CreateEvent(pending, BrokerExecutionEventKind.FILLED,
            10m, authority.Now.AddSeconds(6), "event-filled-after-cancel");

        var filled = pending.Apply(fillEvent);
        var resolved = request.Resolve(fillEvent);

        Assert.Equal(OrderState.FILLED, filled.State);
        Assert.Equal(10m, filled.FilledQuantity);
        Assert.Equal(CancellationRequestState.SUPERSEDED, resolved.State);
        Assert.Equal(fillEvent.EventReference, resolved.ResolutionEventReference);
    }

    [Fact]
    public void Pre_submit_evaluation_restores_and_detects_tampering()
    {
        var evaluation = CreateAuthority().Evaluation;

        var restored = PreSubmitEvaluation.Restore(
            evaluation.Id, evaluation.IntentId, evaluation.IntentDigest,
            evaluation.ApprovalId, evaluation.ReservationId,
            evaluation.ReconciliationId, evaluation.PositionExposure,
            evaluation.OpenOrderExposure, evaluation.BuyingPower,
            evaluation.Currency, evaluation.Checks, evaluation.Result,
            evaluation.EvaluatedAt, evaluation.ContentDigest);

        Assert.Equal(evaluation.Id, restored.Id);
        Assert.Equal(evaluation.ContentDigest, restored.ContentDigest);
        Assert.Equal(evaluation.ReconciliationId, restored.ReconciliationId);
        Assert.Equal(evaluation.Checks, restored.Checks);
        Assert.True(restored.HasValidContentDigest());
        Assert.Throws<InvalidDataException>(() => PreSubmitEvaluation.Restore(
            evaluation.Id, evaluation.IntentId, evaluation.IntentDigest,
            evaluation.ApprovalId, evaluation.ReservationId,
            evaluation.ReconciliationId, evaluation.PositionExposure + 1m,
            evaluation.OpenOrderExposure, evaluation.BuyingPower,
            evaluation.Currency, evaluation.Checks, evaluation.Result,
            evaluation.EvaluatedAt, evaluation.ContentDigest));
    }

    [Fact]
    public void Every_durable_order_state_restores_with_the_same_external_identity()
    {
        var authority = CreateAuthority();
        var submitting = OrderRecord.Prepare(OrderId.New(), authority.Intent,
            authority.Evaluation, authority.Now.AddSeconds(2));
        var acknowledged = submitting.Acknowledge(
            "broker-order", "event-ack", authority.Now.AddSeconds(3));
        var partiallyFilled = acknowledged.Apply(CreateEvent(
            acknowledged, BrokerExecutionEventKind.PARTIALLY_FILLED,
            4m, authority.Now.AddSeconds(4), "event-partial"));
        var filled = partiallyFilled.Apply(CreateEvent(
            partiallyFilled, BrokerExecutionEventKind.FILLED,
            10m, authority.Now.AddSeconds(5), "event-filled"));
        var cancelPending = acknowledged.RequestCancellation(
            authority.Now.AddSeconds(4));
        var canceled = cancelPending.Apply(CreateEvent(
            cancelPending, BrokerExecutionEventKind.CANCELED,
            0m, authority.Now.AddSeconds(5), "event-canceled"));
        var rejected = submitting.Reject(
            "event-rejected", authority.Now.AddSeconds(3));
        var expired = acknowledged.Apply(CreateEvent(
            acknowledged, BrokerExecutionEventKind.EXPIRED,
            0m, authority.Now.AddSeconds(4), "event-expired"));
        var unknown = submitting.MarkSubmissionUnknown(
            authority.Now.AddSeconds(3));
        var reconciliationRequired = unknown.RequireReconciliation(
            authority.Now.AddSeconds(4));
        OrderRecord[] values =
        [
            submitting,
            acknowledged,
            partiallyFilled,
            filled,
            cancelPending,
            canceled,
            rejected,
            expired,
            unknown,
            reconciliationRequired
        ];

        foreach (var value in values)
        {
            var restored = OrderRecord.Restore(
                value.Id, value.IntentId, value.IntentDigest,
                value.PreSubmitEvaluationId, value.AccountBinding,
                value.Request, value.IdempotencyKey,
                value.BrokerClientOrderId, value.State,
                value.BrokerOrderId, value.BrokerEventReference,
                value.FilledQuantity, value.FirstSubmissionTime,
                value.LastUpdatedAt, value.TerminalAt);

            Assert.Equal(value, restored);
            Assert.Equal(submitting.BrokerClientOrderId,
                restored.BrokerClientOrderId);
        }
    }

    private static BrokerExecutionEvent CreateEvent(OrderRecord order,
        BrokerExecutionEventKind kind, decimal cumulativeQuantity,
        DateTimeOffset receivedAt, string eventReference) => BrokerExecutionEvent.Create(
        BrokerExecutionEventId.New(), order, order.AccountBinding, order.BrokerClientOrderId,
        "broker-order", eventReference, kind, cumulativeQuantity,
        receivedAt.AddMilliseconds(-100), receivedAt, $"payload/{eventReference}");

    private static Authority CreateAuthority(
        PreSubmitCheckCode? failedCheck = null,
        ExecutionMode mode = ExecutionMode.PAPER_REVIEW)
    {
        var now = GoalTestData.ReferenceTime;
        var goal = GoalTestData.CreateGoalVersion(mode);
        var binding = goal.ExecutionBinding!;
        var intentId = ExecutionIntentId.New();
        var decisionId = DecisionId.New();
        var sessionId = SessionId.New();
        var riskId = RiskEvaluationId.New();
        var allocationId = AccountAllocationEntryId.New();
        var accountBinding = new IntentAccountBinding(binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER, binding.EndpointIdentity, binding.ExpectedAccountIdentifier);
        var created = ExecutionIntent.Create(intentId, decisionId, goal.Id, sessionId,
            mode, accountBinding, "AAPL", OrderSide.BUY,
            IntentOrderType.LIMIT, 10m, 100m, IntentTimeInForce.DAY,
            now.AddMinutes(5), riskId, now);
        var risk = RiskEvaluation.Create(riskId, intentId, decisionId, goal.Id, sessionId,
            goal.RiskPolicyVersion, allocationId, ReconciliationId.New(), 100m, 100m, 1_000m,
            20_000m, "USD", [new RiskCheck(RiskCheckCode.CAPITAL_ALLOCATION, true, "Within allocation.")], now);
        var riskAccepted = created.ApplyRisk(risk);
        ReviewApproval? approval = null;
        var intent = riskAccepted;
        if (mode == ExecutionMode.PAPER_REVIEW)
        {
            var reviewPending = riskAccepted.AwaitReview();
            approval = ReviewApproval.Create(ReviewApprovalId.New(), intentId,
                OperatorId.New(), ReviewResult.APPROVED, now.AddMilliseconds(100),
                reviewPending.ContentDigest, now.AddMinutes(4),
                "Approve the bounded paper order.");
            intent = reviewPending.ApplyReview(approval);
        }
        var reservation = ExposureReservation.Reserve(ExposureReservationId.New(), intent,
            allocationId, "USD", now.AddMilliseconds(200));
        var checks = Enum.GetValues<PreSubmitCheckCode>()
            .Select(code => new PreSubmitCheck(code, code != failedCheck, $"{code} final check."))
            .ToArray();
        var evaluation = approval is null
            ? PreSubmitEvaluation.CreateAutomatic(PreSubmitEvaluationId.New(), intent,
                reservation, ReconciliationId.New(), 100m, 100m, 20_000m,
                "USD", checks, now.AddSeconds(1))
            : PreSubmitEvaluation.Create(PreSubmitEvaluationId.New(), intent, approval,
                reservation, ReconciliationId.New(), 100m, 100m, 20_000m,
                "USD", checks, now.AddSeconds(1));
        return new Authority(now, intent, evaluation);
    }

    private sealed record Authority(
        DateTimeOffset Now,
        ExecutionIntent Intent,
        PreSubmitEvaluation Evaluation);
}
