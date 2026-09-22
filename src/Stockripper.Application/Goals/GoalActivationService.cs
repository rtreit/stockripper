using System.Globalization;
using System.Text.Json;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;

namespace Stockripper.Application.Goals;

public sealed class GoalActivationService(
    IGoalPersistence goalPersistence,
    IGoalActivationPersistence activationPersistence,
    TimeProvider timeProvider)
{
    public async ValueTask<GoalActivation> ActivateAsync(
        GoalVersionId goalVersionId,
        OperatorId operatorId,
        string activationEvidence,
        DateTimeOffset? expiryTime = null,
        CancellationToken cancellationToken = default)
    {
        var goalVersion = await goalPersistence
            .FindGoalVersionAsync(goalVersionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new GoalActivationNotFoundException();
        if (!goalVersion.HasValidContentDigest())
        {
            throw new InvalidDataException("The Goal version digest is invalid.");
        }

        if (goalVersion.Mode is not (ExecutionMode.REPLAY or ExecutionMode.RESEARCH))
        {
            throw new InvalidOperationException(
                "Phase 0 permits activation only for REPLAY and RESEARCH Goals.");
        }

        var activation = GoalActivation.Activate(
            ActivationId.New(),
            goalVersion.Id,
            operatorId,
            activationEvidence,
            CreateAuthoritySummary(goalVersion, expiryTime),
            timeProvider.GetUtcNow(),
            expiryTime);
        await activationPersistence
            .InsertAsync(goalVersion.GoalId, activation, operatorId, cancellationToken)
            .ConfigureAwait(false);
        return activation;
    }

    public static string CreateAuthoritySummary(
        GoalVersion goalVersion,
        DateTimeOffset? expiryTime = null)
    {
        ArgumentNullException.ThrowIfNull(goalVersion);
        var binding = goalVersion.ExecutionBinding;
        var account = binding?.ExpectedAccountIdentifier;
        var maskedAccount = account is null
            ? null
            : $"****{account[^Math.Min(4, account.Length)..]}";
        var summary = new
        {
            Schema = "stockripper-goal-authority-summary-v1",
            GoalId = goalVersion.GoalId.Value,
            GoalVersionId = goalVersion.Id.Value,
            goalVersion.VersionNumber,
            Mode = goalVersion.Mode.ToWireValue(),
            BrokerProvider = binding?.BrokerConnectionVersion.Identifier,
            Environment = binding?.ExpectedEnvironment.ToString(),
            MaskedAccountIdentifier = maskedAccount,
            CapitalAllocation = new
            {
                goalVersion.CapitalAllocation.Amount,
                goalVersion.CapitalAllocation.Currency
            },
            ExecutionSchedule = new
            {
                goalVersion.ExecutionSchedule.TimeZoneId,
                StartTime = goalVersion.ExecutionSchedule.StartTime.ToString(
                    "HH:mm:ss.fffffff",
                    CultureInfo.InvariantCulture),
                EndTime = goalVersion.ExecutionSchedule.EndTime.ToString(
                    "HH:mm:ss.fffffff",
                    CultureInfo.InvariantCulture),
                ActiveDays = goalVersion.ExecutionSchedule.ActiveDays.Select(day => day.ToString())
            },
            RiskPolicyVersion = goalVersion.RiskPolicyVersion,
            StrategyVersion = goalVersion.StrategyVersion,
            ActivationExpiry = expiryTime?.ToString("O")
        };
        return JsonSerializer.Serialize(summary);
    }

    public ValueTask<GoalActivation> PauseAsync(
        ActivationId activationId,
        OperatorId actorId,
        CancellationToken cancellationToken = default) => ChangeAsync(
        activationId,
        actorId,
        static activation => activation.Pause(),
        cancellationToken);

    public ValueTask<GoalActivation> ResumeAsync(
        ActivationId activationId,
        OperatorId actorId,
        CancellationToken cancellationToken = default) => ChangeAsync(
        activationId,
        actorId,
        static activation => activation.Resume(),
        cancellationToken);

    public ValueTask<GoalActivation> RevokeAsync(
        ActivationId activationId,
        OperatorId actorId,
        string reason,
        CancellationToken cancellationToken = default) => ChangeAsync(
        activationId,
        actorId,
        activation => activation.Revoke(timeProvider.GetUtcNow(), reason),
        cancellationToken);

    public ValueTask<GoalActivation> ExpireAsync(
        ActivationId activationId,
        OperatorId actorId,
        CancellationToken cancellationToken = default) => ChangeAsync(
        activationId,
        actorId,
        activation =>
        {
            if (activation.ExpiryTime is null || timeProvider.GetUtcNow() < activation.ExpiryTime)
            {
                throw new InvalidOperationException("The Goal activation has not reached its expiry time.");
            }

            return activation.Expire();
        },
        cancellationToken);

    private async ValueTask<GoalActivation> ChangeAsync(
        ActivationId activationId,
        OperatorId actorId,
        Func<GoalActivation, GoalActivation> transition,
        CancellationToken cancellationToken)
    {
        var current = await activationPersistence.FindAsync(activationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new GoalActivationNotFoundException();
        var updated = transition(current);
        await activationPersistence
            .UpdateAsync(
                updated,
                current.State,
                current.Revision,
                actorId,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        return updated;
    }
}
