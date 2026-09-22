using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class ExecutionSafetyTests
{
    [Fact]
    public void Kill_switch_requires_post_trigger_reconciliation_before_clear()
    {
        var triggeredAt = GoalTestData.ReferenceTime.AddMinutes(1);
        var killSwitch = KillSwitch.Configure(
            KillSwitchId.New(),
            KillSwitchScopeType.ACCOUNT,
            "paper-account",
            GoalTestData.ReferenceTime).Trigger(triggeredAt, "Operator stopped execution.");
        var staleReconciliation = CreateReconciliation(
            startedAt: GoalTestData.ReferenceTime.AddSeconds(-2),
            completedAt: GoalTestData.ReferenceTime.AddSeconds(-1));

        Assert.Throws<DomainRuleViolationException>(() => killSwitch.RequireRecovery(
            staleReconciliation,
            triggeredAt.AddMinutes(1)));

        var currentReconciliation = CreateReconciliation(
            startedAt: triggeredAt.AddSeconds(1),
            completedAt: triggeredAt.AddSeconds(2));
        var recovery = killSwitch.RequireRecovery(
            currentReconciliation,
            triggeredAt.AddSeconds(3));
        var cleared = recovery.Clear(triggeredAt.AddSeconds(4));

        Assert.Equal(KillSwitchState.RECOVERY_REQUIRED, recovery.State);
        Assert.Equal(currentReconciliation.Id, recovery.RecoveryReconciliationId);
        Assert.Equal(KillSwitchState.CLEAR, cleared.State);
        Assert.Equal(3, cleared.Revision);
    }

    [Fact]
    public void Reconciliation_with_differences_locks_execution_and_detects_tampering()
    {
        var reconciliation = CreateReconciliation(
            differences: ["Broker position differs from durable position."],
            corrections: ["No automatic correction was applied."]);

        Assert.Equal(ReconciliationResult.DIFFERENCE_LOCKED, reconciliation.Result);
        Assert.True(reconciliation.HasValidContentDigest());
        Assert.Throws<InvalidDataException>(() => ReconciliationRecord.Restore(
            reconciliation.Id,
            reconciliation.GoalVersionId,
            reconciliation.AccountBinding,
            reconciliation.ReconciliationPolicyVersion,
            reconciliation.StartedAt,
            reconciliation.CompletedAt,
            reconciliation.ObservedAt,
            reconciliation.PositionExposure + 1m,
            reconciliation.OpenOrderExposure,
            reconciliation.BuyingPower,
            reconciliation.Currency,
            reconciliation.ComparedOrderRange,
            reconciliation.ComparedFillRange,
            reconciliation.ComparedPositionSnapshot,
            reconciliation.Differences,
            reconciliation.Corrections,
            reconciliation.OperatorAction,
            reconciliation.Result,
            reconciliation.ContentDigest));
    }

    [Fact]
    public void Reconciliation_rejects_an_unverifiable_compared_range()
    {
        Assert.Throws<DomainRuleViolationException>(() => CreateReconciliation(
            comparedOrderRange: "orders:summary:current"));
    }

    [Fact]
    public void Exposure_reservation_keeps_authority_digest_when_released()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var intent = CreateIntent(goal);
        var reservation = ExposureReservation.Reserve(
            ExposureReservationId.New(),
            intent,
            AccountAllocationEntryId.New(),
            "USD",
            GoalTestData.ReferenceTime);

        var adjusted = reservation.AdjustRemaining(reservation.Amount / 2m);
        var released = adjusted.Release(
            GoalTestData.ReferenceTime.AddMinutes(1),
            "The intent expired before submission.");

        Assert.Equal(intent.MaximumExposure, reservation.Amount);
        Assert.Equal(intent.MaximumExposure / 2m, adjusted.RemainingAmount);
        Assert.Equal(ExposureReservationState.RELEASED, released.State);
        Assert.Equal(0m, released.RemainingAmount);
        Assert.Equal(reservation.ContentDigest, released.ContentDigest);
        Assert.True(released.HasValidContentDigest());
        Assert.Throws<DomainRuleViolationException>(() => released.Release(
            GoalTestData.ReferenceTime.AddMinutes(2),
            "Duplicate release."));
    }

    private static ReconciliationRecord CreateReconciliation(
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        string[]? differences = null,
        string[]? corrections = null,
        string? comparedOrderRange = null)
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var binding = goal.ExecutionBinding!;
        var started = startedAt ?? GoalTestData.ReferenceTime;
        var completed = completedAt ?? started.AddSeconds(1);
        return ReconciliationRecord.Create(
            ReconciliationId.New(),
            goal.Id,
            new IntentAccountBinding(
                binding.BrokerConnectionVersion,
                binding.ExpectedEnvironment,
                binding.EndpointIdentity,
                binding.ExpectedAccountIdentifier),
            binding.ReconciliationPolicyVersion,
            started,
            completed,
            completed,
            1_000m,
            250m,
            25_000m,
            "USD",
            comparedOrderRange
                ?? "orders:sha256:0000000000000000000000000000000000000000000000000000000000000000",
            "fills:sha256:0000000000000000000000000000000000000000000000000000000000000000",
            "positions:sha256:0000000000000000000000000000000000000000000000000000000000000000",
            differences ?? [],
            corrections ?? [],
            differences is { Length: > 0 }
                ? "Review and resolve the reconciliation difference."
                : null,
            observationSucceeded: true);
    }

    private static ExecutionIntent CreateIntent(GoalVersion goal)
    {
        var binding = goal.ExecutionBinding!;
        return ExecutionIntent.Create(
            ExecutionIntentId.New(),
            DecisionId.New(),
            goal.Id,
            SessionId.New(),
            ExecutionMode.PAPER_REVIEW,
            new IntentAccountBinding(
                binding.BrokerConnectionVersion,
                binding.ExpectedEnvironment,
                binding.EndpointIdentity,
                binding.ExpectedAccountIdentifier),
            "AAPL",
            OrderSide.BUY,
            IntentOrderType.LIMIT,
            10m,
            100m,
            IntentTimeInForce.DAY,
            GoalTestData.ReferenceTime.AddMinutes(5),
            RiskEvaluationId.New(),
            GoalTestData.ReferenceTime);
    }
}
