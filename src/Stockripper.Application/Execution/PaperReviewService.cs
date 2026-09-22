using Stockripper.Application.Allocations;
using Stockripper.Application.Goals;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Risk;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Execution;

public sealed class PaperReviewService(
    IPaperReviewIntentPersistence intentPersistence,
    ISessionPersistence sessionPersistence,
    IGoalPersistence goalPersistence,
    IGoalActivationPersistence activationPersistence,
    IAccountAllocationPersistence allocationPersistence,
    IExecutablePreflightPersistence preflightPersistence,
    IIntentRiskProbe riskProbe,
    IExecutionSafetyPersistence safetyPersistence,
    IPaperReconciliationRunner reconciliationRunner,
    IPaperRiskPolicyProvider riskPolicyProvider,
    TimeProvider timeProvider,
    TimeSpan approvalLifetime)
{
    public async ValueTask<ReviewApproval> ReviewAsync(
        ExecutionIntentId intentId,
        string expectedIntentDigest,
        ReviewResult result,
        OperatorId actorId,
        string? operatorNote = null,
        CancellationToken cancellationToken = default)
    {
        if (approvalLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The review approval lifetime must be configured.");
        }

        var intent = await intentPersistence.FindIntentAsync(intentId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Failure("The Execution intent does not exist.");
        var goal = await goalPersistence.FindGoalVersionAsync(
            intent.GoalVersionId, cancellationToken).ConfigureAwait(false)
            ?? throw Failure("The Goal version does not exist.");
        var riskPolicy = await riskPolicyProvider.FindAsync(
            goal.RiskPolicyVersion, cancellationToken).ConfigureAwait(false);
        if (riskPolicy is null
            || !riskPolicy.IsBoundTo(goal.RiskPolicyVersion)
            || approvalLifetime != riskPolicy.ReviewApprovalLifetime)
        {
            throw Failure("The exact review expiry policy is unavailable.");
        }
        if (intent.State != ExecutionIntentState.REVIEW_PENDING
            || !string.Equals(intent.ContentDigest, expectedIntentDigest, StringComparison.Ordinal))
        {
            throw Failure("The review does not match the current Execution intent.");
        }

        var now = timeProvider.GetUtcNow();
        if (now >= intent.ExpiresAt)
        {
            throw Failure("The Execution intent has expired.");
        }

        AccountAllocationEntry? approvalAllocation = null;
        ReconciliationRecord? reconciliation = null;
        if (result == ReviewResult.APPROVED)
        {
            (approvalAllocation, reconciliation) = await ValidateApprovalAuthorityAsync(
                intent,
                actorId,
                now,
                riskPolicy,
                cancellationToken).ConfigureAwait(false);
        }

        var approvalExpiry = now + approvalLifetime;
        if (approvalExpiry > intent.ExpiresAt)
        {
            approvalExpiry = intent.ExpiresAt;
        }

        var review = ReviewApproval.Create(
            ReviewApprovalId.New(),
            intent.Id,
            actorId,
            result,
            now,
            intent.ContentDigest,
            approvalExpiry,
            operatorNote);
        var reviewed = intent.ApplyReview(review);
        var reservation = approvalAllocation is null
            ? null
            : ExposureReservation.Reserve(
                ExposureReservationId.New(),
                intent,
                approvalAllocation.Id,
                approvalAllocation.Allocation.Currency,
                now);
        await intentPersistence.RecordReviewAsync(
            reviewed,
            ExecutionIntentState.REVIEW_PENDING,
            review,
            reservation,
            reconciliation?.Id,
            cancellationToken).ConfigureAwait(false);
        return review;
    }

    private async ValueTask<(AccountAllocationEntry Allocation, ReconciliationRecord Reconciliation)>
        ValidateApprovalAuthorityAsync(
        ExecutionIntent intent,
        OperatorId actorId,
        DateTimeOffset at,
        PaperRiskPolicy riskPolicy,
        CancellationToken cancellationToken)
    {
        var session = await sessionPersistence.FindAsync(intent.SessionId, cancellationToken)
            .ConfigureAwait(false);
        var goal = await goalPersistence.FindGoalVersionAsync(intent.GoalVersionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null
            || goal is null
            || goal.ExecutionBinding is null
            || session.State != SessionState.RUNNING
            || session.Mode != ExecutionMode.PAPER_REVIEW)
        {
            throw Failure("Current paper review authority is invalid.");
        }

        var activation = await activationPersistence.FindAsync(session.ActivationId, cancellationToken)
            .ConfigureAwait(false);
        var allocation = await allocationPersistence.FindByActivationAsync(
            session.ActivationId,
            cancellationToken).ConfigureAwait(false);
        var preflight = await preflightPersistence.FindAsync(session.Id, cancellationToken)
            .ConfigureAwait(false);
        var binding = goal.ExecutionBinding;
        if (activation?.GoalVersionId != goal.Id
            || !activation.CanStartSession(at)
            || allocation?.State != AccountAllocationState.ACTIVE
            || allocation.GoalVersionId != goal.Id
            || allocation.BrokerConnectionVersion != binding.BrokerConnectionVersion
            || allocation.Environment != BrokerEnvironment.PAPER
            || !string.Equals(allocation.EndpointIdentity, binding.EndpointIdentity, StringComparison.Ordinal)
            || !string.Equals(
                allocation.AccountIdentifier,
                binding.ExpectedAccountIdentifier,
                StringComparison.Ordinal)
            || preflight?.Result != ExecutablePreflightResult.PASSED
            || !preflight.HasValidContentDigest()
            || intent.AccountBinding.BrokerConnectionVersion != binding.BrokerConnectionVersion
            || intent.AccountBinding.Environment != BrokerEnvironment.PAPER
            || !string.Equals(intent.AccountBinding.EndpointIdentity, binding.EndpointIdentity, StringComparison.Ordinal)
            || !string.Equals(
                intent.AccountBinding.AccountIdentifier,
                binding.ExpectedAccountIdentifier,
                StringComparison.Ordinal))
        {
            throw Failure("Current paper review authority is invalid.");
        }

        try
        {
            await ExecutionSafetyService.RequireAllClearAsync(
                safetyPersistence,
                goal.Id,
                intent.AccountBinding,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionSafetyException exception)
        {
            throw Failure(exception.Message);
        }

        IntentRiskObservation observation;
        try
        {
            observation = await riskProbe.ObserveAsync(intent.AccountBinding, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failure("Current review safety evidence is unavailable.");
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

        var completedAt = timeProvider.GetUtcNow();
        if (!observation.KillSwitchClear
            || !observation.ReconciliationCurrent
            || observation.PositionExposure != reconciliation.PositionExposure
            || observation.OpenOrderExposure != reconciliation.OpenOrderExposure
            || observation.BuyingPower != reconciliation.BuyingPower
            || !string.Equals(
                observation.Currency, reconciliation.Currency, StringComparison.Ordinal)
            || observation.Broker.Environment != BrokerEnvironment.PAPER
            || observation.Broker.Environment != intent.AccountBinding.Environment
            || !string.Equals(
                observation.Broker.EndpointIdentity,
                intent.AccountBinding.EndpointIdentity,
                StringComparison.Ordinal)
            || !string.Equals(
                observation.Broker.AccountIdentifier,
                intent.AccountBinding.AccountIdentifier,
                StringComparison.Ordinal)
            || !ExecutionSafetyService.IsCurrentReconciliation(
                reconciliation, goal, intent.AccountBinding, riskPolicy, completedAt))
        {
            throw Failure("Current review safety checks failed.");
        }

        try
        {
            await ExecutionSafetyService.RequireAllClearAsync(
                safetyPersistence,
                goal.Id,
                intent.AccountBinding,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionSafetyException exception)
        {
            throw Failure(exception.Message);
        }

        return (allocation, reconciliation);
    }

    private static PaperReviewIntentException Failure(string message) => new(message);
}
