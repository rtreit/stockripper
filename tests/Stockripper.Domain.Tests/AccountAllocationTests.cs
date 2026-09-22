using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class AccountAllocationTests
{
    [Fact]
    public void Allocation_limit_preserves_exact_paper_binding()
    {
        var binding = GoalTestData.CreateExecutionBinding(BrokerEnvironment.PAPER);

        var limit = AccountAllocationLimit.Create(
            new VersionReference("paper-account-limit", "1", "digest-paper-account-limit-1"),
            binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            new Money(25_000m, "USD"),
            OperatorId.New(),
            GoalTestData.ReferenceTime);

        Assert.Equal(BrokerEnvironment.PAPER, limit.Environment);
        Assert.Equal(binding.ExpectedAccountIdentifier, limit.AccountIdentifier);
        Assert.Equal(25_000m, limit.MaximumAllocation.Amount);
        Assert.True(limit.HasValidContentDigest());
    }

    [Fact]
    public void Allocation_entry_rejects_a_live_environment()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var binding = goal.ExecutionBinding!;
        var limit = AccountAllocationLimit.Create(
            new VersionReference("paper-account-limit", "1", "digest-paper-account-limit-1"),
            binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            new Money(25_000m, "USD"),
            OperatorId.New(),
            GoalTestData.ReferenceTime);

        Assert.Throws<DomainRuleViolationException>(() => AccountAllocationEntry.Reserve(
            AccountAllocationEntryId.New(),
            ActivationId.New(),
            goal,
            limit,
            BrokerEnvironment.LIVE,
            "LONG_LIMIT_WHOLE_SHARE_MAX_NOTIONAL",
            GoalTestData.ReferenceTime));
    }

    [Fact]
    public void Paper_auto_reserves_an_exact_paper_allocation()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_AUTO);
        var binding = goal.ExecutionBinding!;
        var limit = AccountAllocationLimit.Create(
            new VersionReference("paper-account-limit", "1", "digest-paper-account-limit-1"),
            binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            new Money(25_000m, "USD"),
            OperatorId.New(),
            GoalTestData.ReferenceTime);

        var allocation = AccountAllocationEntry.Reserve(
            AccountAllocationEntryId.New(),
            ActivationId.New(),
            goal,
            limit,
            BrokerEnvironment.PAPER,
            "LONG_LIMIT_WHOLE_SHARE_MAX_NOTIONAL",
            GoalTestData.ReferenceTime);

        Assert.Equal(AccountAllocationState.ACTIVE, allocation.State);
        Assert.Equal(BrokerEnvironment.PAPER, allocation.Environment);
        Assert.Equal(goal.Id, allocation.GoalVersionId);
    }

    [Fact]
    public void Allocation_digests_ignore_equivalent_decimal_storage_scale()
    {
        var binding = GoalTestData.CreateExecutionBinding(BrokerEnvironment.PAPER);
        var reference = new VersionReference(
            "paper-account-limit",
            "1",
            "digest-paper-account-limit-1");
        var creator = OperatorId.New();
        var first = AccountAllocationLimit.Create(
            reference,
            binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            new Money(25_000m, "USD"),
            creator,
            GoalTestData.ReferenceTime);
        var stored = AccountAllocationLimit.Create(
            reference,
            binding.BrokerConnectionVersion,
            BrokerEnvironment.PAPER,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            new Money(25_000.000000000000m, "USD"),
            creator,
            GoalTestData.ReferenceTime);

        Assert.Equal(first.ContentDigest, stored.ContentDigest);
    }
}
