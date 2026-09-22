using Stockripper.Application.Audit;
using Stockripper.Application.Execution;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Infrastructure.Persistence;

namespace Stockripper.Host.Runtime;

public sealed partial class StockripperStartupService(
    PostgresDatabaseMigrator migrator,
    AuditIntegrityGuard auditIntegrityGuard,
    PaperReviewExpiryService reviewExpiryService,
    ReadOnlySessionRecoveryService readOnlyRecoveryService,
    PaperExecutionStartupRecoveryService paperExecutionRecoveryService,
    StockripperRuntimeState runtimeState,
    TimeProvider timeProvider,
    ILogger<StockripperStartupService> logger) : IHostedService
{
    private static readonly OperatorId RecoveryActor = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
            await auditIntegrityGuard.VerifyOrThrowAsync(cancellationToken).ConfigureAwait(false);
            _ = await reviewExpiryService.ExpireDueAsync(RecoveryActor, cancellationToken)
                .ConfigureAwait(false);
            var recoveredReadOnly = await readOnlyRecoveryService
                .RecoverAsync(RecoveryActor, cancellationToken)
                .ConfigureAwait(false);
            var recoveredExecutable = await paperExecutionRecoveryService
                .RecoverAsync(RecoveryActor, cancellationToken)
                .ConfigureAwait(false);
            runtimeState.MarkReady(recoveredReadOnly, recoveredExecutable,
                timeProvider.GetUtcNow());
            LogReady(logger, runtimeState.RecoveredSessionCount,
                recoveredExecutable.RecoveredSubmissionCount,
                recoveredExecutable.RecoveredCancellationCount,
                recoveredExecutable.Reconciliation.TargetCount,
                recoveredExecutable.Reconciliation.UnavailableCount);
        }
        catch
        {
            runtimeState.MarkFailed();
            LogStartupFailure(logger);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Stockripper is ready. Recovered Sessions: {RecoveredSessionCount}. Recovered submissions: {RecoveredSubmissionCount}. Recovered cancellations: {RecoveredCancellationCount}. Reconciliation targets: {ReconciliationTargetCount}. Unavailable reconciliations: {ReconciliationUnavailableCount}.")]
    private static partial void LogReady(ILogger logger, int recoveredSessionCount,
        int recoveredSubmissionCount, int recoveredCancellationCount,
        int reconciliationTargetCount, int reconciliationUnavailableCount);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Critical,
        Message = "Stockripper startup safety checks failed.")]
    private static partial void LogStartupFailure(ILogger logger);
}
