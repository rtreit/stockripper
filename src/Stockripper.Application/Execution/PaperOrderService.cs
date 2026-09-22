using Stockripper.Application.Allocations;
using Stockripper.Application.Audit;
using Stockripper.Application.Goals;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;
using Stockripper.Domain.Risk;

namespace Stockripper.Application.Execution;

public sealed class PaperOrderService(
    IGoalPersistence goalPersistence,
    IGoalActivationPersistence activationPersistence,
    ISessionPersistence sessionPersistence,
    IAccountAllocationPersistence allocationPersistence,
    IPaperReviewIntentPersistence intentPersistence,
    IExecutionSafetyPersistence safetyPersistence,
    IIntentRiskProbe riskProbe,
    IPaperOrderPersistence orderPersistence,
    IPaperBrokerOrderAdapter brokerAdapter,
    IPaperRiskPolicyProvider riskPolicyProvider,
    TimeProvider timeProvider,
    IPaperSubmissionInterlock? submissionInterlock = null,
    AuditIntegrityGuard? auditIntegrityGuard = null)
{
    public async ValueTask<PaperOrderPreparation> PrepareAsync(
        ExecutionIntentId intentId,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var intent = await intentPersistence.FindIntentAsync(intentId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The Execution intent does not exist.");
        var existing = await orderPersistence.FindByIntentAsync(intentId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new PaperOrderException("The Execution intent already has an Order.");
        }

        ReviewApproval? review = null;
        if (intent.Mode == ExecutionMode.PAPER_REVIEW)
        {
            review = await intentPersistence.FindReviewAsync(intentId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new PaperOrderException("The approved review does not exist.");
        }
        else if (intent.Mode != ExecutionMode.PAPER_AUTO)
        {
            throw new PaperOrderException("The Execution intent is not a paper intent.");
        }
        var reservation = await safetyPersistence.FindReservationAsync(intentId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The exposure reservation does not exist.");
        var goal = await goalPersistence.FindGoalVersionAsync(intent.GoalVersionId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The Goal version does not exist.");
        var riskPolicy = await riskPolicyProvider.FindAsync(
            goal.RiskPolicyVersion, cancellationToken).ConfigureAwait(false);
        if (riskPolicy is null || !riskPolicy.IsBoundTo(goal.RiskPolicyVersion))
        {
            throw new PaperOrderException("The exact paper Risk policy is unavailable.");
        }
        var session = await sessionPersistence.FindAsync(intent.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The Session does not exist.");
        var activation = await activationPersistence.FindAsync(session.ActivationId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The Goal activation does not exist.");
        var allocation = await allocationPersistence.FindByActivationAsync(session.ActivationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PaperOrderException("The account allocation does not exist.");
        var reconciliationId = await FindAuthorityReconciliationIdAsync(
            intent, review, cancellationToken).ConfigureAwait(false);
        var reconciliation = await RequireCurrentReconciliationAsync(
            reconciliationId, goal, intent.AccountBinding, riskPolicy,
            timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var observation = await riskProbe.ObserveAsync(intent.AccountBinding, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<KillSwitch> switches;
        try
        {
            switches = await safetyPersistence.FindKillSwitchesAsync(
                PaperExecutionSafetyScopes.Required(goal.Id, intent.AccountBinding), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new PaperOrderException("Durable kill-switch state is unavailable.");
        }

        var checks = CreateChecks(now, intent, review, reservation, goal, session,
            activation, allocation, observation, reconciliation, switches, riskPolicy);
        var evaluation = review is null
            ? PreSubmitEvaluation.CreateAutomatic(
                PreSubmitEvaluationId.New(), intent, reservation, reconciliation.Id,
                observation.PositionExposure, observation.OpenOrderExposure,
                observation.BuyingPower, observation.Currency, checks,
                timeProvider.GetUtcNow())
            : PreSubmitEvaluation.Create(
                PreSubmitEvaluationId.New(), intent, review, reservation,
                reconciliation.Id, observation.PositionExposure,
                observation.OpenOrderExposure, observation.BuyingPower,
                observation.Currency, checks, timeProvider.GetUtcNow());
        if (evaluation.Result == PreSubmitEvaluationResult.FAILED)
        {
            await orderPersistence.RecordEvaluationAsync(evaluation, actorId, cancellationToken)
                .ConfigureAwait(false);
            return new(evaluation, null);
        }

        var order = OrderRecord.Prepare(OrderId.New(), intent, evaluation, timeProvider.GetUtcNow());
        var pending = intent.BeginSubmission(evaluation);
        await orderPersistence.PrepareAsync(evaluation, order, pending, intent.State,
            actorId, cancellationToken).ConfigureAwait(false);
        return new(evaluation, order);
    }

    public async ValueTask<OrderRecord> SendPreparedAsync(
        OrderId orderId,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        await using var submissionLease = submissionInterlock is null
            ? null
            : await submissionInterlock.EnterAsync(cancellationToken)
                .ConfigureAwait(false);
        if (auditIntegrityGuard is not null)
        {
            try
            {
                await auditIntegrityGuard.VerifyOrThrowAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AuditIntegrityException)
            {
                throw new PaperOrderException(
                    "Audit integrity verification blocks paper Broker submission.");
            }
        }

        var order = await orderPersistence.FindOrderAsync(orderId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The Order does not exist.");
        if (order.State != OrderState.SUBMITTING)
        {
            return order;
        }

        var intent = await intentPersistence.FindIntentAsync(order.IntentId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The Execution intent does not exist.");
        await RequireSubmissionAuthorityAsync(order, intent, actorId, cancellationToken)
            .ConfigureAwait(false);
        PaperBrokerResult result;
        try
        {
            result = await brokerAdapter.SendAsync(order, cancellationToken).ConfigureAwait(false);
        }
        catch (PaperBrokerOutcomeUnknownException)
        {
            return await MarkUnknownAsync(order, intent, actorId, cancellationToken).ConfigureAwait(false);
        }

        return await ApplyBrokerResultAsync(order, intent, result, actorId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<OrderRecord>> RecoverAsync(
        OperatorId actorId,
        CancellationToken cancellationToken = default)
        => await new PaperSubmissionRecoveryService(orderPersistence, intentPersistence,
            brokerAdapter, timeProvider).RecoverAsync(actorId, cancellationToken).ConfigureAwait(false);

    private async ValueTask RequireSubmissionAuthorityAsync(
        OrderRecord order,
        ExecutionIntent intent,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var goal = await goalPersistence.FindGoalVersionAsync(
            intent.GoalVersionId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The Goal version does not exist.");
        var session = await sessionPersistence.FindAsync(
            intent.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The Session does not exist.");
        var activation = await activationPersistence.FindAsync(
            session.ActivationId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The Goal activation does not exist.");
        var allocation = await allocationPersistence.FindByActivationAsync(
            session.ActivationId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The account allocation does not exist.");
        var reservation = await safetyPersistence.FindReservationAsync(
            intent.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The exposure reservation does not exist.");
        var riskPolicy = await riskPolicyProvider.FindAsync(
            goal.RiskPolicyVersion, cancellationToken).ConfigureAwait(false);
        var expectedBinding = ExecutionSafetyService.ToAccountBinding(goal);
        var evaluation = await orderPersistence.FindEvaluationAsync(
            order.PreSubmitEvaluationId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The pre-submit evaluation does not exist.");
        ReviewApproval? review = null;
        if (intent.Mode == ExecutionMode.PAPER_REVIEW)
        {
            review = await intentPersistence.FindReviewAsync(
                intent.Id, cancellationToken).ConfigureAwait(false);
        }

        if (intent.State != ExecutionIntentState.SUBMISSION_PENDING
            || intent.Mode is not (ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO)
            || intent.AccountBinding != expectedBinding
            || order.AccountBinding != expectedBinding
            || session.GoalVersionId != goal.Id
            || session.Mode != goal.Mode
            || session.State != SessionState.RUNNING
            || activation.GoalVersionId != goal.Id
            || !activation.CanStartSession(now)
            || !goal.ExecutionSchedule.Allows(now)
            || now >= intent.ExpiresAt
            || evaluation.IntentId != intent.Id
            || evaluation.Result != PreSubmitEvaluationResult.PASSED
            || !evaluation.HasValidContentDigest()
            || riskPolicy is null
            || !riskPolicy.IsBoundTo(goal.RiskPolicyVersion))
        {
            throw new PaperOrderException(
                "Current paper authority does not permit Broker submission.");
        }

        var reconciliation = await RequireCurrentReconciliationAsync(
            evaluation.ReconciliationId, goal, expectedBinding, riskPolicy,
            now, cancellationToken).ConfigureAwait(false);
        var observation = await riskProbe.ObserveAsync(
            expectedBinding, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<KillSwitch> switches;
        try
        {
            switches = await safetyPersistence.FindKillSwitchesAsync(
                PaperExecutionSafetyScopes.Required(goal.Id, expectedBinding),
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new PaperOrderException(
                "Durable kill-switch state is unavailable.", exception);
        }

        var checks = CreateChecks(now, intent, review, reservation, goal, session,
            activation, allocation, observation, reconciliation, switches, riskPolicy);
        if (checks.Any(check => !check.Passed))
        {
            throw new PaperOrderException(
                "The final paper safety gate blocks Broker submission.");
        }
    }

    private async ValueTask<ReconciliationId> FindAuthorityReconciliationIdAsync(
        ExecutionIntent intent,
        ReviewApproval? review,
        CancellationToken cancellationToken)
    {
        if (review is not null)
        {
            return await intentPersistence.FindReviewReconciliationAsync(
                intent.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new PaperOrderException(
                    "The review reconciliation reference does not exist.");
        }

        var risk = await intentPersistence.FindRiskEvaluationAsync(
            intent.RiskEvaluationId, cancellationToken).ConfigureAwait(false)
            ?? throw new PaperOrderException("The risk evaluation does not exist.");
        if (!risk.HasValidContentDigest() || risk.IntentId != intent.Id)
        {
            throw new PaperOrderException("The risk evaluation is invalid.");
        }

        return risk.ReconciliationId;
    }

    private async ValueTask<ReconciliationRecord> RequireCurrentReconciliationAsync(
        ReconciliationId referencedId,
        GoalVersion goal,
        IntentAccountBinding binding,
        PaperRiskPolicy riskPolicy,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var referenced = await safetyPersistence.FindReconciliationAsync(
            referencedId, cancellationToken).ConfigureAwait(false);
        var latest = await safetyPersistence.FindLatestCurrentReconciliationAsync(
            goal.Id, binding, cancellationToken).ConfigureAwait(false);
        if (referenced is null
            || latest is null
            || latest.CompletedAt < referenced.CompletedAt
            || !ExecutionSafetyService.IsCurrentReconciliation(
                referenced, goal, binding, riskPolicy, at)
            || !ExecutionSafetyService.IsCurrentReconciliation(
                latest, goal, binding, riskPolicy, at))
        {
            throw new PaperOrderException(
                "Current detailed reconciliation does not permit paper submission.");
        }

        return latest;
    }

    private async ValueTask<OrderRecord> MarkUnknownAsync(OrderRecord order, ExecutionIntent intent,
        OperatorId actorId, CancellationToken cancellationToken)
    {
        var unknown = order.MarkSubmissionUnknown(timeProvider.GetUtcNow());
        var unknownIntent = intent.MarkSubmissionUnknown(unknown);
        await orderPersistence.RecordTransitionAsync(unknown, order.State, unknownIntent, intent.State,
            null, actorId, cancellationToken).ConfigureAwait(false);
        return unknown;
    }

    private async ValueTask<OrderRecord> ApplyBrokerResultAsync(OrderRecord order, ExecutionIntent intent,
        PaperBrokerResult result, OperatorId actorId, CancellationToken cancellationToken)
    {
        if (result.Type is not (PaperBrokerResultType.ACKNOWLEDGED or PaperBrokerResultType.REJECTED)
            || string.IsNullOrWhiteSpace(result.EventReference))
        {
            if (order.State == OrderState.SUBMITTING)
            {
                return await MarkUnknownAsync(order, intent, actorId, cancellationToken).ConfigureAwait(false);
            }

            return order;
        }

        var eventResult = result.Type == PaperBrokerResultType.ACKNOWLEDGED
            ? BrokerOrderEventResult.ACKNOWLEDGED
            : BrokerOrderEventResult.REJECTED;
        var brokerEvent = BrokerOrderEvent.Create(BrokerOrderEventId.New(), order,
            result.BrokerOrderId, result.EventReference, eventResult, result.ObservedAt);
        var updated = result.Type == PaperBrokerResultType.ACKNOWLEDGED
            ? order.Acknowledge(result.BrokerOrderId!, result.EventReference, result.ObservedAt)
            : order.Reject(result.EventReference, result.ObservedAt);
        var updatedIntent = result.Type == PaperBrokerResultType.ACKNOWLEDGED
            ? intent.MarkSubmitted(updated)
            : intent.MarkSubmissionFailed(updated);
        await orderPersistence.RecordTransitionAsync(updated, order.State, updatedIntent, intent.State,
            brokerEvent, actorId, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static IReadOnlyList<PreSubmitCheck> CreateChecks(DateTimeOffset now,
        ExecutionIntent intent, ReviewApproval? review, ExposureReservation reservation,
        GoalVersion goal, Session session, GoalActivation activation,
        AccountAllocationEntry allocation, IntentRiskObservation observation,
        ReconciliationRecord reconciliation, IReadOnlyList<KillSwitch> switches,
        PaperRiskPolicy riskPolicy)
    {
        var exactBinding = observation.Broker.Environment == BrokerEnvironment.PAPER
            && observation.Broker.Environment == intent.AccountBinding.Environment
            && string.Equals(observation.Broker.EndpointIdentity, intent.AccountBinding.EndpointIdentity, StringComparison.Ordinal)
            && string.Equals(observation.Broker.AccountIdentifier, intent.AccountBinding.AccountIdentifier, StringComparison.Ordinal);
        var authority = activation.GoalVersionId == goal.Id && activation.CanStartSession(now)
            && session.GoalVersionId == goal.Id && session.State == SessionState.RUNNING;
        var allocationCurrent = allocation.State == AccountAllocationState.ACTIVE
            && allocation.Id == reservation.AllocationEntryId
            && allocation.ActivationId == activation.Id
            && allocation.GoalVersionId == goal.Id
            && allocation.BrokerConnectionVersion == intent.AccountBinding.BrokerConnectionVersion
            && allocation.Environment == BrokerEnvironment.PAPER
            && string.Equals(allocation.EndpointIdentity, intent.AccountBinding.EndpointIdentity, StringComparison.Ordinal)
            && string.Equals(allocation.AccountIdentifier, intent.AccountBinding.AccountIdentifier, StringComparison.Ordinal)
            && allocation.Allocation.Currency == reservation.Currency
            && allocation.EffectiveStart <= now
            && (allocation.EffectiveEnd is null || now < allocation.EffectiveEnd)
            && allocation.HasValidContentDigest()
            && reservation.GoalVersionId == goal.Id
            && reservation.AccountBinding == intent.AccountBinding
            && reservation.HasValidContentDigest()
            && observation.PositionExposure + observation.OpenOrderExposure + reservation.Amount <= allocation.Allocation.Amount;
        _ = observation.PositionQuantities.TryGetValue(
            intent.InstrumentIdentifier, out var currentInstrumentQuantity);
        var projectedInstrumentQuantity = intent.Side == OrderSide.SELL
            ? currentInstrumentQuantity - intent.Quantity
            : currentInstrumentQuantity + intent.Quantity;
        var projectedInstrumentExposure = Math.Abs(
            projectedInstrumentQuantity * intent.LimitPrice);
        var totalExposure = observation.PositionExposure
            + observation.OpenOrderExposure + reservation.Amount;
        var policyExposureCurrent = reservation.Amount <= riskPolicy.MaximumOrderExposure
            && projectedInstrumentExposure <= riskPolicy.MaximumPositionExposure
            && totalExposure <= riskPolicy.MaximumPortfolioExposure
            && (totalExposure == 0m
                || projectedInstrumentExposure / totalExposure
                    <= riskPolicy.MaximumConcentration)
            && observation.OpenOrderCount + 1 <= riskPolicy.MaximumOpenOrders;
        var requiredSwitchCount = PaperExecutionSafetyScopes.Required(goal.Id, intent.AccountBinding).Count;
        return
        [
            Check(PreSubmitCheckCode.ACTIVE_AUTHORITY, authority),
            Check(PreSubmitCheckCode.ACCOUNT_BINDING, exactBinding),
            Check(PreSubmitCheckCode.APPROVAL_CURRENT,
                intent.Mode == ExecutionMode.PAPER_AUTO
                    ? review is null
                        && intent.State is ExecutionIntentState.RISK_ACCEPTED
                            or ExecutionIntentState.SUBMISSION_PENDING
                        && now < intent.ExpiresAt
                    : review?.Result == ReviewResult.APPROVED
                        && review.IntentDigest == intent.ContentDigest
                        && now < review.ExpiresAt && now < intent.ExpiresAt),
            Check(PreSubmitCheckCode.RECONCILIATION_CURRENT,
                observation.ReconciliationCurrent
                    && reconciliation.Result == ReconciliationResult.CURRENT
                    && reconciliation.HasValidContentDigest()
                    && observation.PositionExposure == reconciliation.PositionExposure
                    && observation.OpenOrderExposure == reconciliation.OpenOrderExposure
                    && observation.BuyingPower == reconciliation.BuyingPower
                    && string.Equals(
                        observation.Currency, reconciliation.Currency,
                        StringComparison.Ordinal)),
            Check(PreSubmitCheckCode.EXPOSURE_RESERVED, reservation.State == ExposureReservationState.RESERVED && reservation.IntentId == intent.Id),
            Check(PreSubmitCheckCode.CAPITAL_ALLOCATION,
                allocationCurrent && observation.CapitalDataCurrent && policyExposureCurrent),
            Check(PreSubmitCheckCode.BUYING_POWER,
                observation.BuyingPower >= reservation.Amount
                    && observation.BuyingPower - reservation.Amount
                        >= riskPolicy.MinimumBuyingPowerReserve),
            Check(PreSubmitCheckCode.INSTRUMENT_PERMISSION,
                observation.Broker.Capabilities.Contains("EQUITY_LIMIT_DAY")
                    && riskPolicy.AllowedInstruments.Contains(intent.InstrumentIdentifier)
                    && projectedInstrumentQuantity >= 0m),
            Check(PreSubmitCheckCode.PRICE_VALID,
                intent.LimitPrice > 0m
                    && decimal.Truncate(intent.Quantity) == intent.Quantity
                    && reservation.Amount <= riskPolicy.MaximumOrderExposure),
            Check(PreSubmitCheckCode.DATA_FRESHNESS,
                observation.ObservedAt <= now
                    && now - observation.ObservedAt <= riskPolicy.MaximumMarketDataAge),
            Check(PreSubmitCheckCode.SCHEDULE_OPEN, goal.ExecutionSchedule.Allows(now)),
            Check(PreSubmitCheckCode.KILL_SWITCH_CLEAR, switches.Count == requiredSwitchCount && switches.All(item => item.State == KillSwitchState.CLEAR)),
            Check(PreSubmitCheckCode.POST_FILL_CURRENT, observation.PostFillCurrent)
        ];
    }

    private static PreSubmitCheck Check(PreSubmitCheckCode code, bool passed) =>
        new(code, passed, passed ? "The final check passed." : "The final check failed closed.");
}
