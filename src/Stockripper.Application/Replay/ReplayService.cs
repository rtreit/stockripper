using Stockripper.Application.Data;
using Stockripper.Application.Decisions;
using Stockripper.Application.Goals;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Replay;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Replay;

public sealed class ReplayService(
    IGoalPersistence goalPersistence,
    IGoalActivationPersistence activationPersistence,
    IDataSnapshotPersistence snapshotPersistence,
    IStrategyRuntime strategyRuntime,
    IDecisionPersistence decisionPersistence,
    DecisionGuard decisionGuard,
    IReplayPersistence replayPersistence,
    ISessionPersistence sessionPersistence,
    TimeProvider timeProvider)
{
    public async ValueTask<ReplayResult> RunAsync(
        GoalVersionId goalVersionId,
        ActivationId activationId,
        DataSnapshotId dataSnapshotId,
        long seed,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var goalVersion = await goalPersistence
            .FindGoalVersionAsync(goalVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ReplayGoalNotFoundException();
        if (goalVersion.Mode != ExecutionMode.REPLAY)
        {
            throw new ReplayPreflightException("The Goal version mode is not REPLAY.");
        }

        if (!goalVersion.HasValidContentDigest())
        {
            throw new ReplayPreflightException("The Goal version digest is invalid.");
        }

        var activation = await activationPersistence
            .FindAsync(activationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ReplayPreflightException("The Goal activation does not exist.");
        if (activation.GoalVersionId != goalVersion.Id)
        {
            throw new ReplayPreflightException("The Goal activation does not match the Goal version.");
        }

        var snapshot = await snapshotPersistence
            .FindAsync(dataSnapshotId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ReplaySnapshotNotFoundException();
        if (!snapshot.HasValidContentDigest())
        {
            throw new ReplayPreflightException("The data snapshot digest is invalid.");
        }

        if (snapshot.DataPolicyVersion != goalVersion.DataPolicyVersion)
        {
            throw new ReplayPreflightException("The data policy version does not match the Goal version.");
        }

        var startedAt = timeProvider.GetUtcNow();
        if (!goalVersion.DecisionSchedule.Allows(startedAt))
        {
            throw new ReplayPreflightException("The current time is outside the Replay decision schedule.");
        }

        var session = Session.Create(
            SessionId.New(),
            goalVersion,
            activation,
            startedAt,
            snapshot.Id,
            seed);
        await sessionPersistence.InsertAsync(session, actorId, cancellationToken).ConfigureAwait(false);
        session = await TransitionAsync(session, SessionState.PREFLIGHT, actorId, startedAt, cancellationToken)
            .ConfigureAwait(false);
        session = await TransitionAsync(session, SessionState.READY, actorId, startedAt, cancellationToken)
            .ConfigureAwait(false);
        session = await TransitionAsync(session, SessionState.RUNNING, actorId, startedAt, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var strategyOutput = await strategyRuntime.EvaluateAsync(
                new StrategyRuntimeRequest(
                    goalVersion.Id,
                    goalVersion.StrategyVersion,
                    snapshot.Id,
                    snapshot.ContentDigest,
                    seed),
                cancellationToken).ConfigureAwait(false);
            if (strategyOutput is null)
            {
                throw new ReplayPreflightException("The Strategy response is malformed.");
            }

            var decisionTime = timeProvider.GetUtcNow();
            var decision = decisionGuard.Evaluate(
                goalVersion,
                session,
                snapshot,
                strategyOutput,
                decisionTime);
            await decisionPersistence
                .InsertAsync(decision, actorId, cancellationToken)
                .ConfigureAwait(false);
            if (decision.State == DecisionState.GUARD_REJECTED)
            {
                throw new ReplayPreflightException("The Strategy response failed deterministic guards.");
            }

            var completedAt = timeProvider.GetUtcNow();
            var completedSession = session.TransitionTo(SessionState.COMPLETED, completedAt);
            var result = ReplayResult.Create(
                ReplayResultId.New(),
                session.Id,
                activation.Id,
                goalVersion,
                snapshot.Id,
                snapshot.ContentDigest,
                snapshot.RangeStart,
                snapshot.RangeEnd,
                seed,
                strategyOutput.OutputMetrics,
                strategyOutput.Evidence,
                startedAt,
                completedAt);
            await replayPersistence
                .InsertCompletedAsync(
                    result,
                    completedSession,
                    session.Revision,
                    actorId,
                    cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        catch
        {
            var failed = session.TransitionTo(
                SessionState.FAILED,
                timeProvider.GetUtcNow(),
                "REPLAY evaluation failed.");
            await sessionPersistence
                .UpdateAsync(
                    failed,
                    session.State,
                    session.Revision,
                    actorId,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<Session> TransitionAsync(
        Session current,
        SessionState next,
        OperatorId actorId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var updated = current.TransitionTo(next, at);
        await sessionPersistence
            .UpdateAsync(updated, current.State, current.Revision, actorId, at, cancellationToken)
            .ConfigureAwait(false);
        return updated;
    }
}
