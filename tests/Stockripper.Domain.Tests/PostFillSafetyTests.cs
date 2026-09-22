using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Application.Execution;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class PostFillSafetyTests
{
    [Fact]
    public void Complete_position_batch_creates_allocation_evaluation()
    {
        var values = CreateValues();
        var snapshot = PositionSnapshot.Create(PositionSnapshotId.New(), values.Batch,
            values.Binding, "AAPL", 10m, 99m, 990m, "USD");
        var evaluation = PostFillEvaluation.Create(PostFillEvaluationId.New(), FillId.New(),
            OrderId.New(), values.Goal.Id, SessionId.New(), values.Goal.RiskPolicyVersion,
            AccountAllocationEntryId.New(), values.Batch, snapshot.MarketValue, 500m,
            10_000m, 1_490m, 8_000m, 10_000m, 1m, "USD", true,
            values.Now.AddSeconds(2));

        Assert.Equal(PostFillEvaluationResult.PASSED, evaluation.Result);
        Assert.Equal(1_490m, evaluation.TotalExposure);
        Assert.Equal(64, snapshot.ContentDigest.Length);
    }

    [Fact]
    public void Allocation_excess_requires_reconciliation_and_operator_notification()
    {
        var values = CreateValues();
        var evaluation = PostFillEvaluation.Create(PostFillEvaluationId.New(), FillId.New(),
            OrderId.New(), values.Goal.Id, SessionId.New(), values.Goal.RiskPolicyVersion,
            AccountAllocationEntryId.New(), values.Batch, 9_800m, 500m,
            10_000m, 9_800m, 10_000m, 10_000m, 1m, "USD", true,
            values.Now.AddSeconds(2));
        var response = PostFillSafetyResponse.Create(PostFillSafetyResponseId.New(), evaluation,
            values.Binding, "Observed exposure exceeds the Goal allocation.",
            values.Now.AddSeconds(3));

        Assert.Equal(PostFillEvaluationResult.VIOLATION_LOCKED, evaluation.Result);
        Assert.Equal(PostFillReconciliationState.REQUESTED, response.ReconciliationState);
        Assert.Equal(OperatorNotificationState.PENDING, response.NotificationState);
    }

    [Theory]
    [InlineData(6_000, 6_000, 5_000, 10_000, 100)]
    [InlineData(10_500, 5_250, 8_000, 10_000, 100)]
    [InlineData(10_000, 8_000, 8_000, 10_000, 75)]
    public void Risk_policy_excess_locks_post_fill_evaluation(
        int totalExposure,
        int largestInstrumentExposure,
        int maximumPositionExposure,
        int maximumPortfolioExposure,
        int maximumConcentrationPercent)
    {
        var values = CreateValues();
        var evaluation = PostFillEvaluation.Create(PostFillEvaluationId.New(), FillId.New(),
            OrderId.New(), values.Goal.Id, SessionId.New(), values.Goal.RiskPolicyVersion,
            AccountAllocationEntryId.New(), values.Batch, totalExposure, 0m,
            20_000m, largestInstrumentExposure, maximumPositionExposure,
            maximumPortfolioExposure, maximumConcentrationPercent / 100m, "USD", true,
            values.Now.AddSeconds(2));

        Assert.Equal(PostFillEvaluationResult.VIOLATION_LOCKED, evaluation.Result);
    }

    [Fact]
    public void Snapshot_rejects_wrong_account_and_fractional_quantity()
    {
        var values = CreateValues();
        var wrong = new IntentAccountBinding(values.Binding.BrokerConnectionVersion,
            values.Binding.Environment, values.Binding.EndpointIdentity, "wrong-paper-account");

        Assert.Throws<DomainRuleViolationException>(() => PositionSnapshot.Create(
            PositionSnapshotId.New(), values.Batch, wrong, "AAPL", 10m, 99m, 990m, "USD"));
        Assert.Throws<DomainRuleViolationException>(() => PositionSnapshot.Create(
            PositionSnapshotId.New(), values.Batch, values.Binding, "AAPL", 1.5m, 99m, 148.5m, "USD"));
    }

    [Fact]
    public void Legacy_post_fill_digest_restores_with_migration_defaults()
    {
        var values = CreateValues();
        var id = PostFillEvaluationId.New();
        var fillId = FillId.New();
        var orderId = OrderId.New();
        var sessionId = SessionId.New();
        var allocationId = AccountAllocationEntryId.New();
        var evaluatedAt = values.Now.AddSeconds(2);
        var legacy = new
        {
            Schema = "stockripper-post-fill-evaluation-v1",
            Id = id.Value,
            FillId = fillId.Value,
            OrderId = orderId.Value,
            GoalVersionId = values.Goal.Id.Value,
            SessionId = sessionId.Value,
            RiskPolicyVersion = new
            {
                values.Goal.RiskPolicyVersion.Identifier,
                values.Goal.RiskPolicyVersion.Version,
                values.Goal.RiskPolicyVersion.Digest
            },
            AllocationEntryId = allocationId.Value,
            PositionBatchId = values.Batch.Id.Value,
            PositionExposure = DecimalValue(990m),
            OpenOrderExposure = DecimalValue(500m),
            AllocationAmount = DecimalValue(10_000m),
            Currency = "USD",
            PositionEvidenceCurrent = true,
            Result = PostFillEvaluationResult.PASSED.ToString(),
            EvaluatedAt = evaluatedAt.ToString("O")
        };
        var storedDigest = Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacy)));

        var restored = PostFillEvaluation.Restore(id, fillId, orderId,
            values.Goal.Id, sessionId, values.Goal.RiskPolicyVersion,
            allocationId, values.Batch, 990m, 500m, 10_000m,
            990m, 10_000m, 10_000m, 1m, "USD", true,
            evaluatedAt, storedDigest);

        Assert.Equal(storedDigest, restored.ContentDigest);
        Assert.Equal(PostFillEvaluationResult.PASSED, restored.Result);
    }

    private static Values CreateValues()
    {
        var now = GoalTestData.ReferenceTime;
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var binding = ExecutionSafetyService.ToAccountBinding(goal);
        var batch = PositionSnapshotBatch.Create(PositionSnapshotBatchId.New(), binding,
            "positions-1", 1, now.AddSeconds(1), now.AddSeconds(1), "payload/positions-1");
        return new(now, goal, binding, batch);
    }

    private sealed record Values(DateTimeOffset Now, Domain.Goals.GoalVersion Goal,
        IntentAccountBinding Binding, PositionSnapshotBatch Batch);

    private static string DecimalValue(decimal value) =>
        value.ToString("G29", CultureInfo.InvariantCulture);
}
