using System.Globalization;
using System.Text;
using Stockripper.Application.Allocations;
using Stockripper.Application.Data;
using Stockripper.Application.Decisions;
using Stockripper.Application.Execution;
using Stockripper.Application.Goals;
using Stockripper.Application.Replay;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;
using Stockripper.Infrastructure.Brokers;

namespace Stockripper.Host.Runtime;

public sealed record PreparedPaperMvp(
    GoalVersion Goal,
    DataSnapshot Snapshot,
    AccountAllocationLimit AccountLimit,
    string AuthoritySummary,
    decimal Quantity,
    decimal LimitPrice);

public sealed class PaperMvpRun
{
    public required PreparedPaperMvp Prepared { get; init; }
    public GoalActivation? Activation { get; set; }
    public Session? Session { get; set; }
    public Decision? Decision { get; set; }
    public RiskEvaluation? RiskEvaluation { get; set; }
    public ExecutionIntent? PreviousIntent { get; set; }
    public ExecutionIntent? Intent { get; set; }
    public ReviewApproval? Review { get; set; }
    public PreSubmitEvaluation? PreSubmitEvaluation { get; set; }
    public OrderRecord? Order { get; set; }
    public FillRecord? Fill { get; set; }
    public ReconciliationRecord? Reconciliation { get; set; }
    public PostFillEvaluation? PostFillEvaluation { get; set; }
}

public sealed class PaperMvpWorkflow(
    GoalService goalService,
    GoalActivationService goalActivationService,
    DataSnapshotService snapshotService,
    PaperReviewActivationService activationService,
    PaperReviewSessionService sessionService,
    PaperExecutableSessionLifecycleService lifecycleService,
    IDecisionPersistence decisionPersistence,
    DecisionGuard decisionGuard,
    IPaperReviewIntentPersistence intentPersistence,
    IPaperOrderPersistence orderPersistence,
    PaperReviewIntentService intentService,
    PaperReviewService reviewService,
    PaperOrderService orderService,
    PaperBrokerEventService eventService,
    PaperPostFillSafetyService postFillSafetyService,
    PaperCancellationService cancellationService,
    PaperReconciliationService reconciliationService,
    ExecutionSafetyService safetyService,
    IExecutionSafetyPersistence safetyPersistence,
    ISimulatedPaperBroker simulatedBroker,
    IPaperOperatorReadModel readModel,
    TimeProvider timeProvider)
{
    public PaperMvpRun? Current { get; private set; }

    public void Reset() => Current = null;

    public ValueTask<PaperOperatorSnapshot?> ReadStatusAsync(
        CancellationToken cancellationToken = default) =>
        readModel.ReadLatestAsync(cancellationToken);

    public async ValueTask<PaperMvpRun> PrepareAsync(
        ExecutionMode mode,
        string objective,
        string instrument,
        decimal quantity,
        decimal limitPrice,
        decimal capitalAllocation,
        CancellationToken cancellationToken = default)
    {
        var checkedInstrument = instrument.Trim().ToUpperInvariant();
        if (mode is not (ExecutionMode.PAPER_REVIEW or ExecutionMode.PAPER_AUTO)
            || !PaperMvpConfiguration.RiskPolicy.AllowedInstruments.Contains(
                checkedInstrument)
            || quantity <= 0m
            || decimal.Truncate(quantity) != quantity
            || limitPrice <= 0m
            || capitalAllocation <= 0m
            || capitalAllocation
                > PaperMvpConfiguration.RiskPolicy.MaximumPortfolioExposure)
        {
            throw new InvalidOperationException(
                "The simulated paper Goal input is outside the MVP policy.");
        }

        var now = timeProvider.GetUtcNow();
        var schedule = new ScheduleDefinition(
            "UTC", TimeOnly.MinValue, TimeOnly.MaxValue,
            Enum.GetValues<DayOfWeek>());
        var definition = new GoalVersionDefinition(
            mode,
            objective.Trim(),
            [checkedInstrument],
            PaperMvpConfiguration.StrategyVersion,
            PaperMvpConfiguration.DataPolicyVersion,
            PaperMvpConfiguration.RiskPolicy.Version.Reference,
            new Money(capitalAllocation, "USD"),
            schedule,
            schedule,
            "Record deterministic evidence before each simulated paper action.",
            "Compare each result with the immutable Goal and Broker truth.",
            PaperMvpConfiguration.ExecutionBinding);
        var draft = await goalService.CreateDraftAsync(
            GoalId.New(), 1, definition, PaperMvpConfiguration.LocalOperator,
            cancellationToken).ConfigureAwait(false);
        var goal = await goalService.PublishDraftAsync(
            draft.Id, draft.Revision, PaperMvpConfiguration.LocalOperator,
            cancellationToken).ConfigureAwait(false);
        var snapshot = await snapshotService.CreateAsync(
            new VersionReference("simulated-paper-market-data", "1",
                "sha256:simulated-paper-market-data-v1"),
            PaperMvpConfiguration.DataPolicyVersion,
            "stock-quote-v1",
            now.AddMinutes(-2),
            now,
            [new DataSourceObservation("simulated-paper-market-data", "1", now)],
            Encoding.UTF8.GetBytes(
                $"{{\"instrument\":\"{checkedInstrument}\",\"limitPrice\":{limitPrice}}}"),
            PaperMvpConfiguration.LocalOperator,
            cancellationToken).ConfigureAwait(false);
        _ = await safetyService.ConfigurePaperScopesAsync(
            goal.Id, PaperMvpConfiguration.LocalOperator, cancellationToken)
            .ConfigureAwait(false);
        var accountLimit = AccountAllocationLimit.Create(
            new VersionReference("simulated-paper-account-limit",
                $"1-{goal.Id.Value:N}", $"sha256:simulated-limit-{goal.Id.Value:N}"),
            PaperMvpConfiguration.BrokerConnectionVersion,
            BrokerEnvironment.PAPER,
            PaperMvpConfiguration.ExecutionBinding.EndpointIdentity,
            PaperMvpConfiguration.ExecutionBinding.ExpectedAccountIdentifier,
            new Money(25_000m, "USD"),
            PaperMvpConfiguration.LocalOperator,
            now);
        await activationService.ConfigureAccountLimitAsync(
            accountLimit, PaperMvpConfiguration.LocalOperator, cancellationToken)
            .ConfigureAwait(false);
        var prepared = new PreparedPaperMvp(goal, snapshot, accountLimit,
            GoalActivationService.CreateAuthoritySummary(goal, null), quantity,
            limitPrice);
        Current = new PaperMvpRun { Prepared = prepared };
        return Current;
    }

    public async ValueTask<PaperMvpRun> ActivateAsync(
        string operatorEvidence,
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        if (run.Activation is not null)
        {
            return run;
        }

        var authority = await activationService.ActivateAsync(
            run.Prepared.Goal.Id,
            run.Prepared.AccountLimit.Reference,
            PaperMvpConfiguration.LocalOperator,
            operatorEvidence,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        run.Activation = authority.Activation;
        return run;
    }

    public async ValueTask<PaperMvpRun> StartSessionAsync(
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        var activation = run.Activation
            ?? throw new InvalidOperationException("Activate the Goal before Session start.");
        var outcome = await sessionService.StartAsync(
            run.Prepared.Goal.Id, activation.Id, run.Prepared.Snapshot.Id,
            PaperMvpConfiguration.LocalOperator, cancellationToken).ConfigureAwait(false);
        if (outcome.Session.State != SessionState.READY)
        {
            run.Session = outcome.Session;
            throw new InvalidOperationException(
                "The simulated paper Session failed preflight.");
        }

        run.Session = await lifecycleService.StartAsync(
            outcome.Session.Id, PaperMvpConfiguration.LocalOperator,
            cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async ValueTask<PaperMvpRun> PauseActivationAsync(
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        var activation = run.Activation
            ?? throw new InvalidOperationException("Activate the Goal before pausing it.");
        run.Activation = await goalActivationService.PauseAsync(
            activation.Id,
            PaperMvpConfiguration.LocalOperator,
            cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async ValueTask<PaperMvpRun> ResumeActivationAsync(
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        var activation = run.Activation
            ?? throw new InvalidOperationException("Activate the Goal before resuming it.");
        run.Activation = await goalActivationService.ResumeAsync(
            activation.Id,
            PaperMvpConfiguration.LocalOperator,
            cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async ValueTask<PaperMvpRun> RevokeActivationAsync(
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        var activation = run.Activation
            ?? throw new InvalidOperationException("Activate the Goal before revoking it.");
        run.Activation = await goalActivationService.RevokeAsync(
            activation.Id,
            PaperMvpConfiguration.LocalOperator,
            "The Operator permanently revoked the simulated paper authority.",
            cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async ValueTask<PaperMvpRun> CreateIntentAsync(
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        var session = run.Session;
        if (session?.State != SessionState.RUNNING)
        {
            throw new InvalidOperationException("Start a running Session first.");
        }

        await safetyService.RequirePaperExecutionClearAsync(
            run.Prepared.Goal.Id, cancellationToken).ConfigureAwait(false);

        var evaluatedAt = timeProvider.GetUtcNow();
        var strategyOutput = new StrategyRuntimeOutput(
            StrategyRuntimeContract.V1,
            run.Prepared.Goal.StrategyVersion,
            run.Prepared.Goal.StrategyVersion.Digest,
            DecisionAction.BUY,
            run.Prepared.Goal.InstrumentUniverse.Single(),
            run.Prepared.Quantity.ToString("G29", CultureInfo.InvariantCulture),
            run.Prepared.LimitPrice.ToString("G29", CultureInfo.InvariantCulture),
            "The simulated Strategy proposed the configured bounded limit order.",
            "1",
            new Dictionary<string, decimal>
            {
                ["maximumExposure"] = checked(
                    run.Prepared.Quantity * run.Prepared.LimitPrice)
            },
            "SIMULATED_PAPER_PROPOSAL");
        var decision = decisionGuard.EvaluatePaper(
            run.Prepared.Goal, session, run.Prepared.Snapshot,
            strategyOutput, PaperMvpConfiguration.RiskPolicy, evaluatedAt);
        await decisionPersistence.InsertAsync(
            decision, PaperMvpConfiguration.LocalOperator, cancellationToken)
            .ConfigureAwait(false);
        run.Decision = decision;
        if (decision.State != DecisionState.PROPOSED)
        {
            return run;
        }

        var outcome = await intentService.CreateAsync(
            decision.Id, run.Prepared.Quantity, run.Prepared.LimitPrice,
            timeProvider.GetUtcNow().AddMinutes(5),
            PaperMvpConfiguration.LocalOperator, cancellationToken)
            .ConfigureAwait(false);
        run.RiskEvaluation = outcome.RiskEvaluation;
        run.Intent = outcome.Intent;
        if (outcome.Intent.Mode == ExecutionMode.PAPER_AUTO
            && outcome.Intent.State == ExecutionIntentState.RISK_ACCEPTED)
        {
            await PrepareAndSendAsync(run, cancellationToken).ConfigureAwait(false);
        }

        return run;
    }

    public async ValueTask<PaperMvpRun> ReviewAsync(
        ReviewResult result,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        var intent = run.Intent
            ?? throw new InvalidOperationException("Create an Execution intent first.");
        run.Review = await reviewService.ReviewAsync(
            intent.Id, intent.ContentDigest, result,
            PaperMvpConfiguration.LocalOperator, note, cancellationToken)
            .ConfigureAwait(false);
        run.Intent = await ReloadIntentAsync(intent.Id, cancellationToken)
            .ConfigureAwait(false);
        if (result == ReviewResult.APPROVED)
        {
            await PrepareAndSendAsync(run, cancellationToken).ConfigureAwait(false);
        }

        return run;
    }

    public async ValueTask<PaperMvpRun> ReplaceIntentAsync(
        decimal quantity,
        decimal limitPrice,
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        var current = run.Intent
            ?? throw new InvalidOperationException("Create an Execution intent first.");
        var decision = run.Decision
            ?? throw new InvalidOperationException("The original Decision is unavailable.");
        if (current.Mode != ExecutionMode.PAPER_REVIEW
            || current.State != ExecutionIntentState.REVIEW_PENDING)
        {
            throw new InvalidOperationException(
                "Only a pending paper Review intent can be replaced.");
        }

        var outcome = await intentService.CreateAsync(
            decision.Id,
            quantity,
            limitPrice,
            timeProvider.GetUtcNow().AddMinutes(5),
            PaperMvpConfiguration.LocalOperator,
            cancellationToken).ConfigureAwait(false);
        run.PreviousIntent = current;
        run.RiskEvaluation = outcome.RiskEvaluation;
        run.Intent = outcome.Intent;
        run.Review = null;
        return run;
    }

    public async ValueTask<PaperMvpRun> SimulateFillAsync(
        decimal fillPrice,
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        var order = run.Order
            ?? throw new InvalidOperationException("Submit a simulated paper Order first.");
        var update = await simulatedBroker.FillAsync(order, fillPrice, cancellationToken)
            .ConfigureAwait(false);
        var ingestion = await eventService.IngestAsync(
            update, PaperMvpConfiguration.LocalOperator, cancellationToken)
            .ConfigureAwait(false);
        run.Order = ingestion.Order;
        run.Fill = ingestion.Fill;
        if (ingestion.Fill is not null)
        {
            var outcome = await postFillSafetyService.EvaluateAsync(
                simulatedBroker.CreatePositionObservation(ingestion.Fill.Id),
                PaperMvpConfiguration.LocalOperator, cancellationToken)
                .ConfigureAwait(false);
            run.PostFillEvaluation = outcome.Evaluation;
        }

        return run;
    }

    public async ValueTask<PaperMvpRun> CancelOrderAsync(
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        var order = run.Order
            ?? throw new InvalidOperationException("No simulated paper Order exists.");
        _ = await cancellationService.CancelAsync(order.Id,
            PaperMvpConfiguration.LocalOperator,
            "The Operator canceled the simulated paper Order.", cancellationToken)
            .ConfigureAwait(false);
        run.Reconciliation = await reconciliationService.ReconcileAsync(
            run.Prepared.Goal.Id, PaperMvpConfiguration.LocalOperator,
            cancellationToken).ConfigureAwait(false);
        run.Order = await ReloadOrderAsync(order.Id, cancellationToken)
            .ConfigureAwait(false);
        return run;
    }

    public async ValueTask<PaperMvpRun> CompleteSessionAsync(
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        var session = run.Session
            ?? throw new InvalidOperationException("No simulated paper Session exists.");
        var outcome = await lifecycleService.CompleteAsync(
            session.Id, PaperMvpConfiguration.LocalOperator, cancellationToken)
            .ConfigureAwait(false);
        run.Session = outcome.Session;
        run.Reconciliation = outcome.Reconciliation;
        return run;
    }

    public async ValueTask<PaperMvpRun> StartNextSessionAsync(
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        if (run.Activation?.State != GoalActivationState.ACTIVE
            || run.Session?.State != SessionState.COMPLETED)
        {
            throw new InvalidOperationException(
                "A completed Session and active Goal authority are required.");
        }

        run.Session = null;
        run.Decision = null;
        run.RiskEvaluation = null;
        run.PreviousIntent = null;
        run.Intent = null;
        run.Review = null;
        run.PreSubmitEvaluation = null;
        run.Order = null;
        run.Fill = null;
        run.Reconciliation = null;
        run.PostFillEvaluation = null;
        return await StartSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PaperMvpRun> TriggerKillSwitchAsync(
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        var binding = ExecutionSafetyService.ToAccountBinding(run.Prepared.Goal);
        var switches = await safetyPersistence.FindKillSwitchesAsync(
            PaperExecutionSafetyScopes.Required(run.Prepared.Goal.Id, binding),
            cancellationToken).ConfigureAwait(false);
        var system = switches.Single(item =>
            item.ScopeType == KillSwitchScopeType.SYSTEM);
        _ = await safetyService.TriggerAsync(system.Id,
            PaperMvpConfiguration.LocalOperator,
            "The Operator triggered the simulated paper kill switch.",
            cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async ValueTask<PaperMvpRun> RecoverKillSwitchesAsync(
        CancellationToken cancellationToken = default)
    {
        var run = RequireCurrent();
        var reconciliation = await reconciliationService.ReconcileAsync(
            run.Prepared.Goal.Id, PaperMvpConfiguration.LocalOperator,
            cancellationToken).ConfigureAwait(false);
        if (reconciliation.Result != ReconciliationResult.CURRENT)
        {
            throw new InvalidOperationException(
                "Current Broker truth does not permit kill-switch recovery.");
        }

        var binding = ExecutionSafetyService.ToAccountBinding(run.Prepared.Goal);
        var switches = await safetyPersistence.FindKillSwitchesAsync(
            PaperExecutionSafetyScopes.Required(run.Prepared.Goal.Id, binding),
            cancellationToken).ConfigureAwait(false);
        foreach (var killSwitch in switches.Where(item =>
                     item.State != KillSwitchState.CLEAR))
        {
            var recoverable = killSwitch.State == KillSwitchState.TRIGGERED
                ? await safetyService.RequireRecoveryAsync(killSwitch.Id,
                    reconciliation.Id, PaperMvpConfiguration.LocalOperator,
                    cancellationToken).ConfigureAwait(false)
                : killSwitch;
            _ = await safetyService.ClearAsync(recoverable.Id,
                PaperMvpConfiguration.LocalOperator, cancellationToken)
                .ConfigureAwait(false);
        }

        run.Reconciliation = reconciliation;
        return run;
    }

    private async ValueTask PrepareAndSendAsync(
        PaperMvpRun run,
        CancellationToken cancellationToken)
    {
        var intent = run.Intent
            ?? throw new InvalidOperationException("The Execution intent is unavailable.");
        var preparation = await orderService.PrepareAsync(
            intent.Id, PaperMvpConfiguration.LocalOperator, cancellationToken)
            .ConfigureAwait(false);
        run.PreSubmitEvaluation = preparation.Evaluation;
        if (preparation.Order is null)
        {
            return;
        }

        run.Order = await orderService.SendPreparedAsync(
            preparation.Order.Id, PaperMvpConfiguration.LocalOperator,
            cancellationToken).ConfigureAwait(false);
        run.Intent = await ReloadIntentAsync(intent.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<ExecutionIntent> ReloadIntentAsync(
        ExecutionIntentId intentId,
        CancellationToken cancellationToken) =>
        await intentPersistence.FindIntentAsync(intentId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new InvalidOperationException("The durable Execution intent is unavailable.");

    private async ValueTask<OrderRecord> ReloadOrderAsync(
        OrderId orderId,
        CancellationToken cancellationToken) =>
        await orderPersistence.FindOrderAsync(orderId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new InvalidOperationException("The durable Order is unavailable.");

    private PaperMvpRun RequireCurrent() => Current
        ?? throw new InvalidOperationException("Prepare a simulated paper Goal first.");

}
