using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Risk;
using Stockripper.Infrastructure.Brokers;

namespace Stockripper.Host.Runtime;

public static class PaperMvpConfiguration
{
    public static readonly OperatorId LocalOperator = new(
        Guid.Parse("00000000-0000-0000-0000-000000000003"));
    public static readonly VersionReference StrategyVersion = new(
        "paper-simulated-strategy", "1", "sha256:paper-simulated-strategy-v1");
    public static readonly VersionReference DataPolicyVersion = new(
        "paper-simulated-data-policy", "1", "sha256:paper-simulated-data-policy-v1");
    public static readonly VersionReference BrokerConnectionVersion = new(
        "paper-simulated-connection", "1", "sha256:paper-simulated-connection-v1");
    public static readonly VersionReference OrderPolicyVersion = new(
        "paper-limit-day-order-policy", "1", "sha256:paper-limit-day-order-policy-v1");
    public static readonly VersionReference ReconciliationPolicyVersion = new(
        "paper-complete-reconciliation-policy", "1",
        "sha256:paper-complete-reconciliation-policy-v1");
    public static readonly PaperRiskPolicy RiskPolicy = PaperRiskPolicy.Create(
        "paper-mvp-risk-policy",
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
        LocalOperator,
        new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));
    public static readonly ExecutionBinding ExecutionBinding = new(
        BrokerConnectionVersion,
        BrokerEnvironment.PAPER,
        "simulated://stockripper/paper/v1",
        "SIMULATED-PAPER-001",
        ["EQUITY_LIMIT_DAY", "ORDER_HISTORY", "POSITIONS"],
        OrderPolicyVersion,
        ReconciliationPolicyVersion);
    public static readonly SimulatedPaperBrokerOptions BrokerOptions = new(
        BrokerConnectionVersion,
        ExecutionBinding.EndpointIdentity,
        ExecutionBinding.ExpectedAccountIdentifier,
        25_000m,
        "USD",
        [
            DataPolicyVersion,
            RiskPolicy.Version.Reference,
            OrderPolicyVersion,
            ReconciliationPolicyVersion
        ]);
}
