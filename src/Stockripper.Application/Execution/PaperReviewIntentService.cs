using Stockripper.Application.Allocations;
using Stockripper.Application.Decisions;
using Stockripper.Application.Goals;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;
using Stockripper.Domain.Risk;

namespace Stockripper.Application.Execution;

public sealed class PaperReviewIntentService(
    IGoalPersistence goalPersistence,
    IGoalActivationPersistence activationPersistence,
    IAccountAllocationPersistence allocationPersistence,
    ISessionPersistence sessionPersistence,
    IExecutablePreflightPersistence preflightPersistence,
    IDecisionPersistence decisionPersistence,
    IIntentRiskProbe riskProbe,
    IExecutionSafetyPersistence safetyPersistence,
    IPaperReconciliationRunner reconciliationRunner,
    IPaperReviewIntentPersistence intentPersistence,
    IPaperRiskPolicyProvider riskPolicyProvider,
    TimeProvider timeProvider)
{
    public async ValueTask<PaperReviewIntentOutcome> CreateAsync(
        DecisionId decisionId,
        decimal quantity,
        decimal limitPrice,
        DateTimeOffset expiresAt,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var decision = await decisionPersistence.FindAsync(decisionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure("The Decision does not exist.");
        var session = await sessionPersistence.FindAsync(decision.SessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure("The Session does not exist.");
        var goal = await goalPersistence.FindGoalVersionAsync(decision.GoalVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure("The Goal version does not exist.");
        var riskPolicy = await riskPolicyProvider.FindAsync(
            goal.RiskPolicyVersion, cancellationToken).ConfigureAwait(false);
        if (riskPolicy is null || !riskPolicy.IsBoundTo(goal.RiskPolicyVersion))
        {
            throw Failure("The exact paper Risk policy is unavailable.");
        }
        var activation = await activationPersistence.FindAsync(session.ActivationId, cancellationToken)
            .ConfigureAwait(false);
        var allocation = await allocationPersistence.FindByActivationAsync(
            session.ActivationId,
            cancellationToken).ConfigureAwait(false);
        var preflight = await preflightPersistence.FindAsync(session.Id, cancellationToken)
            .ConfigureAwait(false);
        if (goal.Mode is not (ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO)
            || goal.ExecutionBinding is null
            || session.Mode != goal.Mode
            || session.State != SessionState.RUNNING
            || decision.State != DecisionState.PROPOSED
            || decision.ProposedAction is not (DecisionAction.BUY or DecisionAction.SELL)
            || string.IsNullOrWhiteSpace(decision.InstrumentIdentifier)
            || quantity <= 0m
            || limitPrice <= 0m)
        {
            throw Failure("The Decision cannot create a paper Execution intent.");
        }

        var binding = goal.ExecutionBinding;
        if (activation?.GoalVersionId != goal.Id
            || !activation.CanStartSession(timeProvider.GetUtcNow())
            || allocation?.State != AccountAllocationState.ACTIVE
            || allocation.GoalVersionId != goal.Id
            || allocation.ActivationId != session.ActivationId
            || allocation.BrokerConnectionVersion != binding.BrokerConnectionVersion
            || allocation.Environment != BrokerEnvironment.PAPER
            || !string.Equals(allocation.EndpointIdentity, binding.EndpointIdentity, StringComparison.Ordinal)
            || !string.Equals(
                allocation.AccountIdentifier,
                binding.ExpectedAccountIdentifier,
                StringComparison.Ordinal)
            || preflight?.Result != ExecutablePreflightResult.PASSED
            || !preflight.HasValidContentDigest())
        {
            throw Failure("Current paper Execution authority is invalid.");
        }

        decimal requestedExposure;
        try
        {
            requestedExposure = checked(quantity * limitPrice);
        }
        catch (OverflowException)
        {
            throw Failure("The requested exposure cannot be calculated.");
        }

        var accountBinding = new IntentAccountBinding(
            binding.BrokerConnectionVersion,
            binding.ExpectedEnvironment,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier);
        try
        {
            await ExecutionSafetyService.RequireAllClearAsync(
                safetyPersistence,
                goal.Id,
                accountBinding,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionSafetyException exception)
        {
            throw Failure(exception.Message);
        }

        IntentRiskObservation observation;
        try
        {
            observation = await riskProbe.ObserveAsync(accountBinding, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failure("Current risk evidence is unavailable.");
        }

        ReconciliationRecord reconciliation;
        try
        {
            reconciliation = await reconciliationRunner.ReconcileAsync(
                goal.Id, actorId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failure("Detailed paper reconciliation is unavailable.");
        }

        var evaluatedAt = timeProvider.GetUtcNow();
        if (!observation.KillSwitchClear
            || !observation.ReconciliationCurrent
            || observation.PositionExposure != reconciliation.PositionExposure
            || observation.OpenOrderExposure != reconciliation.OpenOrderExposure
            || observation.BuyingPower != reconciliation.BuyingPower
            || !string.Equals(
                observation.Currency, reconciliation.Currency, StringComparison.Ordinal)
            || !ExecutionSafetyService.IsCurrentReconciliation(
                reconciliation, goal, accountBinding, riskPolicy, evaluatedAt))
        {
            throw Failure("Current durable safety evidence blocks new Execution intents.");
        }

        try
        {
            await ExecutionSafetyService.RequireAllClearAsync(
                safetyPersistence,
                goal.Id,
                accountBinding,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionSafetyException exception)
        {
            throw Failure(exception.Message);
        }

        var intentId = ExecutionIntentId.New();
        var riskId = RiskEvaluationId.New();
        var checks = Evaluate(
            goal,
            activation,
            session,
            decision,
            allocation,
            preflight,
            observation,
            riskPolicy,
            quantity,
            limitPrice,
            requestedExposure,
            evaluatedAt);
        var riskEvaluation = RiskEvaluation.Create(
            riskId,
            intentId,
            decision.Id,
            goal.Id,
            session.Id,
            goal.RiskPolicyVersion,
            allocation.Id,
            reconciliation.Id,
            observation.PositionExposure,
            observation.OpenOrderExposure,
            requestedExposure,
            observation.BuyingPower,
            observation.Currency,
            checks,
            evaluatedAt);
        var intent = ExecutionIntent.Create(
            intentId,
            decision.Id,
            goal.Id,
            session.Id,
            goal.Mode,
            accountBinding,
            decision.InstrumentIdentifier,
            decision.ProposedAction == DecisionAction.BUY ? OrderSide.BUY : OrderSide.SELL,
            IntentOrderType.LIMIT,
            quantity,
            limitPrice,
            IntentTimeInForce.DAY,
            expiresAt,
            riskId,
            evaluatedAt).ApplyRisk(riskEvaluation);
        ExposureReservation? reservation = null;
        if (intent.State == ExecutionIntentState.RISK_ACCEPTED
            && intent.Mode == ExecutionMode.PAPER_REVIEW)
        {
            intent = intent.AwaitReview();
        }
        else if (intent.State == ExecutionIntentState.RISK_ACCEPTED
            && intent.Mode == ExecutionMode.PAPER_AUTO)
        {
            reservation = ExposureReservation.Reserve(
                ExposureReservationId.New(), intent, allocation.Id,
                allocation.Allocation.Currency, evaluatedAt);
        }

        await intentPersistence.CreateAsync(
            riskEvaluation,
            intent,
            reservation,
            reconciliation.Id,
            actorId,
            cancellationToken).ConfigureAwait(false);
        return new PaperReviewIntentOutcome(riskEvaluation, intent);
    }

    private static IReadOnlyList<RiskCheck> Evaluate(
        GoalVersion goal,
        GoalActivation? activation,
        Session session,
        Decision decision,
        AccountAllocationEntry? allocation,
        ExecutableSessionPreflight? preflight,
        IntentRiskObservation observation,
        PaperRiskPolicy riskPolicy,
        decimal quantity,
        decimal limitPrice,
        decimal requestedExposure,
        DateTimeOffset at)
    {
        var binding = goal.ExecutionBinding!;
        var exactAllocation = allocation is not null
            && allocation.State == AccountAllocationState.ACTIVE
            && allocation.GoalVersionId == goal.Id
            && allocation.ActivationId == session.ActivationId
            && allocation.BrokerConnectionVersion == binding.BrokerConnectionVersion
            && allocation.Environment == BrokerEnvironment.PAPER
            && string.Equals(allocation.EndpointIdentity, binding.EndpointIdentity, StringComparison.Ordinal)
            && string.Equals(allocation.AccountIdentifier, binding.ExpectedAccountIdentifier, StringComparison.Ordinal)
            && allocation.Allocation == goal.CapitalAllocation;
        var exactPreflight = preflight is not null
            && preflight.Result == ExecutablePreflightResult.PASSED
            && preflight.SessionId == session.Id
            && preflight.GoalVersionId == goal.Id
            && preflight.ActivationId == session.ActivationId
            && preflight.HasValidContentDigest();
        var exactBroker = observation.Broker.Environment == BrokerEnvironment.PAPER
            && observation.Broker.Environment == binding.ExpectedEnvironment
            && string.Equals(
                observation.Broker.EndpointIdentity,
                binding.EndpointIdentity,
                StringComparison.Ordinal)
            && string.Equals(
                observation.Broker.AccountIdentifier,
                binding.ExpectedAccountIdentifier,
                StringComparison.Ordinal);
        var currencyMatches = string.Equals(
            observation.Currency,
            goal.CapitalAllocation.Currency,
            StringComparison.Ordinal);
        var totalExposure = observation.PositionExposure
            + observation.OpenOrderExposure
            + requestedExposure;
        _ = observation.PositionQuantities.TryGetValue(
            decision.InstrumentIdentifier!, out var currentInstrumentQuantity);
        var projectedInstrumentQuantity = decision.ProposedAction == DecisionAction.SELL
            ? currentInstrumentQuantity - quantity
            : currentInstrumentQuantity + quantity;
        var projectedInstrumentExposure = Math.Abs(projectedInstrumentQuantity * limitPrice);
        var concentrationWithinLimit = totalExposure == 0m
            || projectedInstrumentExposure / totalExposure <= riskPolicy.MaximumConcentration;
        var policyLimitsPass = requestedExposure <= riskPolicy.MaximumOrderExposure
            && projectedInstrumentExposure <= riskPolicy.MaximumPositionExposure
            && totalExposure <= riskPolicy.MaximumPortfolioExposure
            && concentrationWithinLimit
            && observation.OpenOrderCount + 1 <= riskPolicy.MaximumOpenOrders;

        return
        [
            Check(
                RiskCheckCode.AUTHORITY,
                activation?.GoalVersionId == goal.Id && activation.CanStartSession(at),
                "Goal authority must remain active and exact."),
            Check(
                RiskCheckCode.PREFLIGHT,
                exactPreflight,
                "Executable Session preflight evidence must remain valid."),
            Check(
                RiskCheckCode.RISK_POLICY,
                goal.HasValidContentDigest()
                    && riskPolicy.IsBoundTo(goal.RiskPolicyVersion)
                    && riskPolicy.RetryLimit == 0
                    && preflight?.Checks.Any(
                        check => check.Code == ExecutablePreflightCheckCode.POLICY_BINDINGS
                            && check.Passed) == true,
                "The immutable risk policy binding must remain available."),
            Check(
                RiskCheckCode.EXECUTION_SCHEDULE,
                goal.ExecutionSchedule.Allows(at),
                "Current time must be inside the execution schedule."),
            Check(RiskCheckCode.KILL_SWITCH, observation.KillSwitchClear, "The kill switch must be clear."),
            Check(
                RiskCheckCode.RECONCILIATION,
                observation.ReconciliationCurrent,
                "Account reconciliation must be current."),
            Check(
                RiskCheckCode.ACCOUNT_BINDING,
                exactBroker
                    && exactAllocation
                    && session.ObservedEnvironment == BrokerEnvironment.PAPER
                    && string.Equals(session.ObservedEndpointIdentity, binding.EndpointIdentity, StringComparison.Ordinal)
                    && string.Equals(
                        session.ObservedAccountIdentifier,
                        binding.ExpectedAccountIdentifier,
                        StringComparison.Ordinal),
                "Session and allocation account bindings must remain exact."),
            Check(
                RiskCheckCode.INSTRUMENT,
                goal.InstrumentUniverse.Contains(decision.InstrumentIdentifier!, StringComparer.Ordinal)
                    && riskPolicy.AllowedInstruments.Contains(decision.InstrumentIdentifier!)
                    && projectedInstrumentQuantity >= 0m,
                "The instrument and long-only position must remain permitted."),
            Check(
                RiskCheckCode.ORDER_TYPE,
                binding.RequiredCapabilities.Contains("EQUITY_LIMIT_DAY", StringComparer.Ordinal),
                "The bound Broker account must permit limit day orders."),
            Check(
                RiskCheckCode.PRICE_PROTECTION,
                quantity > 0m && decimal.Truncate(quantity) == quantity
                    && limitPrice > 0m && requestedExposure > 0m
                    && requestedExposure <= riskPolicy.MaximumOrderExposure,
                "Maximum order exposure must be bounded."),
            Check(
                RiskCheckCode.CAPITAL_ALLOCATION,
                observation.CapitalDataCurrent
                    && currencyMatches
                    && totalExposure <= goal.CapitalAllocation.Amount
                    && policyLimitsPass,
                "Worst-case exposure must remain inside Goal allocation."),
            Check(
                RiskCheckCode.BUYING_POWER,
                currencyMatches
                    && requestedExposure <= observation.BuyingPower
                    && observation.BuyingPower - requestedExposure
                        >= riskPolicy.MinimumBuyingPowerReserve,
                "Buying power must cover maximum order exposure."),
            Check(
                RiskCheckCode.DATA_FRESHNESS,
                observation.CapitalDataCurrent && observation.ObservedAt <= at
                    && at - observation.ObservedAt <= riskPolicy.MaximumMarketDataAge,
                "Risk input data must be current and observed before evaluation.")
        ];
    }

    private static RiskCheck Check(RiskCheckCode code, bool passed, string detail) => new(
        code,
        passed,
        detail);

    private static PaperReviewIntentException Failure(string message) => new(message);
}
