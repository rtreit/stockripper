using Stockripper.Application.Brokers;
using Stockripper.Domain.Common;
using Stockripper.Domain.Goals;

namespace Stockripper.Application.Allocations;

public interface IAccountAllocationPersistence
{
    ValueTask InsertLimitAsync(
        AccountAllocationLimit accountLimit,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<AccountAllocationLimit?> FindLimitAsync(
        VersionReference limitVersion,
        CancellationToken cancellationToken);

    ValueTask ReserveAndActivateAsync(
        GoalId goalId,
        GoalActivation activation,
        AccountAllocationEntry allocation,
        BrokerConnectionObservation observation,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<AccountAllocationEntry?> FindByActivationAsync(
        ActivationId activationId,
        CancellationToken cancellationToken);
}

public enum AccountAllocationFailure
{
    LimitNotFound,
    LimitBindingMismatch,
    AccountLimitExceeded,
    ConcurrentAuthorityConflict
}

public sealed class AccountAllocationConflictException(AccountAllocationFailure failure)
    : InvalidOperationException("The paper account allocation failed.")
{
    public AccountAllocationFailure Failure { get; } = failure;
}
