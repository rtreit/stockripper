using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;

namespace Stockripper.Application.Execution;

public sealed record PaperExecutionStartupRecoveryOutcome(
    int LockedSessionCount,
    int RecoveredSubmissionCount,
    int RecoveredCancellationCount,
    PaperReconciliationRecoveryOutcome Reconciliation)
{
    public int TotalCount => checked(
        LockedSessionCount + RecoveredSubmissionCount + RecoveredCancellationCount
        + Reconciliation.TargetCount);
}

public sealed class PaperExecutionStartupRecoveryService(
    ExecutableSessionRecoveryService sessionRecovery,
    IPaperSubmissionRecovery submissionRecovery,
    IPaperCancellationRecovery cancellationRecovery,
    IPaperReconciliationRecovery reconciliationRecovery)
{
    public async ValueTask<PaperExecutionStartupRecoveryOutcome> RecoverAsync(
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var lockedSessions = await sessionRecovery.RecoverAsync(actorId, cancellationToken)
            .ConfigureAwait(false);
        var cancellations = await cancellationRecovery.RecoverAsync(actorId, cancellationToken)
            .ConfigureAwait(false);
        var submissions = await submissionRecovery.RecoverAsync(actorId, cancellationToken)
            .ConfigureAwait(false);
        var reconciliation = await reconciliationRecovery.RecoverAsync(actorId, cancellationToken)
            .ConfigureAwait(false);
        return new(lockedSessions, submissions.Count, cancellations.Count, reconciliation);
    }
}
