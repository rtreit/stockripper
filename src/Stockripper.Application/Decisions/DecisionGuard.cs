using System.Globalization;
using Stockripper.Domain.Common;
using Stockripper.Domain.Data;
using Stockripper.Domain.Decisions;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Risk;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Decisions;

public sealed class DecisionGuard(
    string expectedContractVersion = Replay.StrategyRuntimeContract.V1)
{
    public Decision Evaluate(
        GoalVersion goalVersion,
        Session session,
        DataSnapshot snapshot,
        Replay.StrategyRuntimeOutput output,
        DateTimeOffset evaluatedAt) => EvaluateCore(
            goalVersion,
            session,
            snapshot,
            output,
            evaluatedAt,
            [
                Result(
                    "READ_ONLY_MODE",
                    goalVersion.Mode is ExecutionMode.REPLAY or ExecutionMode.RESEARCH,
                    "Phase 0 Decision evaluation permits only read-only modes.")
            ]);

    public Decision EvaluatePaper(
        GoalVersion goalVersion,
        Session session,
        DataSnapshot snapshot,
        Replay.StrategyRuntimeOutput output,
        PaperRiskPolicy riskPolicy,
        DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(goalVersion);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(riskPolicy);
        var binding = goalVersion.ExecutionBinding;
        var paperAuthority = goalVersion.Mode is (ExecutionMode.PAPER_REVIEW
            or ExecutionMode.PAPER_AUTO)
            && binding?.ExpectedEnvironment == BrokerEnvironment.PAPER
            && session.State == SessionState.RUNNING;
        var observedBinding = binding is not null
            && session.ObservedEnvironment == BrokerEnvironment.PAPER
            && string.Equals(session.ObservedEndpointIdentity,
                binding.EndpointIdentity, StringComparison.Ordinal)
            && string.Equals(session.ObservedAccountIdentifier,
                binding.ExpectedAccountIdentifier, StringComparison.Ordinal);
        var dataCurrent = snapshot.RangeEnd <= evaluatedAt
            && evaluatedAt - snapshot.RangeEnd <= riskPolicy.MaximumMarketDataAge
            && snapshot.SourceObservations.All(source => source.ObservedAt <= evaluatedAt
                && evaluatedAt - source.ObservedAt
                    <= riskPolicy.MaximumMarketDataAge);
        var boundedAction = output.ProposedAction == DecisionAction.NO_ACTION
            || (TryPositiveDecimal(output.Quantity, out var quantity)
                && decimal.Truncate(quantity) == quantity
                && TryPositiveDecimal(output.LimitPrice, out _));
        return EvaluateCore(
            goalVersion,
            session,
            snapshot,
            output,
            evaluatedAt,
            [
                Result("PAPER_MODE", paperAuthority,
                    "Paper Decision evaluation requires a running paper Session."),
                Result("PAPER_BINDING", observedBinding,
                    "The observed Broker identity must match the Goal binding."),
                Result("RISK_POLICY", riskPolicy.IsBoundTo(goalVersion.RiskPolicyVersion),
                    "The exact paper Risk policy must be available."),
                Result("DATA_FRESHNESS", dataCurrent,
                    "Paper Decision data must satisfy the Risk policy freshness limit."),
                Result("BOUNDED_ORDER", boundedAction,
                    "An actionable paper Decision needs whole quantity and limit price.")
            ]);
    }

    private Decision EvaluateCore(
        GoalVersion goalVersion,
        Session session,
        DataSnapshot snapshot,
        Replay.StrategyRuntimeOutput output,
        DateTimeOffset evaluatedAt,
        IReadOnlyList<DecisionGuardResult> boundaryGuards)
    {
        ArgumentNullException.ThrowIfNull(goalVersion);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(output);

        var action = output.ProposedAction;
        var quantityParsed = TryPositiveDecimal(output.Quantity, out var quantity);
        var priceParsed = string.IsNullOrWhiteSpace(output.LimitPrice)
            || TryPositiveDecimal(output.LimitPrice, out _);
        decimal? limitPrice = TryPositiveDecimal(output.LimitPrice, out var parsedPrice)
            ? parsedPrice
            : null;
        var actionShapeValid = action switch
        {
            DecisionAction.NO_ACTION => string.IsNullOrWhiteSpace(output.InstrumentIdentifier)
                && string.IsNullOrWhiteSpace(output.Quantity)
                && string.IsNullOrWhiteSpace(output.LimitPrice),
            DecisionAction.BUY or DecisionAction.SELL => !string.IsNullOrWhiteSpace(output.InstrumentIdentifier)
                && quantityParsed
                && priceParsed,
            _ => false
        };
        var instrumentAllowed = action == DecisionAction.NO_ACTION
            || (!string.IsNullOrWhiteSpace(output.InstrumentIdentifier)
                && goalVersion.InstrumentUniverse.Contains(
                    output.InstrumentIdentifier.Trim(),
                    StringComparer.Ordinal));
        var explanationValid = !string.IsNullOrWhiteSpace(output.Explanation)
            && output.Explanation == output.Explanation.Trim();
        var confidencePresent = !string.IsNullOrWhiteSpace(output.Confidence);
        var confidenceParsed = decimal.TryParse(
            output.Confidence,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var confidence);
        var confidenceValid = !confidencePresent
            || (confidenceParsed && confidence is >= 0m and <= 1m);
        var evidenceValid = output.OutputMetrics is not null
            && output.OutputMetrics.Count > 0
            && !string.IsNullOrWhiteSpace(output.Evidence);
        var guards = new List<DecisionGuardResult>
        {
            Result(
                "CONTRACT_VERSION",
                string.Equals(
                    output.ContractVersion,
                    expectedContractVersion,
                    StringComparison.Ordinal),
                "Strategy contract version must match the application contract."),
            Result(
                "STRATEGY_VERSION",
                output.StrategyVersion == goalVersion.StrategyVersion,
                "Strategy version must match the Goal version."),
            Result(
                "STRATEGY_ARTIFACT",
                string.Equals(
                    output.StrategyArtifactDigest,
                    goalVersion.StrategyVersion.Digest,
                    StringComparison.Ordinal),
                "Strategy artifact digest must match the Goal version."),
            Result(
                "GOAL_DIGEST",
                goalVersion.HasValidContentDigest(),
                "Goal version content must retain its published digest."),
            Result(
                "SESSION_BINDING",
                session.GoalVersionId == goalVersion.Id
                    && session.InputDataSnapshotId == snapshot.Id
                    && session.Mode == goalVersion.Mode,
                "Session identifiers and mode must match the immutable inputs."),
        };
        guards.AddRange(boundaryGuards);
        guards.AddRange(
        [
            Result(
                "DECISION_SCHEDULE",
                goalVersion.DecisionSchedule.Allows(evaluatedAt),
                "Decision time must be inside the Goal schedule."),
            Result(
                "DATA_POLICY",
                snapshot.DataPolicyVersion == goalVersion.DataPolicyVersion
                    && snapshot.HasValidContentDigest(),
                "Input snapshot policy and digest must match the Goal version."),
            Result("OUTPUT_SCHEMA", actionShapeValid, "Strategy action fields must match the response schema."),
            Result("INSTRUMENT", instrumentAllowed, "The proposed instrument must be in the Goal universe."),
            Result("EXPLANATION", explanationValid, "Strategy output must contain explanation data."),
            Result("EVIDENCE", evidenceValid, "Strategy output must contain metrics and evidence."),
            Result("CONFIDENCE", confidenceValid, "Confidence must be from zero through one when present.")
        ]);
        return Decision.Create(
            DecisionId.New(),
            session.Id,
            goalVersion.Id,
            output.StrategyVersion,
            snapshot.Id,
            evaluatedAt,
            action,
            output.InstrumentIdentifier,
            quantityParsed ? quantity : null,
            limitPrice,
            explanationValid
                ? output.Explanation!
                : "Strategy response did not contain a valid explanation.",
            confidenceValid && confidencePresent ? confidence : null,
            guards);
    }

    private static DecisionGuardResult Result(string code, bool passed, string detail) => new(
        code,
        passed,
        detail);

    private static bool TryPositiveDecimal(string? value, out decimal result) => decimal.TryParse(
        value,
        NumberStyles.Number,
        CultureInfo.InvariantCulture,
        out result) && result > 0m;
}
