using Stockripper.Application.Goals;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Risk;

namespace Stockripper.Application.Execution;

public sealed record KillSwitchScopeReference(
    KillSwitchScopeType ScopeType,
    string ScopeIdentifier);

public static class PaperExecutionSafetyScopes
{
    public static IReadOnlyList<KillSwitchScopeReference> Required(
        GoalVersionId goalVersionId,
        IntentAccountBinding binding) =>
    [
        new(KillSwitchScopeType.SYSTEM, BrokerEnvironment.PAPER.ToString()),
        new(KillSwitchScopeType.CONNECTION, binding.BrokerConnectionVersion.Digest),
        new(KillSwitchScopeType.ACCOUNT, binding.AccountIdentifier),
        new(KillSwitchScopeType.GOAL, goalVersionId.Value.ToString("D"))
    ];
}

public interface IExecutionSafetyPersistence
{
    ValueTask ConfigureKillSwitchesAsync(
        IReadOnlyList<KillSwitch> killSwitches,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<KillSwitch>> FindKillSwitchesAsync(
        IReadOnlyList<KillSwitchScopeReference> scopes,
        CancellationToken cancellationToken);

    ValueTask<KillSwitch?> FindKillSwitchAsync(
        KillSwitchId killSwitchId,
        CancellationToken cancellationToken);

    ValueTask UpdateKillSwitchAsync(
        KillSwitch killSwitch,
        KillSwitchState expectedState,
        int expectedRevision,
        OperatorId actorId,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken);

    ValueTask InsertReconciliationAsync(
        ReconciliationRecord reconciliation,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask RecordReconciliationStartedAsync(
        ReconciliationId reconciliationId,
        GoalVersionId goalVersionId,
        IntentAccountBinding accountBinding,
        OperatorId actorId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask<ReconciliationRecord?> FindReconciliationAsync(
        ReconciliationId reconciliationId,
        CancellationToken cancellationToken);

    ValueTask<ReconciliationRecord?> FindLatestCurrentReconciliationAsync(
        GoalVersionId goalVersionId,
        IntentAccountBinding accountBinding,
        CancellationToken cancellationToken);

    ValueTask<ExposureReservation?> FindReservationAsync(
        ExecutionIntentId intentId,
        CancellationToken cancellationToken);

    ValueTask ReleaseReservationAsync(
        ExposureReservation reservation,
        int expectedRevision,
        OperatorId actorId,
        CancellationToken cancellationToken);
}

public sealed class ExecutionSafetyService(
    IGoalPersistence goalPersistence,
    IExecutionSafetyPersistence safetyPersistence,
    TimeProvider timeProvider,
    ITriggeredCancellationCoordinator? cancellationCoordinator = null,
    IPaperReconciliationRecovery? reconciliationRecovery = null,
    IPaperSubmissionInterlock? submissionInterlock = null)
{
    public async ValueTask RequirePaperExecutionClearAsync(
        GoalVersionId goalVersionId,
        CancellationToken cancellationToken = default)
    {
        var goal = await goalPersistence.FindGoalVersionAsync(
            goalVersionId, cancellationToken).ConfigureAwait(false)
            ?? throw new ExecutionSafetyException("The Goal version does not exist.");
        if (goal.Mode is not (ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO)
            || goal.ExecutionBinding is null
            || !goal.HasValidContentDigest())
        {
            throw new ExecutionSafetyException(
                "The paper Goal authority is invalid.");
        }

        await RequireAllClearAsync(
            safetyPersistence, goal.Id, ToAccountBinding(goal), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<KillSwitch>> ConfigurePaperScopesAsync(
        GoalVersionId goalVersionId,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var goal = await goalPersistence.FindGoalVersionAsync(goalVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ExecutionSafetyException("The Goal version does not exist.");
        if (goal.Mode is not (ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO)
            || goal.ExecutionBinding is null)
        {
            throw new ExecutionSafetyException(
                "Kill-switch setup accepts only a paper Goal version.");
        }

        var binding = ToAccountBinding(goal);
        var configuredAt = timeProvider.GetUtcNow();
        var switches = PaperExecutionSafetyScopes.Required(goal.Id, binding)
            .Select(scope => KillSwitch.Configure(
                KillSwitchId.New(),
                scope.ScopeType,
                scope.ScopeIdentifier,
                configuredAt))
            .ToArray();
        await safetyPersistence.ConfigureKillSwitchesAsync(switches, actorId, cancellationToken)
            .ConfigureAwait(false);
        return await safetyPersistence.FindKillSwitchesAsync(
            PaperExecutionSafetyScopes.Required(goal.Id, binding),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<KillSwitch> TriggerAsync(
        KillSwitchId killSwitchId,
        OperatorId actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        KillSwitch triggered;
        await using (submissionInterlock is null
            ? null
            : await submissionInterlock.EnterAsync(cancellationToken)
                .ConfigureAwait(false))
        {
            var current = await safetyPersistence.FindKillSwitchAsync(
                killSwitchId, cancellationToken).ConfigureAwait(false)
                ?? throw new ExecutionSafetyException("The kill switch does not exist.");
            var changedAt = timeProvider.GetUtcNow();
            triggered = current.Trigger(changedAt, reason);
            await safetyPersistence.UpdateKillSwitchAsync(
                triggered,
                current.State,
                current.Revision,
                actorId,
                changedAt,
                cancellationToken).ConfigureAwait(false);
        }
        if (cancellationCoordinator is not null)
        {
            _ = await cancellationCoordinator.CancelForTriggeredScopeAsync(
                triggered, actorId, reason, cancellationToken).ConfigureAwait(false);
        }

        if (reconciliationRecovery is not null)
        {
            _ = await reconciliationRecovery.RecoverAsync(actorId, cancellationToken)
                .ConfigureAwait(false);
        }

        return triggered;
    }

    public async ValueTask<KillSwitch> RequireRecoveryAsync(
        KillSwitchId killSwitchId,
        ReconciliationId reconciliationId,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var current = await safetyPersistence.FindKillSwitchAsync(killSwitchId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ExecutionSafetyException("The kill switch does not exist.");
        var reconciliation = await safetyPersistence.FindReconciliationAsync(
            reconciliationId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new ExecutionSafetyException("The reconciliation record does not exist.");
        if (!ScopeMatches(current, reconciliation))
        {
            throw new ExecutionSafetyException(
                "The reconciliation record does not match the kill-switch scope.");
        }

        var changedAt = timeProvider.GetUtcNow();
        var recovery = current.RequireRecovery(reconciliation, changedAt);
        await safetyPersistence.UpdateKillSwitchAsync(
            recovery,
            current.State,
            current.Revision,
            actorId,
            changedAt,
            cancellationToken).ConfigureAwait(false);
        return recovery;
    }

    public async ValueTask<KillSwitch> ClearAsync(
        KillSwitchId killSwitchId,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var current = await safetyPersistence.FindKillSwitchAsync(killSwitchId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ExecutionSafetyException("The kill switch does not exist.");
        var changedAt = timeProvider.GetUtcNow();
        var cleared = current.Clear(changedAt);
        await safetyPersistence.UpdateKillSwitchAsync(
            cleared,
            current.State,
            current.Revision,
            actorId,
            changedAt,
            cancellationToken).ConfigureAwait(false);
        return cleared;
    }

    public static IntentAccountBinding ToAccountBinding(GoalVersion goal)
    {
        var binding = goal.ExecutionBinding
            ?? throw new ExecutionSafetyException("The Goal version has no Execution binding.");
        return new IntentAccountBinding(
            binding.BrokerConnectionVersion,
            binding.ExpectedEnvironment,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier);
    }

    internal static async ValueTask RequireAllClearAsync(
        IExecutionSafetyPersistence persistence,
        GoalVersionId goalVersionId,
        IntentAccountBinding binding,
        CancellationToken cancellationToken)
    {
        var required = PaperExecutionSafetyScopes.Required(goalVersionId, binding);
        IReadOnlyList<KillSwitch> switches;
        try
        {
            switches = await persistence.FindKillSwitchesAsync(required, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ExecutionSafetyException("Durable kill-switch state is unavailable.");
        }

        if (switches.Count != required.Count
            || switches.Any(killSwitch => killSwitch.State != KillSwitchState.CLEAR))
        {
            throw new ExecutionSafetyException("The applicable durable kill switch blocks execution.");
        }
    }

    internal static bool IsCurrentReconciliation(
        ReconciliationRecord reconciliation,
        GoalVersion goal,
        IntentAccountBinding expectedBinding,
        PaperRiskPolicy riskPolicy,
        DateTimeOffset at) =>
        reconciliation.Result == ReconciliationResult.CURRENT
        && reconciliation.HasValidContentDigest()
        && reconciliation.GoalVersionId == goal.Id
        && reconciliation.AccountBinding == expectedBinding
        && reconciliation.ReconciliationPolicyVersion
            == goal.ExecutionBinding!.ReconciliationPolicyVersion
        && riskPolicy.IsBoundTo(goal.RiskPolicyVersion)
        && reconciliation.CompletedAt <= at
        && reconciliation.ObservedAt <= at
        && at - reconciliation.ObservedAt <= riskPolicy.MaximumMarketDataAge;

    private static bool ScopeMatches(KillSwitch killSwitch, ReconciliationRecord reconciliation) =>
        killSwitch.ScopeType switch
        {
            KillSwitchScopeType.SYSTEM => string.Equals(
                killSwitch.ScopeIdentifier,
                reconciliation.AccountBinding.Environment.ToString(),
                StringComparison.Ordinal),
            KillSwitchScopeType.CONNECTION => string.Equals(
                killSwitch.ScopeIdentifier,
                reconciliation.AccountBinding.BrokerConnectionVersion.Digest,
                StringComparison.Ordinal),
            KillSwitchScopeType.ACCOUNT => string.Equals(
                killSwitch.ScopeIdentifier,
                reconciliation.AccountBinding.AccountIdentifier,
                StringComparison.Ordinal),
            KillSwitchScopeType.GOAL => string.Equals(
                killSwitch.ScopeIdentifier,
                reconciliation.GoalVersionId.Value.ToString("D"),
                StringComparison.Ordinal),
            _ => false
        };

}

public sealed class ExecutionSafetyException(string message) : InvalidOperationException(message);
