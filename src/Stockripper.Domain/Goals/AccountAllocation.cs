using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Domain.Goals;

public enum AccountAllocationState
{
    ACTIVE,
    RELEASED
}

public sealed record AccountAllocationLimit
{
    private AccountAllocationLimit(
        VersionReference reference,
        VersionReference brokerConnectionVersion,
        BrokerEnvironment environment,
        string endpointIdentity,
        string accountIdentifier,
        Money maximumAllocation,
        OperatorId creatorId,
        DateTimeOffset createdAt,
        string contentDigest)
    {
        Reference = reference;
        BrokerConnectionVersion = brokerConnectionVersion;
        Environment = environment;
        EndpointIdentity = endpointIdentity;
        AccountIdentifier = accountIdentifier;
        MaximumAllocation = maximumAllocation;
        CreatorId = creatorId;
        CreatedAt = createdAt;
        ContentDigest = contentDigest;
    }

    public VersionReference Reference { get; }
    public VersionReference BrokerConnectionVersion { get; }
    public BrokerEnvironment Environment { get; }
    public string EndpointIdentity { get; }
    public string AccountIdentifier { get; }
    public Money MaximumAllocation { get; }
    public OperatorId CreatorId { get; }
    public DateTimeOffset CreatedAt { get; }
    public string ContentDigest { get; }

    public static AccountAllocationLimit Create(
        VersionReference reference,
        VersionReference brokerConnectionVersion,
        BrokerEnvironment environment,
        string endpointIdentity,
        string accountIdentifier,
        Money maximumAllocation,
        OperatorId creatorId,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(brokerConnectionVersion);
        ArgumentNullException.ThrowIfNull(maximumAllocation);
        if (environment != BrokerEnvironment.PAPER)
        {
            throw new DomainRuleViolationException("Phase 1 account allocation must use PAPER.");
        }

        var endpoint = RequireText(endpointIdentity, "The allocation endpoint identity is required.");
        var account = RequireText(accountIdentifier, "The allocation account identifier is required.");
        var time = UtcTimestamp.Require(createdAt, nameof(createdAt));
        var digest = ComputeLimitDigest(
            reference,
            brokerConnectionVersion,
            environment,
            endpoint,
            account,
            maximumAllocation,
            creatorId,
            time);
        return new AccountAllocationLimit(
            reference,
            brokerConnectionVersion,
            environment,
            endpoint,
            account,
            maximumAllocation,
            creatorId,
            time,
            digest);
    }

    public bool HasValidContentDigest() => string.Equals(
        ContentDigest,
        ComputeLimitDigest(
            Reference,
            BrokerConnectionVersion,
            Environment,
            EndpointIdentity,
            AccountIdentifier,
            MaximumAllocation,
            CreatorId,
            CreatedAt),
        StringComparison.Ordinal);

    private static string ComputeLimitDigest(
        VersionReference reference,
        VersionReference brokerConnectionVersion,
        BrokerEnvironment environment,
        string endpointIdentity,
        string accountIdentifier,
        Money maximumAllocation,
        OperatorId creatorId,
        DateTimeOffset createdAt) => Hash(new
        {
            Schema = "stockripper-account-allocation-limit-v1",
            Reference = VersionValue(reference),
            BrokerConnectionVersion = VersionValue(brokerConnectionVersion),
            Environment = environment.ToString(),
            EndpointIdentity = endpointIdentity,
            AccountIdentifier = accountIdentifier,
            MaximumAllocation = MoneyValue(maximumAllocation),
            CreatorId = creatorId.Value,
            CreatedAt = createdAt.ToString("O")
        });

    internal static object VersionValue(VersionReference value) => new
    {
        value.Identifier,
        value.Version,
        value.Digest
    };

    internal static object MoneyValue(Money value) => new
    {
        Amount = value.Amount.ToString("G29", CultureInfo.InvariantCulture),
        value.Currency
    };

    internal static string Hash(object value) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    internal static string RequireText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException(message);
        }

        return value;
    }
}

public sealed record AccountAllocationEntry
{
    private AccountAllocationEntry(
        AccountAllocationEntryId id,
        ActivationId activationId,
        GoalVersionId goalVersionId,
        VersionReference accountLimitVersion,
        VersionReference brokerConnectionVersion,
        BrokerEnvironment environment,
        string endpointIdentity,
        string accountIdentifier,
        Money allocation,
        string exposureMethodIdentifier,
        DateTimeOffset effectiveStart,
        DateTimeOffset? effectiveEnd,
        AccountAllocationState state,
        string contentDigest)
    {
        Id = id;
        ActivationId = activationId;
        GoalVersionId = goalVersionId;
        AccountLimitVersion = accountLimitVersion;
        BrokerConnectionVersion = brokerConnectionVersion;
        Environment = environment;
        EndpointIdentity = endpointIdentity;
        AccountIdentifier = accountIdentifier;
        Allocation = allocation;
        ExposureMethodIdentifier = exposureMethodIdentifier;
        EffectiveStart = effectiveStart;
        EffectiveEnd = effectiveEnd;
        State = state;
        ContentDigest = contentDigest;
    }

    public AccountAllocationEntryId Id { get; }
    public ActivationId ActivationId { get; }
    public GoalVersionId GoalVersionId { get; }
    public VersionReference AccountLimitVersion { get; }
    public VersionReference BrokerConnectionVersion { get; }
    public BrokerEnvironment Environment { get; }
    public string EndpointIdentity { get; }
    public string AccountIdentifier { get; }
    public Money Allocation { get; }
    public string ExposureMethodIdentifier { get; }
    public DateTimeOffset EffectiveStart { get; }
    public DateTimeOffset? EffectiveEnd { get; private init; }
    public AccountAllocationState State { get; private init; }
    public string ContentDigest { get; }

    public static AccountAllocationEntry Reserve(
        AccountAllocationEntryId id,
        ActivationId activationId,
        GoalVersion goalVersion,
        AccountAllocationLimit limit,
        BrokerEnvironment observedEnvironment,
        string exposureMethodIdentifier,
        DateTimeOffset effectiveStart)
    {
        ArgumentNullException.ThrowIfNull(goalVersion);
        ArgumentNullException.ThrowIfNull(limit);
        if (activationId.Value == Guid.Empty
            || goalVersion.Mode is not (ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO)
            || goalVersion.ExecutionBinding is null
            || observedEnvironment != BrokerEnvironment.PAPER)
        {
            throw new DomainRuleViolationException("An allocation entry needs paper authority.");
        }

        var binding = goalVersion.ExecutionBinding;
        if (binding.ExpectedEnvironment != BrokerEnvironment.PAPER
            || binding.BrokerConnectionVersion != limit.BrokerConnectionVersion
            || !string.Equals(binding.EndpointIdentity, limit.EndpointIdentity, StringComparison.Ordinal)
            || !string.Equals(binding.ExpectedAccountIdentifier, limit.AccountIdentifier, StringComparison.Ordinal)
            || !string.Equals(
                goalVersion.CapitalAllocation.Currency,
                limit.MaximumAllocation.Currency,
                StringComparison.Ordinal))
        {
            throw new DomainRuleViolationException("The account allocation binding is inconsistent.");
        }

        var start = UtcTimestamp.Require(effectiveStart, nameof(effectiveStart));
        var method = AccountAllocationLimit.RequireText(
            exposureMethodIdentifier,
            "The exposure method identifier is required.");
        var digest = ComputeEntryDigest(
            id,
            activationId,
            goalVersion.Id,
            limit.Reference,
            binding.BrokerConnectionVersion,
            binding.ExpectedEnvironment,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            goalVersion.CapitalAllocation,
            method,
            start);
        return new AccountAllocationEntry(
            id,
            activationId,
            goalVersion.Id,
            limit.Reference,
            binding.BrokerConnectionVersion,
            binding.ExpectedEnvironment,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            goalVersion.CapitalAllocation,
            method,
            start,
            null,
            AccountAllocationState.ACTIVE,
            digest);
    }

    public AccountAllocationEntry Release(DateTimeOffset releasedAt)
    {
        var time = UtcTimestamp.Require(releasedAt, nameof(releasedAt));
        if (State != AccountAllocationState.ACTIVE || time < EffectiveStart)
        {
            throw new DomainRuleViolationException("The account allocation release is invalid.");
        }

        return this with { State = AccountAllocationState.RELEASED, EffectiveEnd = time };
    }

    public static AccountAllocationEntry Restore(
        AccountAllocationEntryId id,
        ActivationId activationId,
        GoalVersionId goalVersionId,
        VersionReference accountLimitVersion,
        VersionReference brokerConnectionVersion,
        BrokerEnvironment environment,
        string endpointIdentity,
        string accountIdentifier,
        Money allocation,
        string exposureMethodIdentifier,
        DateTimeOffset effectiveStart,
        DateTimeOffset? effectiveEnd,
        AccountAllocationState state,
        string contentDigest)
    {
        var start = UtcTimestamp.Require(effectiveStart, nameof(effectiveStart));
        DateTimeOffset? end = effectiveEnd is null
            ? null
            : UtcTimestamp.Require(effectiveEnd.Value, nameof(effectiveEnd));
        if (environment != BrokerEnvironment.PAPER
            || (state == AccountAllocationState.ACTIVE && end is not null)
            || (state == AccountAllocationState.RELEASED && end is null)
            || (end is not null && end.Value < start))
        {
            throw new DomainRuleViolationException("The stored account allocation state is invalid.");
        }

        var entry = new AccountAllocationEntry(
            id,
            activationId,
            goalVersionId,
            accountLimitVersion,
            brokerConnectionVersion,
            environment,
            AccountAllocationLimit.RequireText(
                endpointIdentity,
                "The allocation endpoint identity is required."),
            AccountAllocationLimit.RequireText(
                accountIdentifier,
                "The allocation account identifier is required."),
            allocation,
            AccountAllocationLimit.RequireText(
                exposureMethodIdentifier,
                "The exposure method identifier is required."),
            start,
            end,
            state,
            contentDigest);
        if (!entry.HasValidContentDigest())
        {
            throw new InvalidDataException("The stored account allocation digest is invalid.");
        }

        return entry;
    }

    public bool HasValidContentDigest() => string.Equals(
        ContentDigest,
        ComputeEntryDigest(
            Id,
            ActivationId,
            GoalVersionId,
            AccountLimitVersion,
            BrokerConnectionVersion,
            Environment,
            EndpointIdentity,
            AccountIdentifier,
            Allocation,
            ExposureMethodIdentifier,
            EffectiveStart),
        StringComparison.Ordinal);

    private static string ComputeEntryDigest(
        AccountAllocationEntryId id,
        ActivationId activationId,
        GoalVersionId goalVersionId,
        VersionReference accountLimitVersion,
        VersionReference brokerConnectionVersion,
        BrokerEnvironment environment,
        string endpointIdentity,
        string accountIdentifier,
        Money allocation,
        string exposureMethodIdentifier,
        DateTimeOffset effectiveStart) => AccountAllocationLimit.Hash(new
        {
            Schema = "stockripper-account-allocation-entry-v1",
            Id = id.Value,
            ActivationId = activationId.Value,
            GoalVersionId = goalVersionId.Value,
            AccountLimitVersion = AccountAllocationLimit.VersionValue(accountLimitVersion),
            BrokerConnectionVersion = AccountAllocationLimit.VersionValue(brokerConnectionVersion),
            Environment = environment.ToString(),
            EndpointIdentity = endpointIdentity,
            AccountIdentifier = accountIdentifier,
            Allocation = AccountAllocationLimit.MoneyValue(allocation),
            ExposureMethodIdentifier = exposureMethodIdentifier,
            EffectiveStart = effectiveStart.ToString("O")
        });
}
