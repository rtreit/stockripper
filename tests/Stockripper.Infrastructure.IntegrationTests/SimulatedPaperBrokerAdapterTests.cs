using Stockripper.Application.Execution;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Infrastructure.Brokers;
using Stockripper.Infrastructure;
using Stockripper.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Stockripper.Infrastructure.IntegrationTests;

public sealed class SimulatedPaperBrokerAdapterTests
{
    [Fact]
    public void Simulated_registration_resolves_the_complete_paper_workflow()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_AUTO);
        var binding = goal.ExecutionBinding!;
        var broker = new SimulatedPaperBrokerOptions(binding.BrokerConnectionVersion,
            binding.EndpointIdentity, binding.ExpectedAccountIdentifier, 25_000m, "USD",
            [goal.DataPolicyVersion, goal.RiskPolicyVersion,
                binding.OrderPolicyVersion, binding.ReconciliationPolicyVersion]);
        var services = new ServiceCollection();
        services.AddStockripperInfrastructure(new StockripperInfrastructureOptions(
            "Host=127.0.0.1;Database=stockripper-test;Username=test;Password=test",
            new Uri("http://127.0.0.1:5081"), "test-token",
            TimeSpan.FromSeconds(1), 65_536,
            GoalTestData.DefaultPaperRiskPolicy, broker));
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<PaperReviewIntentService>());
        Assert.NotNull(provider.GetRequiredService<PaperReviewService>());
        Assert.NotNull(provider.GetRequiredService<PaperOrderService>());
        Assert.NotNull(provider.GetRequiredService<PaperReviewSessionService>());
        Assert.NotNull(provider.GetRequiredService<PaperExecutableSessionLifecycleService>());
        Assert.NotNull(provider.GetRequiredService<PaperPostFillSafetyService>());
        Assert.NotNull(provider.GetRequiredService<PaperCancellationService>());
        Assert.NotNull(provider.GetRequiredService<PaperReconciliationService>());
        Assert.NotNull(provider.GetRequiredService<IPaperOperatorReadModel>());
        Assert.NotNull(provider.GetRequiredService<ISimulatedPaperBroker>());
        Assert.IsType<SimulatedPaperBrokerAdapter>(
            provider.GetRequiredService<IPaperBrokerOrderAdapter>());
    }

    [Fact]
    public async Task Simulated_adapter_is_idempotent_and_reports_complete_paper_truth()
    {
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime);
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_AUTO);
        var binding = goal.ExecutionBinding!;
        var options = new SimulatedPaperBrokerOptions(binding.BrokerConnectionVersion,
            binding.EndpointIdentity, binding.ExpectedAccountIdentifier, 25_000m, "USD",
            [goal.DataPolicyVersion, goal.RiskPolicyVersion,
                binding.OrderPolicyVersion, binding.ReconciliationPolicyVersion]);
        var adapter = new SimulatedPaperBrokerAdapter(options, clock);
        var order = CreatePreparedOrder(goal, clock.GetUtcNow());

        var first = await ((IPaperBrokerOrderAdapter)adapter).SendAsync(order, default);
        var duplicate = await ((IPaperBrokerOrderAdapter)adapter).SendAsync(order, default);
        var acknowledged = order.Acknowledge(first.BrokerOrderId!,
            first.EventReference!, first.ObservedAt);
        clock.Advance(TimeSpan.FromSeconds(1));
        var update = await adapter.FillAsync(acknowledged, 99m);
        var snapshot = await ((IPaperBrokerReconciliationAdapter)adapter).ObserveAsync(
            order.AccountBinding, default);

        Assert.Equal(first, duplicate);
        Assert.Equal(BrokerEnvironment.PAPER, snapshot.ObservedBinding.Environment);
        Assert.Equal(OrderState.FILLED, Assert.Single(snapshot.Orders).State);
        Assert.Equal("simulated-fill-1", Assert.Single(snapshot.Fills).Identity);
        Assert.Equal(10m, Assert.Single(snapshot.Positions).Quantity);
        Assert.Equal(BrokerExecutionEventKind.FILLED, update.Kind);
    }

    private static OrderRecord CreatePreparedOrder(GoalVersion goal, DateTimeOffset now)
    {
        var intentId = ExecutionIntentId.New();
        var riskId = RiskEvaluationId.New();
        var binding = goal.ExecutionBinding!;
        var intent = ExecutionIntent.Create(intentId, DecisionId.New(), goal.Id,
            SessionId.New(), ExecutionMode.PAPER_AUTO,
            new IntentAccountBinding(binding.BrokerConnectionVersion,
                BrokerEnvironment.PAPER, binding.EndpointIdentity,
                binding.ExpectedAccountIdentifier),
            "AAPL", OrderSide.BUY, IntentOrderType.LIMIT, 10m, 100m,
            IntentTimeInForce.DAY, now.AddMinutes(5), riskId, now);
        var risk = RiskEvaluation.Create(riskId, intentId, intent.DecisionId,
            goal.Id, intent.SessionId, goal.RiskPolicyVersion,
            AccountAllocationEntryId.New(), ReconciliationId.New(),
            0m, 0m, 1_000m, 25_000m, "USD",
            [new RiskCheck(RiskCheckCode.CAPITAL_ALLOCATION, true,
                "The allocation check passed.")], now);
        var accepted = intent.ApplyRisk(risk);
        var reservation = ExposureReservation.Reserve(ExposureReservationId.New(),
            accepted, AccountAllocationEntryId.New(), "USD", now);
        var checks = Enum.GetValues<PreSubmitCheckCode>()
            .Select(code => new PreSubmitCheck(code, true,
                "The final check passed."))
            .ToArray();
        var evaluation = PreSubmitEvaluation.CreateAutomatic(
            PreSubmitEvaluationId.New(), accepted, reservation,
            ReconciliationId.New(), 0m, 0m, 25_000m, "USD", checks, now);
        return OrderRecord.Prepare(OrderId.New(), accepted, evaluation, now);
    }
}
