using System.Collections.Immutable;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;

namespace Stockripper.Domain.Goals;

public sealed record GoalVersionDefinition
{
    public GoalVersionDefinition(
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
        ExecutionBinding? executionBinding)
    {
        ArgumentNullException.ThrowIfNull(instrumentUniverse);
        ArgumentNullException.ThrowIfNull(strategyVersion);
        ArgumentNullException.ThrowIfNull(dataPolicyVersion);
        ArgumentNullException.ThrowIfNull(riskPolicyVersion);
        ArgumentNullException.ThrowIfNull(capitalAllocation);
        ArgumentNullException.ThrowIfNull(executionSchedule);
        ArgumentNullException.ThrowIfNull(decisionSchedule);

        Mode = mode;
        Objective = RequireText(objective, "The Goal objective is required.");
        InstrumentUniverse = instrumentUniverse
            .Select(instrument => RequireText(instrument, "An instrument identifier is invalid."))
            .ToImmutableArray();
        StrategyVersion = strategyVersion;
        DataPolicyVersion = dataPolicyVersion;
        RiskPolicyVersion = riskPolicyVersion;
        CapitalAllocation = capitalAllocation;
        ExecutionSchedule = executionSchedule;
        DecisionSchedule = decisionSchedule;
        OutputCriteria = RequireText(outputCriteria, "The output criteria are required.");
        EvaluationCriteria = RequireText(evaluationCriteria, "The evaluation criteria are required.");
        ExecutionBinding = executionBinding;

        if (InstrumentUniverse.IsEmpty
            || InstrumentUniverse.Distinct(StringComparer.Ordinal).Count() != InstrumentUniverse.Length)
        {
            throw new DomainRuleViolationException("The instrument universe must contain unique identifiers.");
        }
    }

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

    public ExecutionBinding? ExecutionBinding { get; }

    private static string RequireText(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new DomainRuleViolationException(message);
        }

        return value;
    }
}
