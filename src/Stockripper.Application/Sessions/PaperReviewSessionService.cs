using Stockripper.Application.Allocations;
using Stockripper.Application.Data;
using Stockripper.Application.Execution;
using Stockripper.Application.Goals;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Sessions;

public sealed class PaperReviewSessionService(
    IGoalPersistence goalPersistence,
    IGoalActivationPersistence activationPersistence,
    IDataSnapshotPersistence snapshotPersistence,
    IAccountAllocationPersistence allocationPersistence,
    ISessionPersistence sessionPersistence,
    IExecutablePreflightPersistence preflightPersistence,
    IExecutablePreflightProbe preflightProbe,
    IExecutionSafetyPersistence safetyPersistence,
    IPaperReconciliationRunner reconciliationRunner,
    TimeProvider timeProvider)
{
    public async ValueTask<PaperReviewSessionOutcome> StartAsync(
        GoalVersionId goalVersionId,
        ActivationId activationId,
        DataSnapshotId snapshotId,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var goal = await goalPersistence.FindGoalVersionAsync(goalVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PaperReviewSessionPreflightException("The Goal version does not exist.");
        if (goal.Mode is not (ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO)
            || goal.ExecutionBinding is null)
        {
            throw new PaperReviewSessionPreflightException(
                "The paper Session boundary accepts only a paper mode.");
        }

        var accountBinding = ExecutionSafetyService.ToAccountBinding(goal);
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
            throw new PaperReviewSessionPreflightException(exception.Message);
        }

        var activation = await activationPersistence.FindAsync(activationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PaperReviewSessionPreflightException("The Goal activation does not exist.");
        var startedAt = timeProvider.GetUtcNow();
        if (activation.GoalVersionId != goal.Id || !activation.CanStartSession(startedAt))
        {
            throw new PaperReviewSessionPreflightException(
                "The Goal activation cannot start a paper review Session.");
        }

        var snapshot = await snapshotPersistence.FindAsync(snapshotId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PaperReviewSessionPreflightException("The data snapshot does not exist.");
        var session = Session.Create(
            SessionId.New(),
            goal,
            activation,
            startedAt,
            snapshot.Id);
        await sessionPersistence.InsertAsync(session, actorId, cancellationToken).ConfigureAwait(false);
        var preflightSession = session.TransitionTo(SessionState.PREFLIGHT, startedAt);
        await sessionPersistence.UpdateAsync(
            preflightSession,
            session.State,
            session.Revision,
            actorId,
            startedAt,
            cancellationToken).ConfigureAwait(false);

        ExecutablePreflightObservation? observation = null;
        try
        {
            observation = await preflightProbe.ObserveAsync(
                goal.ExecutionBinding.BrokerConnectionVersion,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            observation = null;
        }

        ReconciliationRecord? reconciliation = null;
        if (observation is not null)
        {
            try
            {
                reconciliation = await reconciliationRunner.ReconcileAsync(
                    goal.Id, actorId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                reconciliation = null;
            }
        }

        var completedAt = timeProvider.GetUtcNow();

        var durableKillSwitchClear = true;
        try
        {
            await ExecutionSafetyService.RequireAllClearAsync(
                safetyPersistence,
                goal.Id,
                accountBinding,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionSafetyException)
        {
            durableKillSwitchClear = false;
        }

        var currentActivation = await activationPersistence.FindAsync(activationId, cancellationToken)
            .ConfigureAwait(false);
        var allocation = await allocationPersistence.FindByActivationAsync(
            activationId,
            cancellationToken).ConfigureAwait(false);
        var checks = Evaluate(
            goal,
            currentActivation,
            snapshot,
            allocation,
            observation,
            durableKillSwitchClear,
            IsCurrentReconciliation(
                goal, accountBinding, observation, reconciliation, completedAt),
            completedAt);
        var evidence = ExecutableSessionPreflight.Create(
            SessionPreflightId.New(),
            preflightSession,
            goal.ExecutionBinding.BrokerConnectionVersion,
            reconciliation?.Id,
            observation?.Broker.Environment,
            observation?.Broker.EndpointIdentity,
            observation?.Broker.AccountIdentifier,
            observation?.Broker.Capabilities ?? [],
            observation?.Broker.ObservedAt,
            observation?.BrokerTime,
            observation?.CapitalObservedAt,
            observation?.ReconciliationObservedAt,
            observation?.Currency ?? goal.CapitalAllocation.Currency,
            observation?.PositionExposure,
            observation?.OpenOrderExposure,
            observation?.BuyingPower,
            checks,
            completedAt);
        var completedSession = evidence.Result == ExecutablePreflightResult.PASSED
            ? preflightSession.CompleteExecutablePreflight(
                observation!.Broker.Environment,
                observation.Broker.EndpointIdentity,
                observation.Broker.AccountIdentifier,
                completedAt)
            : preflightSession.FailExecutablePreflight(
                observation?.Broker.Environment,
                observation?.Broker.EndpointIdentity,
                observation?.Broker.AccountIdentifier,
                completedAt,
                "Executable Session preflight failed.");
        await preflightPersistence.CompleteAsync(
            preflightSession,
            completedSession,
            evidence,
            actorId,
            cancellationToken).ConfigureAwait(false);
        return new PaperReviewSessionOutcome(completedSession, evidence);
    }

    private static IReadOnlyList<ExecutablePreflightCheck> Evaluate(
        GoalVersion goal,
        GoalActivation? activation,
        Domain.Data.DataSnapshot snapshot,
        AccountAllocationEntry? allocation,
        ExecutablePreflightObservation? observation,
        bool durableKillSwitchClear,
        bool durableReconciliationCurrent,
        DateTimeOffset at)
    {
        var binding = goal.ExecutionBinding!;
        var policiesAvailable = observation is not null
            && observation.AvailablePolicies.Contains(goal.DataPolicyVersion)
            && observation.AvailablePolicies.Contains(goal.RiskPolicyVersion)
            && observation.AvailablePolicies.Contains(binding.OrderPolicyVersion)
            && observation.AvailablePolicies.Contains(binding.ReconciliationPolicyVersion);
        var allocationValid = allocation is not null
            && allocation.State == AccountAllocationState.ACTIVE
            && allocation.ActivationId == activation?.Id
            && allocation.GoalVersionId == goal.Id
            && allocation.BrokerConnectionVersion == binding.BrokerConnectionVersion
            && allocation.Environment == BrokerEnvironment.PAPER
            && string.Equals(allocation.EndpointIdentity, binding.EndpointIdentity, StringComparison.Ordinal)
            && string.Equals(
                allocation.AccountIdentifier,
                binding.ExpectedAccountIdentifier,
                StringComparison.Ordinal)
            && allocation.Allocation == goal.CapitalAllocation;
        var currencyMatches = observation is not null
            && string.Equals(
                observation.Currency,
                goal.CapitalAllocation.Currency,
                StringComparison.Ordinal);
        var exposureWithinAllocation = observation is not null
            && observation.PositionExposure + observation.OpenOrderExposure
                <= goal.CapitalAllocation.Amount;

        return
        [
            Check(
                ExecutablePreflightCheckCode.ACTIVATION_ACTIVE,
                activation?.GoalVersionId == goal.Id && activation.CanStartSession(at),
                "Goal activation must remain active and bound."),
            Check(
                ExecutablePreflightCheckCode.GOAL_DIGEST,
                goal.HasValidContentDigest(),
                "Goal version content must retain its published digest."),
            Check(
                ExecutablePreflightCheckCode.EXECUTION_SCHEDULE,
                goal.ExecutionSchedule.Allows(at),
                "Current time must be inside the execution schedule."),
            Check(
                ExecutablePreflightCheckCode.DATA_SNAPSHOT,
                snapshot.HasValidContentDigest()
                    && snapshot.DataPolicyVersion == goal.DataPolicyVersion,
                "Data snapshot digest and policy must match the Goal version."),
            Check(
                ExecutablePreflightCheckCode.DATA_SOURCES,
                observation?.DataSourcesAvailable == true,
                "Required data sources must be available."),
            Check(
                ExecutablePreflightCheckCode.POLICY_BINDINGS,
                policiesAvailable,
                "All bound policy versions must be available."),
            Check(
                ExecutablePreflightCheckCode.KILL_SWITCH,
                durableKillSwitchClear && observation?.KillSwitchClear == true,
                "The broadest applicable kill switch must be clear."),
            Check(
                ExecutablePreflightCheckCode.BROKER_ENVIRONMENT,
                observation?.Broker.Environment == BrokerEnvironment.PAPER,
                "Observed Broker environment must be PAPER."),
            Check(
                ExecutablePreflightCheckCode.ENDPOINT_IDENTITY,
                string.Equals(
                    observation?.Broker.EndpointIdentity,
                    binding.EndpointIdentity,
                    StringComparison.Ordinal),
                "Observed Broker endpoint must match the Goal binding."),
            Check(
                ExecutablePreflightCheckCode.ACCOUNT_IDENTITY,
                string.Equals(
                    observation?.Broker.AccountIdentifier,
                    binding.ExpectedAccountIdentifier,
                    StringComparison.Ordinal),
                "Observed Broker account must match the Goal binding."),
            Check(
                ExecutablePreflightCheckCode.ACCOUNT_CAPABILITIES,
                observation is not null
                    && binding.RequiredCapabilities.All(
                        capability => observation.Broker.Capabilities.Contains(capability)),
                "Observed Broker capabilities must satisfy the Goal binding."),
            Check(
                ExecutablePreflightCheckCode.BROKER_CLOCK,
                observation?.BrokerClockUsable == true,
                "Broker clock data must be usable."),
            Check(
                ExecutablePreflightCheckCode.MARKET_SESSION,
                observation?.MarketSessionOpen == true,
                "The regular market session must be open."),
            Check(
                ExecutablePreflightCheckCode.RECONCILIATION,
                durableReconciliationCurrent && observation?.ReconciliationCurrent == true,
                "Account reconciliation must be current."),
            Check(
                ExecutablePreflightCheckCode.CAPITAL_DATA,
                observation?.CapitalDataCurrent == true
                    && currencyMatches
                    && exposureWithinAllocation,
                "Capital data must be current and inside Goal allocation."),
            Check(
                ExecutablePreflightCheckCode.BUYING_POWER,
                observation?.BuyingPowerDataCurrent == true
                    && currencyMatches,
                "Buying power data must be current and use the Goal currency."),
            Check(
                ExecutablePreflightCheckCode.ACCOUNT_ALLOCATION,
                allocationValid,
                "Durable account allocation must remain active and exact.")
        ];
    }

    private static ExecutablePreflightCheck Check(
        ExecutablePreflightCheckCode code,
        bool passed,
        string detail) => new(code, passed, detail);

    private static bool IsCurrentReconciliation(
        GoalVersion goal,
        IntentAccountBinding binding,
        ExecutablePreflightObservation? observation,
        ReconciliationRecord? reconciliation,
        DateTimeOffset at) =>
        observation is not null
        && reconciliation is not null
        && reconciliation.Result == ReconciliationResult.CURRENT
        && reconciliation.HasValidContentDigest()
        && reconciliation.GoalVersionId == goal.Id
        && reconciliation.AccountBinding == binding
        && reconciliation.ReconciliationPolicyVersion
            == goal.ExecutionBinding!.ReconciliationPolicyVersion
        && reconciliation.CompletedAt <= at
        && reconciliation.ObservedAt <= at
        && reconciliation.PositionExposure == observation.PositionExposure
        && reconciliation.OpenOrderExposure == observation.OpenOrderExposure
        && reconciliation.BuyingPower == observation.BuyingPower
        && string.Equals(
            reconciliation.Currency, observation.Currency, StringComparison.Ordinal);
}
