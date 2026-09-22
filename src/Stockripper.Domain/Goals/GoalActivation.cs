using Stockripper.Domain.Common;

namespace Stockripper.Domain.Goals;

public enum GoalActivationState
{
    INACTIVE,
    ACTIVE,
    PAUSED,
    REVOKED,
    EXPIRED
}

public sealed record GoalActivation
{
    private GoalActivation(
        ActivationId id,
        GoalVersionId goalVersionId,
        OperatorId operatorId,
        GoalActivationState state,
        string activationEvidence,
        string authoritySummary,
        DateTimeOffset activationTime,
        DateTimeOffset? expiryTime,
        DateTimeOffset? revocationTime,
        string? revocationReason,
        int revision)
    {
        Id = id;
        GoalVersionId = goalVersionId;
        OperatorId = operatorId;
        State = state;
        ActivationEvidence = activationEvidence;
        AuthoritySummary = authoritySummary;
        ActivationTime = activationTime;
        ExpiryTime = expiryTime;
        RevocationTime = revocationTime;
        RevocationReason = revocationReason;
        Revision = revision;
    }

    public ActivationId Id { get; }

    public GoalVersionId GoalVersionId { get; }

    public OperatorId OperatorId { get; }

    public GoalActivationState State { get; private init; }

    public string ActivationEvidence { get; }

    public string AuthoritySummary { get; }

    public DateTimeOffset ActivationTime { get; }

    public DateTimeOffset? ExpiryTime { get; }

    public DateTimeOffset? RevocationTime { get; private init; }

    public string? RevocationReason { get; private init; }

    public int Revision { get; private init; }

    public static GoalActivation Activate(
        ActivationId id,
        GoalVersionId goalVersionId,
        OperatorId operatorId,
        string activationEvidence,
        DateTimeOffset activationTime,
        DateTimeOffset? expiryTime = null)
        => Activate(
            id,
            goalVersionId,
            operatorId,
            activationEvidence,
            activationEvidence,
            activationTime,
            expiryTime);

    public static GoalActivation Activate(
        ActivationId id,
        GoalVersionId goalVersionId,
        OperatorId operatorId,
        string activationEvidence,
        string authoritySummary,
        DateTimeOffset activationTime,
        DateTimeOffset? expiryTime = null)
    {
        if (id.Value == Guid.Empty || goalVersionId.Value == Guid.Empty || operatorId.Value == Guid.Empty)
        {
            throw new DomainRuleViolationException("Goal activation contains an empty stable identifier.");
        }

        if (string.IsNullOrWhiteSpace(activationEvidence))
        {
            throw new DomainRuleViolationException("Goal activation evidence is required.");
        }

        if (string.IsNullOrWhiteSpace(authoritySummary))
        {
            throw new DomainRuleViolationException("The Goal authority summary is required.");
        }

        var checkedActivationTime = UtcTimestamp.Require(activationTime, nameof(activationTime));
        DateTimeOffset? checkedExpiryTime = expiryTime is null
            ? null
            : UtcTimestamp.Require(expiryTime.Value, nameof(expiryTime));

        if (checkedExpiryTime <= checkedActivationTime)
        {
            throw new DomainRuleViolationException("Goal activation expiry must follow activation.");
        }

        return new GoalActivation(
            id,
            goalVersionId,
            operatorId,
            GoalActivationState.ACTIVE,
            activationEvidence,
            authoritySummary,
            checkedActivationTime,
            checkedExpiryTime,
            null,
            null,
            1);
    }

    public static GoalActivation Restore(
        ActivationId id,
        GoalVersionId goalVersionId,
        OperatorId operatorId,
        GoalActivationState state,
        string activationEvidence,
        string authoritySummary,
        DateTimeOffset activationTime,
        DateTimeOffset? expiryTime,
        DateTimeOffset? revocationTime,
        string? revocationReason,
        int revision)
    {
        var activation = Activate(
            id,
            goalVersionId,
            operatorId,
            activationEvidence,
            authoritySummary,
            activationTime,
            expiryTime);
        if (revision < 1)
        {
            throw new DomainRuleViolationException("The Goal activation revision is invalid.");
        }

        if (state == GoalActivationState.INACTIVE)
        {
            throw new DomainRuleViolationException("A stored Goal activation cannot be INACTIVE.");
        }

        if (state == GoalActivationState.REVOKED
            && (revocationTime is null || string.IsNullOrWhiteSpace(revocationReason)))
        {
            throw new DomainRuleViolationException("A revoked Goal activation needs revocation evidence.");
        }

        if (state != GoalActivationState.REVOKED
            && (revocationTime is not null || revocationReason is not null))
        {
            throw new DomainRuleViolationException("Only a revoked Goal activation can contain revocation evidence.");
        }

        DateTimeOffset? checkedRevocationTime = revocationTime is null
            ? null
            : UtcTimestamp.Require(revocationTime.Value, nameof(revocationTime));
        if (checkedRevocationTime < activation.ActivationTime)
        {
            throw new DomainRuleViolationException("Goal activation revocation cannot precede activation.");
        }

        return activation with
        {
            State = state,
            RevocationTime = checkedRevocationTime,
            RevocationReason = revocationReason,
            Revision = revision
        };
    }

    public GoalActivation Pause() => Transition(GoalActivationState.PAUSED);

    public GoalActivation Resume() => Transition(GoalActivationState.ACTIVE);

    public GoalActivation Expire() => Transition(GoalActivationState.EXPIRED);

    public GoalActivation Revoke(DateTimeOffset revokedAt, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainRuleViolationException("A revocation reason is required.");
        }

        var checkedRevokedAt = UtcTimestamp.Require(revokedAt, nameof(revokedAt));
        if (checkedRevokedAt < ActivationTime)
        {
            throw new DomainRuleViolationException("Goal activation revocation cannot precede activation.");
        }

        var transitioned = Transition(GoalActivationState.REVOKED);
        return transitioned with
        {
            RevocationTime = checkedRevokedAt,
            RevocationReason = reason
        };
    }

    public bool CanStartSession(DateTimeOffset at)
    {
        var checkedAt = UtcTimestamp.Require(at, nameof(at));
        return State == GoalActivationState.ACTIVE
            && (ExpiryTime is null || checkedAt < ExpiryTime.Value);
    }

    private GoalActivation Transition(GoalActivationState next)
    {
        var valid = (State, next) switch
        {
            (GoalActivationState.ACTIVE, GoalActivationState.PAUSED) => true,
            (GoalActivationState.PAUSED, GoalActivationState.ACTIVE) => true,
            (GoalActivationState.ACTIVE, GoalActivationState.REVOKED) => true,
            (GoalActivationState.PAUSED, GoalActivationState.REVOKED) => true,
            (GoalActivationState.ACTIVE, GoalActivationState.EXPIRED) => true,
            (GoalActivationState.PAUSED, GoalActivationState.EXPIRED) => true,
            _ => false
        };

        if (!valid)
        {
            throw new DomainRuleViolationException("The Goal activation transition is invalid.");
        }

        return this with { State = next, Revision = checked(Revision + 1) };
    }
}
