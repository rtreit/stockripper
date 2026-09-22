using Stockripper.Application.Execution;
using Stockripper.Domain.Common;

namespace Stockripper.Host.Runtime;

public sealed record StockripperPeriodicReconciliationOptions(TimeSpan Interval);

public sealed partial class StockripperPeriodicReconciliationService(
    IPaperReconciliationRecovery reconciliationRecovery,
    PaperReviewExpiryService reviewExpiryService,
    StockripperPeriodicReconciliationOptions options,
    StockripperRuntimeState runtimeState,
    TimeProvider timeProvider,
    ILogger<StockripperPeriodicReconciliationService> logger) : BackgroundService
{
    private static readonly OperatorId RecoveryActor = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Interval < TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException(
                "The periodic reconciliation interval must be at least five seconds.");
        }

        using var timer = new PeriodicTimer(options.Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!runtimeState.IsReady)
            {
                continue;
            }

            try
            {
                var expiredCount = await reviewExpiryService.ExpireDueAsync(
                    RecoveryActor, stoppingToken).ConfigureAwait(false);
                var outcome = await reconciliationRecovery.RecoverAsync(
                    RecoveryActor, stoppingToken).ConfigureAwait(false);
                runtimeState.RecordReconciliation(outcome, timeProvider.GetUtcNow());
                LogCompleted(logger, expiredCount, outcome.TargetCount, outcome.CurrentCount,
                    outcome.LockedCount, outcome.UnavailableCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                runtimeState.RecordReconciliationFailure(timeProvider.GetUtcNow());
                LogFailed(logger);
            }
        }
    }

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Periodic paper maintenance completed. Expired reviews: {ExpiredReviewCount}. Targets: {TargetCount}. Current: {CurrentCount}. Locked: {LockedCount}. Unavailable: {UnavailableCount}.")]
    private static partial void LogCompleted(ILogger logger, int expiredReviewCount, int targetCount,
        int currentCount, int lockedCount, int unavailableCount);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Error,
        Message = "Periodic paper reconciliation failed closed.")]
    private static partial void LogFailed(ILogger logger);
}
