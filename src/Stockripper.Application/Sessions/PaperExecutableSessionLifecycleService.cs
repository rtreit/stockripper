using Stockripper.Application.Execution;
using Stockripper.Application.Goals;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Sessions;

public sealed record PaperExecutableSessionCompletion(
    Session Session,
    ReconciliationRecord Reconciliation);

public sealed class PaperExecutableSessionLifecycleService(
    ISessionPersistence sessionPersistence,
    IGoalPersistence goalPersistence,
    IGoalActivationPersistence activationPersistence,
    IExecutionSafetyPersistence safetyPersistence,
    IPaperReconciliationRunner reconciliationRunner,
    TimeProvider timeProvider)
{
    public async ValueTask<Session> StartAsync(
        SessionId sessionId,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var session = await sessionPersistence.FindAsync(sessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure("The Session does not exist.");
        var goal = await goalPersistence.FindGoalVersionAsync(
            session.GoalVersionId, cancellationToken).ConfigureAwait(false)
            ?? throw Failure("The Goal version does not exist.");
        var activation = await activationPersistence.FindAsync(
            session.ActivationId, cancellationToken).ConfigureAwait(false)
            ?? throw Failure("The Goal activation does not exist.");
        var now = timeProvider.GetUtcNow();
        var binding = ExecutionSafetyService.ToAccountBinding(goal);
        if (goal.Mode is not (ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO)
            || session.Mode != goal.Mode
            || session.State != SessionState.READY
            || session.GoalVersionId != goal.Id
            || activation.GoalVersionId != goal.Id
            || !activation.CanStartSession(now)
            || !goal.ExecutionSchedule.Allows(now)
            || session.ObservedEnvironment != BrokerEnvironment.PAPER
            || !string.Equals(session.ObservedEndpointIdentity,
                binding.EndpointIdentity, StringComparison.Ordinal)
            || !string.Equals(session.ObservedAccountIdentifier,
                binding.AccountIdentifier, StringComparison.Ordinal))
        {
            throw Failure("Current paper Session authority does not permit execution.");
        }

        await RequireCurrentSafetyAsync(goal.Id, binding, null, cancellationToken)
            .ConfigureAwait(false);
        var running = session.TransitionTo(SessionState.RUNNING, now);
        await sessionPersistence.UpdateAsync(running, session.State, session.Revision,
            actorId, now, cancellationToken).ConfigureAwait(false);
        return running;
    }

    public async ValueTask<PaperExecutableSessionCompletion> CompleteAsync(
        SessionId sessionId,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var running = await sessionPersistence.FindAsync(sessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure("The Session does not exist.");
        if (running.Mode is not (ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO)
            || running.State != SessionState.RUNNING)
        {
            throw Failure("Only a running paper Session can complete.");
        }

        var changedAt = timeProvider.GetUtcNow();
        var stopping = running.TransitionTo(SessionState.STOPPING, changedAt);
        await sessionPersistence.UpdateAsync(stopping, running.State, running.Revision,
            actorId, changedAt, cancellationToken).ConfigureAwait(false);
        var reconcilingAt = timeProvider.GetUtcNow();
        var reconciling = stopping.TransitionTo(
            SessionState.RECONCILING, reconcilingAt);
        await sessionPersistence.UpdateAsync(reconciling, stopping.State,
            stopping.Revision, actorId, reconcilingAt, cancellationToken)
            .ConfigureAwait(false);

        var reconciliation = await reconciliationRunner.ReconcileAsync(
            running.GoalVersionId, actorId, cancellationToken).ConfigureAwait(false);
        if (reconciliation.Result != ReconciliationResult.CURRENT)
        {
            return new(await EnsureLockedAsync(reconciling, actorId, cancellationToken)
                .ConfigureAwait(false), reconciliation);
        }

        var goal = await goalPersistence.FindGoalVersionAsync(
            running.GoalVersionId, cancellationToken).ConfigureAwait(false)
            ?? throw Failure("The Goal version does not exist.");
        var binding = ExecutionSafetyService.ToAccountBinding(goal);
        await RequireCurrentSafetyAsync(
            goal.Id, binding, reconciliation.Id, cancellationToken).ConfigureAwait(false);
        var current = await sessionPersistence.FindAsync(sessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure("The Session does not exist.");
        if (current.State == SessionState.LOCKED)
        {
            return new(current, reconciliation);
        }

        if (current.State != SessionState.RECONCILING)
        {
            throw Failure("The Session changed during final reconciliation.");
        }

        var completedAt = timeProvider.GetUtcNow();
        var completed = current.TransitionTo(SessionState.COMPLETED, completedAt);
        await sessionPersistence.UpdateAsync(completed, current.State, current.Revision,
            actorId, completedAt, cancellationToken).ConfigureAwait(false);
        return new(completed, reconciliation);
    }

    private async ValueTask RequireCurrentSafetyAsync(
        GoalVersionId goalVersionId,
        IntentAccountBinding binding,
        ReconciliationId? expectedReconciliationId,
        CancellationToken cancellationToken)
    {
        await ExecutionSafetyService.RequireAllClearAsync(
            safetyPersistence, goalVersionId, binding, cancellationToken)
            .ConfigureAwait(false);
        var current = await safetyPersistence.FindLatestCurrentReconciliationAsync(
            goalVersionId, binding, cancellationToken).ConfigureAwait(false);
        if (current is null
            || expectedReconciliationId is not null
            && current.Id != expectedReconciliationId.Value)
        {
            throw Failure("Current reconciliation evidence does not permit execution.");
        }
    }

    private async ValueTask<Session> EnsureLockedAsync(
        Session reconciling,
        OperatorId actorId,
        CancellationToken cancellationToken)
    {
        var current = await sessionPersistence.FindAsync(reconciling.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure("The Session does not exist.");
        if (current.State == SessionState.LOCKED)
        {
            return current;
        }

        if (current.State != SessionState.RECONCILING)
        {
            throw Failure("The Session changed during final reconciliation.");
        }

        var changedAt = timeProvider.GetUtcNow();
        var locked = current.TransitionTo(SessionState.LOCKED, changedAt);
        await sessionPersistence.UpdateAsync(locked, current.State, current.Revision,
            actorId, changedAt, cancellationToken).ConfigureAwait(false);
        return locked;
    }

    private static PaperExecutableSessionLifecycleException Failure(string message) =>
        new(message);
}

public sealed class PaperExecutableSessionLifecycleException(string message)
    : InvalidOperationException(message);
