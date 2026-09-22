using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Application.Execution;

public sealed record PaperPositionData(
    string InstrumentIdentifier,
    decimal Quantity,
    decimal? AverageEntryPrice,
    decimal MarketValue,
    string Currency);

public sealed record PaperPositionObservation(
    FillId FillId,
    IntentAccountBinding ObservedBinding,
    string ObservationReference,
    DateTimeOffset BrokerObservationTime,
    string SourcePayloadReference,
    IReadOnlyList<PaperPositionData> Positions);

public sealed record PostFillSafetyOutcome(
    PositionSnapshotBatch PositionBatch,
    IReadOnlyList<PositionSnapshot> Positions,
    PostFillEvaluation Evaluation,
    PostFillSafetyResponse? SafetyResponse,
    bool Duplicate);

public interface IPaperPostFillSafetyPersistence
{
    ValueTask<PostFillSafetyOutcome> EvaluateAsync(
        FillId fillId,
        PositionSnapshotBatch positionBatch,
        IReadOnlyList<PositionSnapshot> positions,
        OperatorId actorId,
        CancellationToken cancellationToken);
}

public sealed class PaperPostFillSafetyService(
    IPaperPostFillSafetyPersistence persistence,
    TimeProvider timeProvider,
    IExecutionSafetyPersistence? safetyPersistence = null,
    ITriggeredCancellationCoordinator? cancellationCoordinator = null,
    IPaperSubmissionInterlock? submissionInterlock = null)
{
    public async ValueTask<PostFillSafetyOutcome> EvaluateAsync(
        PaperPositionObservation observation,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.Positions);
        if (observation.Positions.Select(item => item.InstrumentIdentifier)
            .Distinct(StringComparer.Ordinal).Count() != observation.Positions.Count)
        {
            throw new PaperOrderException("A Position observation contains duplicate instruments.");
        }

        var batch = PositionSnapshotBatch.Create(PositionSnapshotBatchId.New(),
            observation.ObservedBinding, observation.ObservationReference,
            observation.Positions.Count, observation.BrokerObservationTime,
            timeProvider.GetUtcNow(), observation.SourcePayloadReference);
        var snapshots = observation.Positions
            .Select(position => PositionSnapshot.Create(PositionSnapshotId.New(), batch,
                observation.ObservedBinding, position.InstrumentIdentifier, position.Quantity,
                position.AverageEntryPrice, position.MarketValue, position.Currency))
            .OrderBy(position => position.InstrumentIdentifier, StringComparer.Ordinal)
            .ToArray();
        PostFillSafetyOutcome outcome;
        IReadOnlyList<KillSwitch> switches;
        await using (submissionInterlock is null
            ? null
            : await submissionInterlock.EnterAsync(cancellationToken)
                .ConfigureAwait(false))
        {
            outcome = await persistence.EvaluateAsync(observation.FillId, batch, snapshots,
                actorId, cancellationToken).ConfigureAwait(false);
            if (outcome.SafetyResponse is null)
            {
                return outcome;
            }

            if (safetyPersistence is null || cancellationCoordinator is null)
            {
                throw new ExecutionSafetyException(
                    "Post-Fill cancellation coordination is not configured.");
            }

            var requiredScopes = PaperExecutionSafetyScopes.Required(
                outcome.SafetyResponse.GoalVersionId, outcome.SafetyResponse.AccountBinding);
            switches = await safetyPersistence.FindKillSwitchesAsync(
                requiredScopes, cancellationToken).ConfigureAwait(false);
            if (switches.Count != requiredScopes.Count
                || switches.Any(item => (item.ScopeType is KillSwitchScopeType.ACCOUNT
                    or KillSwitchScopeType.GOAL) && item.State == KillSwitchState.CLEAR))
            {
                throw new ExecutionSafetyException(
                    "Post-Fill durable kill-switch state is incomplete.");
            }
        }

        foreach (var killSwitch in switches.Where(item => item.State != KillSwitchState.CLEAR))
        {
            _ = await cancellationCoordinator.CancelForTriggeredScopeAsync(
                killSwitch, actorId, outcome.SafetyResponse.Reason, cancellationToken)
                .ConfigureAwait(false);
        }

        return outcome;
    }
}
