using Stockripper.Application.Brokers;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Risk;

namespace Stockripper.Testing;

public static class GoalTestData
{
    public static readonly DateTimeOffset ReferenceTime = new(
        2026,
        8,
        18,
        16,
        0,
        0,
        TimeSpan.Zero);

    public static readonly PaperRiskPolicy DefaultPaperRiskPolicy = PaperRiskPolicy.Create(
        "paper-risk-policy",
        "1",
        ["AAPL", "MSFT"],
        5_000m,
        8_000m,
        10_000m,
        1m,
        1_000m,
        TimeSpan.FromMinutes(1),
        5,
        0,
        TimeSpan.FromMinutes(2),
        new OperatorId(Guid.Parse("30000000-0000-0000-0000-000000000001")),
        ReferenceTime);

    public static GoalVersion CreateGoalVersion(
        ExecutionMode mode,
        string objective = "Preserve capital while testing deterministic decisions.",
        ExecutionBinding? executionBinding = null,
        bool includeDefaultBinding = true)
    {
        return GoalVersion.Publish(
            new GoalVersionId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
            new GoalId(Guid.Parse("20000000-0000-0000-0000-000000000001")),
            1,
            CreateDefinition(mode, objective, executionBinding, includeDefaultBinding),
            new OperatorId(Guid.Parse("30000000-0000-0000-0000-000000000001")),
            ReferenceTime);
    }

    public static GoalVersionDefinition CreateDefinition(
        ExecutionMode mode,
        string objective = "Preserve capital while testing deterministic decisions.",
        ExecutionBinding? executionBinding = null,
        bool includeDefaultBinding = true)
    {
        var requiredEnvironment = mode.RequiredBrokerEnvironment();
        var binding = executionBinding ?? (!includeDefaultBinding || requiredEnvironment is null
            ? null
            : CreateExecutionBinding(requiredEnvironment.Value));

        return new GoalVersionDefinition(
            mode,
            objective,
            ["AAPL", "MSFT"],
            Version("strategy", "1"),
            Version("data-policy", "1"),
            DefaultPaperRiskPolicy.Version.Reference,
            new Money(10_000m, "USD"),
            Schedule(),
            Schedule(),
            "Record every deterministic Decision.",
            "Compare replay output with the expected Decision set.",
            binding);
    }

    public static ExecutionBinding CreateExecutionBinding(BrokerEnvironment environment) => new(
        Version("broker-connection", "1"),
        environment,
        Endpoint(environment),
        Account(environment),
        ["EQUITY_LIMIT_DAY", "ORDER_HISTORY", "POSITIONS"],
        Version("order-policy", "1"),
        Version("reconciliation-policy", "1"));

    public static GoalActivation CreateActivation(
        GoalVersion goalVersion,
        DateTimeOffset? expiryTime = null) => GoalActivation.Activate(
        new ActivationId(Guid.Parse("40000000-0000-0000-0000-000000000001")),
        goalVersion.Id,
        new OperatorId(Guid.Parse("30000000-0000-0000-0000-000000000001")),
        "operator-test-evidence",
        ReferenceTime,
        expiryTime);

    public static BrokerConnectionObservation CreateObservation(GoalVersion goalVersion)
    {
        var binding = goalVersion.ExecutionBinding
            ?? throw new InvalidOperationException("The test Goal version is not executable.");

        return new BrokerConnectionObservation(
            binding.ExpectedEnvironment,
            binding.EndpointIdentity,
            binding.ExpectedAccountIdentifier,
            binding.RequiredCapabilities,
            ReferenceTime);
    }

    public static DataSnapshot CreateDataSnapshot(
        GoalVersion goalVersion,
        string payload = "{\"AAPL\":{\"close\":230.12},\"MSFT\":{\"close\":510.09}}",
        VersionReference? dataPolicyVersion = null) => DataSnapshot.Create(
        DataSnapshotId.New(),
        Version("fixture-data-set", "1"),
        dataPolicyVersion ?? goalVersion.DataPolicyVersion,
        "stock-bars-v1",
        ReferenceTime.AddHours(-8),
        ReferenceTime.AddHours(-1),
        ReferenceTime,
        [new DataSourceObservation("retained-fixture", "1", ReferenceTime.AddHours(-1))],
        System.Text.Encoding.UTF8.GetBytes(payload));

    public static string Endpoint(BrokerEnvironment environment) => environment switch
    {
        BrokerEnvironment.PAPER => "broker-paper-endpoint-v1",
        BrokerEnvironment.LIVE => "broker-live-endpoint-v1",
        _ => throw new ArgumentOutOfRangeException(nameof(environment))
    };

    public static string Account(BrokerEnvironment environment) => environment switch
    {
        BrokerEnvironment.PAPER => "paper-account-test-001",
        BrokerEnvironment.LIVE => "live-account-test-001",
        _ => throw new ArgumentOutOfRangeException(nameof(environment))
    };

    private static VersionReference Version(string identifier, string version) => new(
        identifier,
        version,
        $"digest-{identifier}-{version}");

    private static ScheduleDefinition Schedule() => new(
        "America/New_York",
        new TimeOnly(9, 30),
        new TimeOnly(16, 0),
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);
}
