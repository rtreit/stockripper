using Stockripper.Application.Execution;
using Stockripper.Host.Runtime;

namespace Stockripper.Host.Tests;

public sealed class StockripperRuntimeStateTests
{
    private static readonly DateTimeOffset ReferenceTime = new(
        2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Startup_reconciliation_difference_reports_degraded_runtime()
    {
        var state = new StockripperRuntimeState();
        var recovery = new PaperExecutionStartupRecoveryOutcome(
            1,
            0,
            0,
            new PaperReconciliationRecoveryOutcome(1, 0, 1, 0));

        state.MarkReady(0, recovery, ReferenceTime);

        Assert.True(state.IsReady);
        Assert.Equal("DEGRADED", state.Status);
        Assert.Equal(1, state.ReconciliationLockedCount);
    }

    [Fact]
    public void Periodic_reconciliation_difference_reports_degraded_runtime()
    {
        var state = new StockripperRuntimeState();

        state.RecordReconciliation(
            new PaperReconciliationRecoveryOutcome(1, 0, 1, 0),
            ReferenceTime);

        Assert.Equal("DEGRADED", state.Status);
        Assert.Equal(1, state.ReconciliationLockedCount);
    }

    [Fact]
    public void Current_reconciliation_reports_ready_runtime()
    {
        var state = new StockripperRuntimeState();

        state.RecordReconciliation(
            new PaperReconciliationRecoveryOutcome(1, 1, 0, 0),
            ReferenceTime);

        Assert.Equal("READY", state.Status);
    }
}
