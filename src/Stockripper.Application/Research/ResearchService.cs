using Stockripper.Application.Data;
using Stockripper.Application.Decisions;
using Stockripper.Application.Goals;
using Stockripper.Application.Replay;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Research;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Research;

public sealed class ResearchService(
    IGoalPersistence goalPersistence,
    IGoalActivationPersistence activationPersistence,
    IDataSnapshotPersistence snapshotPersistence,
    IStrategyRuntime strategyRuntime,
    IDecisionPersistence decisionPersistence,
    DecisionGuard decisionGuard,
    IResearchPersistence researchPersistence,
    ISessionPersistence sessionPersistence,
    TimeProvider timeProvider)
{
    public async ValueTask<ResearchResult> RunAsync(
        GoalVersionId goalVersionId,
        ActivationId activationId,
        DataSnapshotId dataSnapshotId,
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var goalVersion = await goalPersistence.FindGoalVersionAsync(goalVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResearchPreflightException("The Research Goal version does not exist.");
        if (goalVersion.Mode != ExecutionMode.RESEARCH || !goalVersion.HasValidContentDigest())
        {
            throw new ResearchPreflightException("The Goal version is not a valid RESEARCH version.");
        }

        var activation = await activationPersistence.FindAsync(activationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResearchPreflightException("The Goal activation does not exist.");
        if (activation.GoalVersionId != goalVersion.Id)
        {
            throw new ResearchPreflightException("The Goal activation does not match the Goal version.");
        }

        var snapshot = await snapshotPersistence.FindAsync(dataSnapshotId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResearchPreflightException("The Research data snapshot does not exist.");
        if (!snapshot.HasValidContentDigest()
            || snapshot.DataPolicyVersion != goalVersion.DataPolicyVersion)
        {
            throw new ResearchPreflightException("The Research data snapshot binding is invalid.");
        }

        var startedAt = timeProvider.GetUtcNow();
        if (!goalVersion.DecisionSchedule.Allows(startedAt))
        {
            throw new ResearchPreflightException("The current time is outside the Research decision schedule.");
        }

        var session = Session.Create(
            SessionId.New(),
            goalVersion,
            activation,
            startedAt,
            snapshot.Id);
        await sessionPersistence.InsertAsync(session, actorId, cancellationToken).ConfigureAwait(false);
        session = await TransitionAsync(session, SessionState.PREFLIGHT, actorId, startedAt, cancellationToken)
            .ConfigureAwait(false);
        session = await TransitionAsync(session, SessionState.READY, actorId, startedAt, cancellationToken)
            .ConfigureAwait(false);
        session = await TransitionAsync(session, SessionState.RUNNING, actorId, startedAt, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var output = await strategyRuntime.EvaluateAsync(
                new StrategyRuntimeRequest(
                    goalVersion.Id,
                    goalVersion.StrategyVersion,
                    snapshot.Id,
                    snapshot.ContentDigest,
                    0),
                cancellationToken).ConfigureAwait(false);
            if (output is null)
            {
                throw new ResearchPreflightException("The Strategy response is malformed.");
            }

            var decision = decisionGuard.Evaluate(
                goalVersion,
                session,
                snapshot,
                output,
                timeProvider.GetUtcNow());
            await decisionPersistence.InsertAsync(decision, actorId, cancellationToken).ConfigureAwait(false);
            if (decision.State == DecisionState.GUARD_REJECTED)
            {
                throw new ResearchPreflightException("The Strategy response failed deterministic guards.");
            }

            var completedAt = timeProvider.GetUtcNow();
            var completedSession = session.TransitionTo(SessionState.COMPLETED, completedAt);
            var result = ResearchResult.Create(
                ResearchResultId.New(),
                session.Id,
                activation.Id,
                goalVersion,
                snapshot,
                output.OutputMetrics,
                output.Evidence,
                startedAt,
                completedAt);
            await researchPersistence.InsertCompletedAsync(
                result,
                completedSession,
                session.Revision,
                actorId,
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            var failed = session.TransitionTo(
                SessionState.FAILED,
                timeProvider.GetUtcNow(),
                "RESEARCH evaluation failed.");
            await sessionPersistence.UpdateAsync(
                failed,
                session.State,
                session.Revision,
                actorId,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
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
        await sessionPersistence.UpdateAsync(
            updated,
            current.State,
            current.Revision,
            actorId,
            at,
            cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
