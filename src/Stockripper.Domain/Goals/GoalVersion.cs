using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Domain.Goals;

public sealed record GoalVersion
{
    private const string DigestSchema = "stockripper-goal-version-v1";

    private GoalVersion(
        GoalVersionId id,
        GoalId goalId,
        int versionNumber,
        ExecutionMode mode,
        string objective,
        ImmutableArray<string> instrumentUniverse,
        VersionReference strategyVersion,
        VersionReference dataPolicyVersion,
        VersionReference riskPolicyVersion,
        Money capitalAllocation,
        ScheduleDefinition executionSchedule,
        ScheduleDefinition decisionSchedule,
        string outputCriteria,
        string evaluationCriteria,
        OperatorId creatorId,
        DateTimeOffset createdAt,
        ExecutionBinding? executionBinding,
        string contentDigest)
    {
        Id = id;
        GoalId = goalId;
        VersionNumber = versionNumber;
        Mode = mode;
        Objective = objective;
        InstrumentUniverse = instrumentUniverse;
        StrategyVersion = strategyVersion;
        DataPolicyVersion = dataPolicyVersion;
        RiskPolicyVersion = riskPolicyVersion;
        CapitalAllocation = capitalAllocation;
        ExecutionSchedule = executionSchedule;
        DecisionSchedule = decisionSchedule;
        OutputCriteria = outputCriteria;
        EvaluationCriteria = evaluationCriteria;
        CreatorId = creatorId;
        CreatedAt = createdAt;
        ExecutionBinding = executionBinding;
        ContentDigest = contentDigest;
    }

    public GoalVersionId Id { get; }

    public GoalId GoalId { get; }

    public int VersionNumber { get; }

    public ExecutionMode Mode { get; }

    public string Objective { get; }

    public ImmutableArray<string> InstrumentUniverse { get; }

    public VersionReference StrategyVersion { get; }

    public VersionReference DataPolicyVersion { get; }

    public VersionReference RiskPolicyVersion { get; }

    public Money CapitalAllocation { get; }

    public ScheduleDefinition ExecutionSchedule { get; }

    public ScheduleDefinition DecisionSchedule { get; }

    public string OutputCriteria { get; }

    public string EvaluationCriteria { get; }

    public OperatorId CreatorId { get; }

    public DateTimeOffset CreatedAt { get; }

    public ExecutionBinding? ExecutionBinding { get; }

    public string ContentDigest { get; }

    public static GoalVersion Publish(
        GoalVersionId id,
        GoalId goalId,
        int versionNumber,
        GoalVersionDefinition definition,
        OperatorId creatorId,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return Publish(
            id,
            goalId,
            versionNumber,
            definition.Mode,
            definition.Objective,
            definition.InstrumentUniverse,
            definition.StrategyVersion,
            definition.DataPolicyVersion,
            definition.RiskPolicyVersion,
            definition.CapitalAllocation,
            definition.ExecutionSchedule,
            definition.DecisionSchedule,
            definition.OutputCriteria,
            definition.EvaluationCriteria,
            creatorId,
            createdAt,
            definition.ExecutionBinding);
    }

    public static GoalVersion Publish(
        GoalVersionId id,
        GoalId goalId,
        int versionNumber,
        ExecutionMode mode,
        string objective,
        IEnumerable<string> instrumentUniverse,
        VersionReference strategyVersion,
        VersionReference dataPolicyVersion,
        VersionReference riskPolicyVersion,
        Money capitalAllocation,
        ScheduleDefinition executionSchedule,
        ScheduleDefinition decisionSchedule,
        string outputCriteria,
        string evaluationCriteria,
        OperatorId creatorId,
        DateTimeOffset createdAt,
        ExecutionBinding? executionBinding)
    {
        ArgumentNullException.ThrowIfNull(instrumentUniverse);
        ArgumentNullException.ThrowIfNull(strategyVersion);
        ArgumentNullException.ThrowIfNull(dataPolicyVersion);
        ArgumentNullException.ThrowIfNull(riskPolicyVersion);
        ArgumentNullException.ThrowIfNull(capitalAllocation);
        ArgumentNullException.ThrowIfNull(executionSchedule);
        ArgumentNullException.ThrowIfNull(decisionSchedule);

        if (id.Value == Guid.Empty || goalId.Value == Guid.Empty || creatorId.Value == Guid.Empty)
        {
            throw new DomainRuleViolationException("The Goal version contains an empty stable identifier.");
        }

        if (versionNumber <= 0)
        {
            throw new DomainRuleViolationException("The Goal version number must be greater than zero.");
        }

        var checkedObjective = RequireText(objective, "The Goal objective is required.");
        var checkedOutput = RequireText(outputCriteria, "The output criteria are required.");
        var checkedEvaluation = RequireText(evaluationCriteria, "The evaluation criteria are required.");
        var checkedCreatedAt = UtcTimestamp.Require(createdAt, nameof(createdAt));
        var checkedUniverse = instrumentUniverse
            .Select(instrument => RequireText(instrument, "An instrument identifier is invalid."))
            .ToImmutableArray();

        if (checkedUniverse.IsEmpty || checkedUniverse.Distinct(StringComparer.Ordinal).Count() != checkedUniverse.Length)
        {
            throw new DomainRuleViolationException("The instrument universe must contain unique identifiers.");
        }

        ValidateExecutionBinding(mode, executionBinding);

        var digest = ComputeDigest(
            id,
            goalId,
            versionNumber,
            mode,
            checkedObjective,
            checkedUniverse,
            strategyVersion,
            dataPolicyVersion,
            riskPolicyVersion,
            capitalAllocation,
            executionSchedule,
            decisionSchedule,
            checkedOutput,
            checkedEvaluation,
            creatorId,
            checkedCreatedAt,
            executionBinding);

        return new GoalVersion(
            id,
            goalId,
            versionNumber,
            mode,
            checkedObjective,
            checkedUniverse,
            strategyVersion,
            dataPolicyVersion,
            riskPolicyVersion,
            capitalAllocation,
            executionSchedule,
            decisionSchedule,
            checkedOutput,
            checkedEvaluation,
            creatorId,
            checkedCreatedAt,
            executionBinding,
            digest);
    }

    public bool HasValidContentDigest() => string.Equals(
        ContentDigest,
        ComputeDigest(
            Id,
            GoalId,
            VersionNumber,
            Mode,
            Objective,
            InstrumentUniverse,
            StrategyVersion,
            DataPolicyVersion,
            RiskPolicyVersion,
            CapitalAllocation,
            ExecutionSchedule,
            DecisionSchedule,
            OutputCriteria,
            EvaluationCriteria,
            CreatorId,
            CreatedAt,
            ExecutionBinding),
        StringComparison.Ordinal);

    private static void ValidateExecutionBinding(ExecutionMode mode, ExecutionBinding? binding)
    {
        if (mode.IsExecutable() && binding is null)
        {
            throw new DomainRuleViolationException("An executable Goal version needs an execution binding.");
        }

        if (!mode.IsExecutable() && binding is not null)
        {
            throw new DomainRuleViolationException("A read-only Goal version cannot contain an execution binding.");
        }

        if (binding is not null && binding.ExpectedEnvironment != mode.RequiredBrokerEnvironment())
        {
            throw new DomainRuleViolationException("The Broker environment does not match the execution mode.");
        }
    }

    private static string ComputeDigest(
        GoalVersionId id,
        GoalId goalId,
        int versionNumber,
        ExecutionMode mode,
        string objective,
        ImmutableArray<string> instrumentUniverse,
        VersionReference strategyVersion,
        VersionReference dataPolicyVersion,
        VersionReference riskPolicyVersion,
        Money capitalAllocation,
        ScheduleDefinition executionSchedule,
        ScheduleDefinition decisionSchedule,
        string outputCriteria,
        string evaluationCriteria,
        OperatorId creatorId,
        DateTimeOffset createdAt,
        ExecutionBinding? executionBinding)
    {
        var canonicalValue = new
        {
            Schema = DigestSchema,
            Id = id.Value,
            GoalId = goalId.Value,
            VersionNumber = versionNumber,
            Mode = mode.ToWireValue(),
            Objective = objective,
            InstrumentUniverse = instrumentUniverse,
            StrategyVersion = ToDigestValue(strategyVersion),
            DataPolicyVersion = ToDigestValue(dataPolicyVersion),
            RiskPolicyVersion = ToDigestValue(riskPolicyVersion),
            CapitalAllocation = new { capitalAllocation.Amount, capitalAllocation.Currency },
            ExecutionSchedule = ToDigestValue(executionSchedule),
            DecisionSchedule = ToDigestValue(decisionSchedule),
            OutputCriteria = outputCriteria,
            EvaluationCriteria = evaluationCriteria,
            CreatorId = creatorId.Value,
            CreatedAt = createdAt.ToString("O"),
            ExecutionBinding = executionBinding is null ? null : new
            {
                BrokerConnectionVersion = ToDigestValue(executionBinding.BrokerConnectionVersion),
                Environment = executionBinding.ExpectedEnvironment.ToString(),
                executionBinding.EndpointIdentity,
                executionBinding.ExpectedAccountIdentifier,
                executionBinding.RequiredCapabilities,
                OrderPolicyVersion = ToDigestValue(executionBinding.OrderPolicyVersion),
                ReconciliationPolicyVersion = ToDigestValue(executionBinding.ReconciliationPolicyVersion)
            }
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonicalValue);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static object ToDigestValue(VersionReference value) => new
    {
        value.Identifier,
        value.Version,
        value.Digest
    };

    private static object ToDigestValue(ScheduleDefinition value) => new
    {
        value.TimeZoneId,
        StartTime = value.StartTime.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        EndTime = value.EndTime.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        ActiveDays = value.ActiveDays.Select(day => day.ToString())
    };

    private static string RequireText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException(message);
        }

        return value;
    }
}
