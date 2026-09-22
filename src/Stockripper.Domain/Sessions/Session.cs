using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;

namespace Stockripper.Domain.Sessions;

public enum SessionState
{
    CREATED,
    PREFLIGHT,
    READY,
    RUNNING,
    STOPPING,
    RECONCILING,
    COMPLETED,
    FAILED,
    LOCKED
}

public sealed record Session
{
    private Session(
        SessionId id,
        GoalVersionId goalVersionId,
        ActivationId activationId,
        ExecutionMode mode,
        DateTimeOffset startTime,
        SessionState state,
        DateTimeOffset? endTime,
        string? failureReason,
        DataSnapshotId? inputDataSnapshotId,
        long? replaySeed,
        BrokerEnvironment? observedEnvironment,
        string? observedEndpointIdentity,
        string? observedAccountIdentifier,
        int revision)
    {
        Id = id;
        GoalVersionId = goalVersionId;
        ActivationId = activationId;
        Mode = mode;
        StartTime = startTime;
        State = state;
        EndTime = endTime;
        FailureReason = failureReason;
        InputDataSnapshotId = inputDataSnapshotId;
        ReplaySeed = replaySeed;
        ObservedEnvironment = observedEnvironment;
        ObservedEndpointIdentity = observedEndpointIdentity;
        ObservedAccountIdentifier = observedAccountIdentifier;
        Revision = revision;
    }

    public SessionId Id { get; }

    public GoalVersionId GoalVersionId { get; }

    public ActivationId ActivationId { get; }

    public ExecutionMode Mode { get; }

    public DateTimeOffset StartTime { get; }

    public SessionState State { get; private init; }

    public DateTimeOffset? EndTime { get; private init; }

    public string? FailureReason { get; private init; }

    public DataSnapshotId? InputDataSnapshotId { get; }

    public long? ReplaySeed { get; }

    public BrokerEnvironment? ObservedEnvironment { get; private init; }

    public string? ObservedEndpointIdentity { get; private init; }

    public string? ObservedAccountIdentifier { get; private init; }

    public int Revision { get; private init; }

    public static Session Create(
        SessionId id,
        GoalVersion goalVersion,
        GoalActivation activation,
        DateTimeOffset startTime,
        DataSnapshotId? inputDataSnapshotId = null,
        long? replaySeed = null)
    {
        var checkedStartTime = UtcTimestamp.Require(startTime, nameof(startTime));

        if (id.Value == Guid.Empty)
        {
            throw new DomainRuleViolationException("The Session identifier is empty.");
        }

        if (activation.GoalVersionId != goalVersion.Id)
        {
            throw new DomainRuleViolationException("The Goal activation does not match the Goal version.");
        }

        if (!activation.CanStartSession(checkedStartTime))
        {
            throw new DomainRuleViolationException("The Goal activation cannot start a Session.");
        }

        if (!goalVersion.HasValidContentDigest())
        {
            throw new DomainRuleViolationException("The Goal version digest is invalid.");
        }

        if (goalVersion.Mode == ExecutionMode.REPLAY
            && (inputDataSnapshotId is null || replaySeed is null))
        {
            throw new DomainRuleViolationException("A REPLAY Session needs its data snapshot and seed.");
        }

        if (goalVersion.Mode == ExecutionMode.RESEARCH
            && (inputDataSnapshotId is null || replaySeed is not null))
        {
            throw new DomainRuleViolationException("A RESEARCH Session needs one data snapshot and no Replay seed.");
        }

        if (goalVersion.Mode.IsExecutable()
            && (inputDataSnapshotId is null || replaySeed is not null))
        {
            throw new DomainRuleViolationException(
                "An executable Session needs one data snapshot and no Replay seed.");
        }

        return new Session(
            id,
            goalVersion.Id,
            activation.Id,
            goalVersion.Mode,
            checkedStartTime,
            SessionState.CREATED,
            null,
            null,
            inputDataSnapshotId,
            replaySeed,
            null,
            null,
            null,
            1);
    }

    public static Session Restore(
        SessionId id,
        GoalVersionId goalVersionId,
        ActivationId activationId,
        ExecutionMode mode,
        DateTimeOffset startTime,
        SessionState state,
        DateTimeOffset? endTime,
        string? failureReason,
        DataSnapshotId? inputDataSnapshotId,
        long? replaySeed,
        int revision,
        BrokerEnvironment? observedEnvironment = null,
        string? observedEndpointIdentity = null,
        string? observedAccountIdentifier = null)
    {
        if (revision < 1)
        {
            throw new DomainRuleViolationException("The Session revision is invalid.");
        }

        var checkedStartTime = UtcTimestamp.Require(startTime, nameof(startTime));
        if (mode == ExecutionMode.REPLAY && (inputDataSnapshotId is null || replaySeed is null))
        {
            throw new DomainRuleViolationException("A REPLAY Session needs its data snapshot and seed.");
        }

        if (mode == ExecutionMode.RESEARCH && (inputDataSnapshotId is null || replaySeed is not null))
        {
            throw new DomainRuleViolationException("A RESEARCH Session needs one data snapshot and no Replay seed.");
        }

        if (mode.IsExecutable() && (inputDataSnapshotId is null || replaySeed is not null))
        {
            throw new DomainRuleViolationException(
                "An executable Session needs one data snapshot and no Replay seed.");
        }

        var observedValues = new object?[]
        {
            observedEnvironment,
            observedEndpointIdentity,
            observedAccountIdentifier
        };
        var hasAnyObservation = observedValues.Any(static value => value is not null);
        var hasCompleteObservation = observedValues.All(static value => value is not null);
        if (hasAnyObservation != hasCompleteObservation
            || (!mode.IsExecutable() && hasAnyObservation)
            || (mode.IsExecutable()
                && state is SessionState.READY
                    or SessionState.RUNNING
                    or SessionState.STOPPING
                    or SessionState.RECONCILING
                    or SessionState.COMPLETED
                && !hasCompleteObservation))
        {
            throw new DomainRuleViolationException("The Session Broker observation is invalid.");
        }

        var terminal = state is SessionState.COMPLETED or SessionState.FAILED;
        if (terminal != (endTime is not null))
        {
            throw new DomainRuleViolationException("The Session terminal time does not match its state.");
        }

        if (state == SessionState.FAILED && string.IsNullOrWhiteSpace(failureReason))
        {
            throw new DomainRuleViolationException("A failed Session needs a failure reason.");
        }

        if (state != SessionState.FAILED && failureReason is not null)
        {
            throw new DomainRuleViolationException("Only a failed Session can contain a failure reason.");
        }

        DateTimeOffset? checkedEndTime = endTime is null
            ? null
            : UtcTimestamp.Require(endTime.Value, nameof(endTime));
        if (checkedEndTime < checkedStartTime)
        {
            throw new DomainRuleViolationException("The Session end cannot precede its start.");
        }

        return new Session(
            id,
            goalVersionId,
            activationId,
            mode,
            checkedStartTime,
            state,
            checkedEndTime,
            failureReason,
            inputDataSnapshotId,
            replaySeed,
            observedEnvironment,
            observedEndpointIdentity,
            observedAccountIdentifier,
            revision);
    }

    public Session TransitionTo(
        SessionState next,
        DateTimeOffset at,
        string? failureReason = null)
    {
        var checkedAt = UtcTimestamp.Require(at, nameof(at));

        if (checkedAt < StartTime)
        {
            throw new DomainRuleViolationException("A Session transition cannot precede its start.");
        }

        var valid = (State, next) switch
        {
            (SessionState.CREATED, SessionState.PREFLIGHT) => true,
            (SessionState.PREFLIGHT, SessionState.READY) when !Mode.IsExecutable() => true,
            (SessionState.READY, SessionState.RUNNING) => true,
            (SessionState.RUNNING, SessionState.COMPLETED) when !Mode.IsExecutable() => true,
            (SessionState.RUNNING, SessionState.STOPPING) => true,
            (SessionState.STOPPING, SessionState.RECONCILING) => true,
            (SessionState.RECONCILING, SessionState.COMPLETED) => true,
            (SessionState.LOCKED, SessionState.PREFLIGHT) => true,
            (_, SessionState.FAILED) when IsRecoverable(State) => true,
            (_, SessionState.LOCKED) when IsActive(State) => true,
            _ => false
        };

        if (!valid)
        {
            throw new DomainRuleViolationException("The Session transition is invalid.");
        }

        if (next == SessionState.FAILED && string.IsNullOrWhiteSpace(failureReason))
        {
            throw new DomainRuleViolationException("A failed Session needs a failure reason.");
        }

        DateTimeOffset? terminalTime = next is SessionState.COMPLETED or SessionState.FAILED
            ? checkedAt
            : null;
        return this with
        {
            State = next,
            EndTime = terminalTime,
            FailureReason = next == SessionState.FAILED ? failureReason : null,
            Revision = checked(Revision + 1)
        };
    }

    public Session CompleteExecutablePreflight(
        BrokerEnvironment observedEnvironment,
        string observedEndpointIdentity,
        string observedAccountIdentifier,
        DateTimeOffset at)
    {
        var checkedAt = UtcTimestamp.Require(at, nameof(at));
        var expectedEnvironment = Mode.RequiredBrokerEnvironment();
        if (!Mode.IsExecutable()
            || State != SessionState.PREFLIGHT
            || checkedAt < StartTime
            || expectedEnvironment != observedEnvironment
            || string.IsNullOrWhiteSpace(observedEndpointIdentity)
            || observedEndpointIdentity != observedEndpointIdentity.Trim()
            || string.IsNullOrWhiteSpace(observedAccountIdentifier)
            || observedAccountIdentifier != observedAccountIdentifier.Trim())
        {
            throw new DomainRuleViolationException("The executable Session preflight result is invalid.");
        }

        return this with
        {
            State = SessionState.READY,
            ObservedEnvironment = observedEnvironment,
            ObservedEndpointIdentity = observedEndpointIdentity,
            ObservedAccountIdentifier = observedAccountIdentifier,
            Revision = checked(Revision + 1)
        };
    }

    public Session FailExecutablePreflight(
        BrokerEnvironment? observedEnvironment,
        string? observedEndpointIdentity,
        string? observedAccountIdentifier,
        DateTimeOffset at,
        string failureReason)
    {
        if (!Mode.IsExecutable() || State != SessionState.PREFLIGHT)
        {
            throw new DomainRuleViolationException("Only executable preflight can use this failure path.");
        }

        var values = new object?[]
        {
            observedEnvironment,
            observedEndpointIdentity,
            observedAccountIdentifier
        };
        if (values.Any(static value => value is not null)
            != values.All(static value => value is not null))
        {
            throw new DomainRuleViolationException("The failed preflight observation is incomplete.");
        }

        var failed = TransitionTo(SessionState.FAILED, at, failureReason);
        return failed with
        {
            ObservedEnvironment = observedEnvironment,
            ObservedEndpointIdentity = observedEndpointIdentity,
            ObservedAccountIdentifier = observedAccountIdentifier
        };
    }

    private static bool IsActive(SessionState state) => state is
        SessionState.CREATED
        or SessionState.PREFLIGHT
        or SessionState.READY
        or SessionState.RUNNING
        or SessionState.STOPPING
        or SessionState.RECONCILING;

    private static bool IsRecoverable(SessionState state) => IsActive(state) || state == SessionState.LOCKED;
}
