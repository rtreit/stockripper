using System.Globalization;
using System.Text.Json;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;

namespace Stockripper.Infrastructure.Persistence;

internal static class GoalDefinitionSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(GoalVersionDefinition definition) => JsonSerializer.Serialize(
        DefinitionDocument.FromDomain(definition),
        Options);

    public static GoalVersionDefinition Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<DefinitionDocument>(json, Options)
            ?? throw new InvalidDataException("The Goal definition document is missing.");
        return document.ToDomain();
    }

    private sealed record DefinitionDocument(
        string Mode,
        string Objective,
        string[] InstrumentUniverse,
        VersionDocument StrategyVersion,
        VersionDocument DataPolicyVersion,
        VersionDocument RiskPolicyVersion,
        decimal CapitalAllocationAmount,
        string CapitalAllocationCurrency,
        ScheduleDocument ExecutionSchedule,
        ScheduleDocument DecisionSchedule,
        string OutputCriteria,
        string EvaluationCriteria,
        ExecutionBindingDocument? ExecutionBinding)
    {
        public static DefinitionDocument FromDomain(GoalVersionDefinition definition) => new(
            definition.Mode.ToWireValue(),
            definition.Objective,
            [.. definition.InstrumentUniverse],
            VersionDocument.FromDomain(definition.StrategyVersion),
            VersionDocument.FromDomain(definition.DataPolicyVersion),
            VersionDocument.FromDomain(definition.RiskPolicyVersion),
            definition.CapitalAllocation.Amount,
            definition.CapitalAllocation.Currency,
            ScheduleDocument.FromDomain(definition.ExecutionSchedule),
            ScheduleDocument.FromDomain(definition.DecisionSchedule),
            definition.OutputCriteria,
            definition.EvaluationCriteria,
            definition.ExecutionBinding is null
                ? null
                : ExecutionBindingDocument.FromDomain(definition.ExecutionBinding));

        public GoalVersionDefinition ToDomain() => new(
            ExecutionModeRules.Parse(Mode),
            Objective,
            InstrumentUniverse,
            StrategyVersion.ToDomain(),
            DataPolicyVersion.ToDomain(),
            RiskPolicyVersion.ToDomain(),
            new Money(CapitalAllocationAmount, CapitalAllocationCurrency),
            ExecutionSchedule.ToDomain(),
            DecisionSchedule.ToDomain(),
            OutputCriteria,
            EvaluationCriteria,
            ExecutionBinding?.ToDomain());
    }

    private sealed record VersionDocument(string Identifier, string Version, string Digest)
    {
        public static VersionDocument FromDomain(VersionReference value) => new(
            value.Identifier,
            value.Version,
            value.Digest);

        public VersionReference ToDomain() => new(Identifier, Version, Digest);
    }

    private sealed record ScheduleDocument(
        string TimeZoneId,
        string StartTime,
        string EndTime,
        int[] ActiveDays)
    {
        public static ScheduleDocument FromDomain(ScheduleDefinition value) => new(
            value.TimeZoneId,
            value.StartTime.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            value.EndTime.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            [.. value.ActiveDays.Select(day => (int)day)]);

        public ScheduleDefinition ToDomain() => new(
            TimeZoneId,
            TimeOnly.ParseExact(StartTime, "HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            TimeOnly.ParseExact(EndTime, "HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            ActiveDays.Select(value => (DayOfWeek)value));
    }

    private sealed record ExecutionBindingDocument(
        VersionDocument BrokerConnectionVersion,
        string ExpectedEnvironment,
        string EndpointIdentity,
        string ExpectedAccountIdentifier,
        string[] RequiredCapabilities,
        VersionDocument OrderPolicyVersion,
        VersionDocument ReconciliationPolicyVersion)
    {
        public static ExecutionBindingDocument FromDomain(ExecutionBinding value) => new(
            VersionDocument.FromDomain(value.BrokerConnectionVersion),
            value.ExpectedEnvironment.ToString(),
            value.EndpointIdentity,
            value.ExpectedAccountIdentifier,
            [.. value.RequiredCapabilities],
            VersionDocument.FromDomain(value.OrderPolicyVersion),
            VersionDocument.FromDomain(value.ReconciliationPolicyVersion));

        public ExecutionBinding ToDomain() => new(
            BrokerConnectionVersion.ToDomain(),
            Enum.Parse<BrokerEnvironment>(ExpectedEnvironment, ignoreCase: false),
            EndpointIdentity,
            ExpectedAccountIdentifier,
            RequiredCapabilities,
            OrderPolicyVersion.ToDomain(),
            ReconciliationPolicyVersion.ToDomain());
    }
}
