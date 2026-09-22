namespace Stockripper.Application.Execution;

public sealed record PaperOperatorSnapshot(
    Guid GoalVersionId,
    string Mode,
    string Environment,
    string MaskedAccountIdentifier,
    string? ActivationState,
    string? SessionState,
    string? IntentState,
    string? OrderState,
    string? ReconciliationState,
    string KillSwitchState,
    IReadOnlyList<string> Alerts);

public interface IPaperOperatorReadModel
{
    ValueTask<PaperOperatorSnapshot?> ReadLatestAsync(
        CancellationToken cancellationToken = default);
}
