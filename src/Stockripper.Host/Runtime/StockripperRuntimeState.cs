namespace Stockripper.Host.Runtime;

using Stockripper.Application.Execution;

public sealed class StockripperRuntimeState
{
    public bool IsReady { get; private set; }
    public string Status { get; private set; } = "STARTING";
    public int RecoveredSessionCount { get; private set; }
    public int RecoveredSubmissionCount { get; private set; }
    public int RecoveredCancellationCount { get; private set; }
    public int ReconciliationTargetCount { get; private set; }
    public int ReconciliationCurrentCount { get; private set; }
    public int ReconciliationLockedCount { get; private set; }
    public int ReconciliationUnavailableCount { get; private set; }
    public DateTimeOffset? LastReconciliationAt { get; private set; }
    public bool PeriodicReconciliationFailed { get; private set; }

    public void MarkReady(int recoveredReadOnlySessionCount,
        PaperExecutionStartupRecoveryOutcome paperRecovery,
        DateTimeOffset completedAt)
    {
        RecoveredSessionCount = checked(
            recoveredReadOnlySessionCount + paperRecovery.LockedSessionCount);
        RecoveredSubmissionCount = paperRecovery.RecoveredSubmissionCount;
        RecoveredCancellationCount = paperRecovery.RecoveredCancellationCount;
        ReconciliationTargetCount = paperRecovery.Reconciliation.TargetCount;
        ReconciliationCurrentCount = paperRecovery.Reconciliation.CurrentCount;
        ReconciliationLockedCount = paperRecovery.Reconciliation.LockedCount;
        ReconciliationUnavailableCount = paperRecovery.Reconciliation.UnavailableCount;
        LastReconciliationAt = completedAt;
        PeriodicReconciliationFailed = false;
        Status = ReconciliationIsHealthy(
            ReconciliationLockedCount,
            ReconciliationUnavailableCount)
            ? "READY"
            : "DEGRADED";
        IsReady = true;
    }

    public void RecordReconciliation(
        PaperReconciliationRecoveryOutcome outcome,
        DateTimeOffset completedAt)
    {
        ReconciliationTargetCount = outcome.TargetCount;
        ReconciliationCurrentCount = outcome.CurrentCount;
        ReconciliationLockedCount = outcome.LockedCount;
        ReconciliationUnavailableCount = outcome.UnavailableCount;
        LastReconciliationAt = completedAt;
        PeriodicReconciliationFailed = false;
        Status = ReconciliationIsHealthy(outcome.LockedCount, outcome.UnavailableCount)
            ? "READY"
            : "DEGRADED";
    }

    public void RecordReconciliationFailure(DateTimeOffset failedAt)
    {
        LastReconciliationAt = failedAt;
        PeriodicReconciliationFailed = true;
        Status = "DEGRADED";
    }

    public void MarkFailed()
    {
        Status = "FAILED";
        IsReady = false;
    }

    private static bool ReconciliationIsHealthy(int lockedCount, int unavailableCount) =>
        lockedCount == 0 && unavailableCount == 0;
}
