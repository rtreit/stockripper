using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Stockripper.Application.Allocations;
using Stockripper.Application.Audit;
using Stockripper.Application.Brokers;
using Stockripper.Application.Data;
using Stockripper.Application.Decisions;
using Stockripper.Application.Execution;
using Stockripper.Application.Goals;
using Stockripper.Application.Replay;
using Stockripper.Application.Research;
using Stockripper.Application.Sessions;
using Stockripper.Infrastructure.Audit;
using Stockripper.Infrastructure.Brokers;
using Stockripper.Infrastructure.Persistence;
using Stockripper.Infrastructure.Strategy;
using Stockripper.Strategy.Contracts.V1;
using Stockripper.Domain.Risk;

namespace Stockripper.Infrastructure;

public sealed record StockripperInfrastructureOptions(
    string ConnectionString,
    Uri StrategyAddress,
    string StrategyAccessToken,
    TimeSpan StrategyDeadline,
    int MaximumStrategyResponseBytes,
    PaperRiskPolicy? PaperRiskPolicy = null,
    SimulatedPaperBrokerOptions? SimulatedPaperBroker = null);

public static class InfrastructureServiceRegistration
{
    public static IServiceCollection AddStockripperInfrastructure(
        this IServiceCollection services,
        StockripperInfrastructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString)
            || !options.StrategyAddress.IsAbsoluteUri
            || !options.StrategyAddress.IsLoopback
            || string.IsNullOrWhiteSpace(options.StrategyAccessToken))
        {
            throw new InvalidOperationException("The Stockripper runtime configuration is invalid.");
        }

        services.AddSingleton(_ => NpgsqlDataSource.Create(options.ConnectionString));
        services.AddSingleton(_ => GrpcChannel.ForAddress(options.StrategyAddress));
        services.AddSingleton(serviceProvider => new StrategyRuntime.StrategyRuntimeClient(
            serviceProvider.GetRequiredService<GrpcChannel>()));
        services.AddSingleton(new GrpcStrategyRuntimeOptions(
            options.StrategyDeadline,
            options.MaximumStrategyResponseBytes,
            options.StrategyAccessToken));
        services.AddSingleton<TimeProvider>(TimeProvider.System);

        services.AddSingleton<IGoalPersistence, PostgresGoalPersistence>();
        services.AddSingleton<IGoalActivationPersistence, PostgresGoalActivationPersistence>();
        services.AddSingleton<IAccountAllocationPersistence, PostgresAccountAllocationPersistence>();
        services.AddSingleton<IDataSnapshotPersistence, PostgresDataSnapshotPersistence>();
        services.AddSingleton<IDecisionPersistence, PostgresDecisionPersistence>();
        services.AddSingleton<IReplayPersistence, PostgresReplayPersistence>();
        services.AddSingleton<IResearchPersistence, PostgresResearchPersistence>();
        services.AddSingleton<PostgresSessionPersistence>();
        services.AddSingleton<ISessionPersistence>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresSessionPersistence>());
        services.AddSingleton<IExecutableSessionRecoveryPersistence>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresSessionPersistence>());
        services.AddSingleton<IExecutablePreflightPersistence, PostgresExecutablePreflightPersistence>();
        services.AddSingleton<PostgresPaperReviewIntentPersistence>();
        services.AddSingleton<IPaperReviewIntentPersistence>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresPaperReviewIntentPersistence>());
        services.AddSingleton<IPaperReviewExpiryPersistence>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresPaperReviewIntentPersistence>());
        services.AddSingleton<IExecutionSafetyPersistence, PostgresExecutionSafetyPersistence>();
        services.AddSingleton<IPaperOrderPersistence, PostgresPaperOrderPersistence>();
        services.AddSingleton<IPaperCancellationPersistence, PostgresPaperCancellationPersistence>();
        services.AddSingleton<IPaperBrokerEventPersistence, PostgresPaperBrokerEventPersistence>();
        services.AddSingleton<IPaperPostFillSafetyPersistence, PostgresPaperPostFillSafetyPersistence>();
        services.AddSingleton<IPaperReconciliationPersistence,
            PostgresPaperReconciliationPersistence>();
        services.AddSingleton<IPaperOperatorReadModel, PostgresPaperOperatorReadModel>();
        services.AddSingleton<IAuditIntegrityVerifier, PostgresAuditIntegrityVerifier>();
        services.AddSingleton<PaperSubmissionInterlock>();
        services.AddSingleton<IPaperSubmissionInterlock>(serviceProvider =>
            serviceProvider.GetRequiredService<PaperSubmissionInterlock>());
        services.AddSingleton<IStrategyRuntime, GrpcStrategyRuntime>();
        if (options.PaperRiskPolicy is not null)
        {
            services.AddSingleton(options.PaperRiskPolicy);
            services.AddSingleton<IPaperRiskPolicyProvider, StaticPaperRiskPolicyProvider>();
        }
        else
        {
            services.AddSingleton<IPaperRiskPolicyProvider,
                UnavailablePaperRiskPolicyProvider>();
        }

        if (options.SimulatedPaperBroker is null)
        {
            services.AddSingleton<IPaperBrokerOrderAdapter,
                UnavailablePaperBrokerOrderAdapter>();
            services.AddSingleton<IPaperBrokerReconciliationAdapter>(serviceProvider =>
                (IPaperBrokerReconciliationAdapter)serviceProvider
                    .GetRequiredService<IPaperBrokerOrderAdapter>());
        }
        else
        {
            if (options.PaperRiskPolicy is null
                || !options.SimulatedPaperBroker.AvailablePolicies.Contains(
                    options.PaperRiskPolicy.Version.Reference))
            {
                throw new InvalidOperationException(
                    "The simulated paper Broker needs its exact Risk policy.");
            }

            services.AddSingleton(options.SimulatedPaperBroker);
            services.AddSingleton<SimulatedPaperBrokerAdapter>();
            services.AddSingleton<IPaperBrokerOrderAdapter>(serviceProvider =>
                serviceProvider.GetRequiredService<SimulatedPaperBrokerAdapter>());
            services.AddSingleton<IPaperBrokerReconciliationAdapter>(serviceProvider =>
                serviceProvider.GetRequiredService<SimulatedPaperBrokerAdapter>());
            services.AddSingleton<IBrokerConnectionProbe>(serviceProvider =>
                serviceProvider.GetRequiredService<SimulatedPaperBrokerAdapter>());
            services.AddSingleton<IExecutablePreflightProbe>(serviceProvider =>
                serviceProvider.GetRequiredService<SimulatedPaperBrokerAdapter>());
            services.AddSingleton<IIntentRiskProbe>(serviceProvider =>
                serviceProvider.GetRequiredService<SimulatedPaperBrokerAdapter>());
            services.AddSingleton<ISimulatedPaperBroker>(serviceProvider =>
                serviceProvider.GetRequiredService<SimulatedPaperBrokerAdapter>());
            services.AddSingleton<BrokerPreflightService>();
            services.AddSingleton<PaperReviewActivationService>();
            services.AddSingleton<PaperReviewSessionService>();
            services.AddSingleton<PaperReviewIntentService>();
            services.AddSingleton(serviceProvider => new PaperReviewService(
                serviceProvider.GetRequiredService<IPaperReviewIntentPersistence>(),
                serviceProvider.GetRequiredService<ISessionPersistence>(),
                serviceProvider.GetRequiredService<IGoalPersistence>(),
                serviceProvider.GetRequiredService<IGoalActivationPersistence>(),
                serviceProvider.GetRequiredService<IAccountAllocationPersistence>(),
                serviceProvider.GetRequiredService<IExecutablePreflightPersistence>(),
                serviceProvider.GetRequiredService<IIntentRiskProbe>(),
                serviceProvider.GetRequiredService<IExecutionSafetyPersistence>(),
                serviceProvider.GetRequiredService<IPaperReconciliationRunner>(),
                serviceProvider.GetRequiredService<IPaperRiskPolicyProvider>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                options.PaperRiskPolicy.ReviewApprovalLifetime));
            services.AddSingleton<PaperOrderService>();
        }

        services.AddSingleton<PostgresDatabaseMigrator>();
        services.AddSingleton<AuditIntegrityGuard>();
        services.AddSingleton<ReadOnlySessionRecoveryService>();
        services.AddSingleton<ExecutableSessionRecoveryService>();
        services.AddSingleton<PaperSubmissionRecoveryService>();
        services.AddSingleton<IPaperSubmissionRecovery>(serviceProvider =>
            serviceProvider.GetRequiredService<PaperSubmissionRecoveryService>());
        services.AddSingleton<PaperCancellationService>();
        services.AddSingleton<IPaperCancellationRecovery>(serviceProvider =>
            serviceProvider.GetRequiredService<PaperCancellationService>());
        services.AddSingleton<ITriggeredCancellationCoordinator>(serviceProvider =>
            serviceProvider.GetRequiredService<PaperCancellationService>());
        services.AddSingleton<PaperExecutionStartupRecoveryService>();
        services.AddSingleton<PaperReviewExpiryService>();
        services.AddSingleton<PaperPostFillSafetyService>();
        services.AddSingleton<PaperBrokerEventService>();
        services.AddSingleton<PaperReconciliationService>();
        services.AddSingleton<IPaperReconciliationRunner>(serviceProvider =>
            serviceProvider.GetRequiredService<PaperReconciliationService>());
        services.AddSingleton<PaperReconciliationRecoveryService>();
        services.AddSingleton<IPaperReconciliationRecovery>(serviceProvider =>
            serviceProvider.GetRequiredService<PaperReconciliationRecoveryService>());
        services.AddSingleton<PaperExecutableSessionLifecycleService>();
        services.AddSingleton<ExecutionSafetyService>();
        services.AddSingleton<GoalService>();
        services.AddSingleton<GoalActivationService>();
        services.AddSingleton<DataSnapshotService>();
        services.AddSingleton<DecisionGuard>();
        services.AddSingleton<ReplayService>();
        services.AddSingleton<ResearchService>();
        return services;
    }
}
