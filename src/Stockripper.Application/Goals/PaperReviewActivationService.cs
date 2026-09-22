using Stockripper.Application.Allocations;
using Stockripper.Application.Brokers;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;

namespace Stockripper.Application.Goals;

public sealed record PaperReviewAuthority(
    GoalActivation Activation,
    AccountAllocationEntry Allocation,
    BrokerConnectionObservation BrokerObservation);

public sealed class PaperReviewActivationService(
    IGoalPersistence goalPersistence,
    BrokerPreflightService brokerPreflightService,
    IAccountAllocationPersistence allocationPersistence,
    TimeProvider timeProvider)
{
    private const string ExposureMethod = "LONG_LIMIT_WHOLE_SHARE_MAX_NOTIONAL";

    public ValueTask ConfigureAccountLimitAsync(
        AccountAllocationLimit accountLimit,
        OperatorId actorId,
        CancellationToken cancellationToken = default) => allocationPersistence.InsertLimitAsync(
        accountLimit,
        actorId,
        cancellationToken);

    public async ValueTask<PaperReviewAuthority> ActivateAsync(
        GoalVersionId goalVersionId,
        VersionReference accountLimitVersion,
        OperatorId operatorId,
        string activationEvidence,
        DateTimeOffset? expiryTime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountLimitVersion);
        var goalVersion = await goalPersistence
            .FindGoalVersionAsync(goalVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new GoalActivationNotFoundException();
        if (goalVersion.Mode is not (ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO))
        {
            throw new InvalidOperationException(
                "The paper activation boundary accepts only a paper mode.");
        }

        if (!goalVersion.HasValidContentDigest() || goalVersion.ExecutionBinding is null)
        {
            throw new InvalidDataException("The executable Goal version binding is invalid.");
        }

        var limit = await allocationPersistence
            .FindLimitAsync(accountLimitVersion, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new AccountAllocationConflictException(AccountAllocationFailure.LimitNotFound);
        if (!limit.HasValidContentDigest() || !HasExactLimitBinding(goalVersion, limit))
        {
            throw new AccountAllocationConflictException(AccountAllocationFailure.LimitBindingMismatch);
        }

        if (goalVersion.CapitalAllocation.Amount > limit.MaximumAllocation.Amount)
        {
            throw new AccountAllocationConflictException(AccountAllocationFailure.AccountLimitExceeded);
        }

        var observation = await brokerPreflightService
            .VerifyAsync(goalVersion, cancellationToken)
            .ConfigureAwait(false);
        var activationTime = timeProvider.GetUtcNow();
        var activation = GoalActivation.Activate(
            ActivationId.New(),
            goalVersion.Id,
            operatorId,
            activationEvidence,
            GoalActivationService.CreateAuthoritySummary(goalVersion, expiryTime),
            activationTime,
            expiryTime);
        var allocation = AccountAllocationEntry.Reserve(
            AccountAllocationEntryId.New(),
            activation.Id,
            goalVersion,
            limit,
            observation.Environment,
            ExposureMethod,
            activationTime);
        await allocationPersistence.ReserveAndActivateAsync(
            goalVersion.GoalId,
            activation,
            allocation,
            observation,
            operatorId,
            cancellationToken).ConfigureAwait(false);
        return new PaperReviewAuthority(activation, allocation, observation);
    }

    private static bool HasExactLimitBinding(
        GoalVersion goalVersion,
        AccountAllocationLimit limit)
    {
        var binding = goalVersion.ExecutionBinding!;
        return limit.Environment == BrokerEnvironment.PAPER
            && binding.ExpectedEnvironment == BrokerEnvironment.PAPER
            && binding.BrokerConnectionVersion == limit.BrokerConnectionVersion
            && string.Equals(binding.EndpointIdentity, limit.EndpointIdentity, StringComparison.Ordinal)
            && string.Equals(
                binding.ExpectedAccountIdentifier,
                limit.AccountIdentifier,
                StringComparison.Ordinal)
            && string.Equals(
                goalVersion.CapitalAllocation.Currency,
                limit.MaximumAllocation.Currency,
                StringComparison.Ordinal);
    }
}
