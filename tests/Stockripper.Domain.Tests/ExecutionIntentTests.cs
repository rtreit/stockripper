using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class ExecutionIntentTests
{
    [Fact]
    public void Accepted_risk_moves_paper_intent_to_review_without_changing_digest()
    {
        var values = CreateValues();
        var created = CreateIntent(values);
        var risk = CreateRisk(values, accepted: true);

        var pending = created.ApplyRisk(risk).AwaitReview();

        Assert.Equal(ExecutionIntentState.REVIEW_PENDING, pending.State);
        Assert.Equal(created.ContentDigest, pending.ContentDigest);
        Assert.True(pending.HasValidContentDigest());
    }

    [Fact]
    public void Failed_risk_cannot_enter_review()
    {
        var values = CreateValues();
        var rejected = CreateIntent(values).ApplyRisk(CreateRisk(values, accepted: false));

        Assert.Equal(ExecutionIntentState.RISK_REJECTED, rejected.State);
        Assert.Throws<DomainRuleViolationException>(() => rejected.AwaitReview());
    }

    [Fact]
    public void Paper_auto_keeps_accepted_risk_as_activation_authorized_state()
    {
        var values = CreateValues(ExecutionMode.PAPER_AUTO);

        var accepted = CreateIntent(values).ApplyRisk(CreateRisk(values, accepted: true));

        Assert.Equal(ExecutionIntentState.RISK_ACCEPTED, accepted.State);
        Assert.Throws<DomainRuleViolationException>(() => accepted.AwaitReview());
    }

    [Fact]
    public void Review_approval_is_valid_only_for_the_exact_intent_digest()
    {
        var values = CreateValues();
        var pending = CreateIntent(values).ApplyRisk(CreateRisk(values, accepted: true)).AwaitReview();
        var approval = ReviewApproval.Create(
            ReviewApprovalId.New(),
            pending.Id,
            OperatorId.New(),
            ReviewResult.APPROVED,
            GoalTestData.ReferenceTime.AddSeconds(1),
            pending.ContentDigest,
            GoalTestData.ReferenceTime.AddMinutes(2),
            "Approve the bounded paper order.");

        var approved = pending.ApplyReview(approval);

        Assert.Equal(ExecutionIntentState.APPROVED, approved.State);
        Assert.Equal(pending.ContentDigest, approved.ContentDigest);
        var other = CreateIntent(CreateValues());
        Assert.Throws<DomainRuleViolationException>(() => other
            .ApplyRisk(CreateRiskForIntent(other, accepted: true))
            .AwaitReview()
            .ApplyReview(approval));
    }

    [Fact]
    public void Operator_modification_has_a_new_identity_and_digest()
    {
        var firstValues = CreateValues();
        var secondValues = CreateValues();
        var first = CreateIntent(firstValues, limitPrice: 100m);
        var second = CreateIntent(secondValues, limitPrice: 101m);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.RiskEvaluationId, second.RiskEvaluationId);
        Assert.NotEqual(first.ContentDigest, second.ContentDigest);
    }

    [Fact]
    public void Approved_intent_expires_when_its_bound_approval_expires()
    {
        var values = CreateValues();
        var pending = CreateIntent(values).ApplyRisk(CreateRisk(values, accepted: true)).AwaitReview();
        var approvalExpiry = GoalTestData.ReferenceTime.AddMinutes(2);
        var approval = ReviewApproval.Create(
            ReviewApprovalId.New(),
            pending.Id,
            OperatorId.New(),
            ReviewResult.APPROVED,
            GoalTestData.ReferenceTime.AddSeconds(1),
            pending.ContentDigest,
            approvalExpiry,
            "Approve the bounded paper order.");
        var approved = pending.ApplyReview(approval);

        var expired = approved.ExpireApproval(approvalExpiry, approval);

        Assert.Equal(ExecutionIntentState.EXPIRED, expired.State);
        Assert.Equal(approved.ContentDigest, expired.ContentDigest);
        Assert.True(expired.HasValidContentDigest());
    }

    private static ExecutionIntent CreateIntent(Values values, decimal limitPrice = 100m) => ExecutionIntent.Create(
        values.IntentId,
        values.DecisionId,
        values.Goal.Id,
        values.SessionId,
        values.Goal.Mode,
        values.AccountBinding,
        "AAPL",
        OrderSide.BUY,
        IntentOrderType.LIMIT,
        10m,
        limitPrice,
        IntentTimeInForce.DAY,
        GoalTestData.ReferenceTime.AddMinutes(5),
        values.RiskEvaluationId,
        GoalTestData.ReferenceTime);

    private static RiskEvaluation CreateRisk(Values values, bool accepted) => RiskEvaluation.Create(
        values.RiskEvaluationId,
        values.IntentId,
        values.DecisionId,
        values.Goal.Id,
        values.SessionId,
        values.Goal.RiskPolicyVersion,
        values.AllocationEntryId,
        ReconciliationId.New(),
        1_000m,
        500m,
        1_000m,
        25_000m,
        "USD",
        [new RiskCheck(RiskCheckCode.CAPITAL_ALLOCATION, accepted, "Exposure must remain inside allocation.")],
        GoalTestData.ReferenceTime);

    private static RiskEvaluation CreateRiskForIntent(ExecutionIntent intent, bool accepted) => RiskEvaluation.Create(
        intent.RiskEvaluationId,
        intent.Id,
        intent.DecisionId,
        intent.GoalVersionId,
        intent.SessionId,
        new VersionReference("risk-policy", "1", "digest-risk-policy-1"),
        AccountAllocationEntryId.New(),
        ReconciliationId.New(),
        1_000m,
        500m,
        intent.MaximumExposure,
        25_000m,
        "USD",
        [new RiskCheck(RiskCheckCode.CAPITAL_ALLOCATION, accepted, "Exposure must remain inside allocation.")],
        GoalTestData.ReferenceTime);

    private static Values CreateValues(
        ExecutionMode mode = ExecutionMode.PAPER_REVIEW)
    {
        var goal = GoalTestData.CreateGoalVersion(mode);
        var binding = goal.ExecutionBinding!;
        return new Values(
            goal,
            SessionId.New(),
            DecisionId.New(),
            ExecutionIntentId.New(),
            RiskEvaluationId.New(),
            AccountAllocationEntryId.New(),
            new IntentAccountBinding(
                binding.BrokerConnectionVersion,
                binding.ExpectedEnvironment,
                binding.EndpointIdentity,
                binding.ExpectedAccountIdentifier));
    }

    private sealed record Values(
        GoalVersion Goal,
        SessionId SessionId,
        DecisionId DecisionId,
        ExecutionIntentId IntentId,
        RiskEvaluationId RiskEvaluationId,
        AccountAllocationEntryId AllocationEntryId,
        IntentAccountBinding AccountBinding);
}
