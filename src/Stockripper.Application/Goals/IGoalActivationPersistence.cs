using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Application.Goals;

public interface IGoalActivationPersistence
{
    ValueTask InsertAsync(
        GoalId goalId,
        GoalActivation activation,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<GoalActivation?> FindAsync(
        ActivationId activationId,
        CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        GoalActivation activation,
        GoalActivationState expectedState,
        int expectedRevision,
        OperatorId actorId,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken);
}

public sealed class GoalActivationConflictException : InvalidOperationException
{
    public GoalActivationConflictException()
        : base("The Goal already has an active or paused activation.")
    {
    }
}

public sealed class GoalActivationNotFoundException : InvalidOperationException
{
    public GoalActivationNotFoundException()
        : base("The Goal activation does not exist.")
    {
    }
}

public sealed class GoalActivationConcurrencyException : InvalidOperationException
{
    public GoalActivationConcurrencyException()
        : base("The Goal activation changed before this operation completed.")
    {
    }
}
