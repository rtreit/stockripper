using System.Text.Json;
using Stockripper.Application.Data;
using Stockripper.Application.Goals;
using Stockripper.Application.Replay;
using Stockripper.Application.Research;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Replay;
using Stockripper.Domain.Research;
using Stockripper.Domain.Risk;

namespace Stockripper.Host.Runtime;

public sealed record PreparedPhaseZeroRun(
    GoalVersion GoalVersion,
    DataSnapshot DataSnapshot,
    string AuthoritySummary);

public sealed record CompletedPhaseZeroRun(
    Guid ResultId,
    Guid SessionId,
    string ResultType,
    string Evidence,
    string ContentDigest,
    IReadOnlyDictionary<string, decimal> Metrics);

public sealed class PhaseZeroWorkflow(
    GoalService goalService,
    DataSnapshotService snapshotService,
    GoalActivationService activationService,
    ReplayService replayService,
    ResearchService researchService,
    TimeProvider timeProvider)
{
    private static readonly OperatorId LocalOperator = new(
        Guid.Parse("00000000-0000-0000-0000-000000000002"));

    public async ValueTask<PreparedPhaseZeroRun> PrepareAsync(
        ExecutionMode mode,
        string objective,
        IEnumerable<string> instruments,
        CancellationToken cancellationToken = default)
    {
        if (mode is not (ExecutionMode.REPLAY or ExecutionMode.RESEARCH))
        {
            throw new InvalidOperationException("The Phase 0 workflow accepts only REPLAY or RESEARCH.");
        }

        var checkedInstruments = instruments
            .Select(instrument => instrument.Trim().ToUpperInvariant())
            .Where(instrument => instrument.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var now = timeProvider.GetUtcNow();
        var riskPolicy = RiskPolicyVersion.Create(
            "phase0-read-only-risk-policy",
            "1",
            Enum.GetValues<RiskControlKind>().Select(RiskControlDefinition.Disabled),
            LocalOperator,
            now);
        var dataPolicy = new VersionReference(
            "retained-fixture-data-policy",
            "1",
            "sha256:phase0-retained-fixture-data-policy-v1");
        var schedule = new ScheduleDefinition(
            "UTC",
            TimeOnly.MinValue,
            TimeOnly.MaxValue,
            Enum.GetValues<DayOfWeek>());
        var definition = new GoalVersionDefinition(
            mode,
            objective.Trim(),
            checkedInstruments,
            new VersionReference(
                "phase0-no-action-strategy",
                "1",
                "sha256:phase0-no-action-strategy-v1"),
            dataPolicy,
            riskPolicy.Reference,
            new Money(10_000m, "USD"),
            schedule,
            schedule,
            "Record a deterministic, non-executable Decision with evidence.",
            "Verify stable output for the same immutable inputs.",
            null);
        var draft = await goalService.CreateDraftAsync(
            GoalId.New(),
            1,
            definition,
            LocalOperator,
            cancellationToken).ConfigureAwait(false);
        var goalVersion = await goalService.PublishDraftAsync(
            draft.Id,
            draft.Revision,
            LocalOperator,
            cancellationToken).ConfigureAwait(false);

        var rangeEnd = now.AddMinutes(-1);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = "stockripper-phase0-fixture-v1",
            Instruments = checkedInstruments,
            RangeEnd = rangeEnd.ToString("O")
        });
        var snapshot = await snapshotService.CreateAsync(
            new VersionReference(
                "phase0-fixture-data-set",
                "1",
                "sha256:phase0-fixture-data-set-v1"),
            dataPolicy,
            "stockripper-phase0-fixture-v1",
            rangeEnd.AddDays(-1),
            rangeEnd,
            [new DataSourceObservation("phase0-retained-fixture", "1", rangeEnd)],
            payload,
            LocalOperator,
            cancellationToken).ConfigureAwait(false);
        return new PreparedPhaseZeroRun(
            goalVersion,
            snapshot,
            GoalActivationService.CreateAuthoritySummary(goalVersion));
    }

    public ValueTask<GoalActivation> ActivateAsync(
        PreparedPhaseZeroRun prepared,
        CancellationToken cancellationToken = default) => activationService.ActivateAsync(
        prepared.GoalVersion.Id,
        LocalOperator,
        "The local operator confirmed the displayed authority summary.",
        cancellationToken: cancellationToken);

    public async ValueTask<CompletedPhaseZeroRun> RunAsync(
        PreparedPhaseZeroRun prepared,
        GoalActivation activation,
        long replaySeed,
        CancellationToken cancellationToken = default)
    {
        if (prepared.GoalVersion.Mode == ExecutionMode.REPLAY)
        {
            var result = await replayService.RunAsync(
                prepared.GoalVersion.Id,
                activation.Id,
                prepared.DataSnapshot.Id,
                replaySeed,
                LocalOperator,
                cancellationToken).ConfigureAwait(false);
            return FromReplay(result);
        }

        var research = await researchService.RunAsync(
            prepared.GoalVersion.Id,
            activation.Id,
            prepared.DataSnapshot.Id,
            LocalOperator,
            cancellationToken).ConfigureAwait(false);
        return FromResearch(research);
    }

    private static CompletedPhaseZeroRun FromReplay(ReplayResult result) => new(
        result.Id.Value,
        result.SessionId.Value,
        "REPLAY",
        result.Evidence,
        result.OutputDigest,
        result.OutputMetrics);

    private static CompletedPhaseZeroRun FromResearch(ResearchResult result) => new(
        result.Id.Value,
        result.SessionId.Value,
        "RESEARCH",
        result.Evidence,
        result.ContentDigest,
        result.OutputMetrics);
}
