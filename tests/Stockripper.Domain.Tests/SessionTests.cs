using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class SessionTests
{
    public static TheoryData<ExecutionMode> AllModes => new()
    {
        ExecutionMode.REPLAY,
        ExecutionMode.RESEARCH,
        ExecutionMode.PAPER_REVIEW,
        ExecutionMode.PAPER_AUTO,
        ExecutionMode.LIVE_REVIEW,
        ExecutionMode.LIVE_AUTO
    };

    [Theory]
    [MemberData(nameof(AllModes))]
    public void Session_inherits_mode_from_immutable_goal_version(ExecutionMode mode)
    {
        var goalVersion = GoalTestData.CreateGoalVersion(mode);
        var activation = GoalTestData.CreateActivation(goalVersion);

        var session = Session.Create(
            SessionId.New(),
            goalVersion,
            activation,
            GoalTestData.ReferenceTime.AddMinutes(1),
            DataSnapshotId.New(),
            mode == ExecutionMode.REPLAY ? 42 : null);

        Assert.Equal(mode, session.Mode);
        Assert.Equal(goalVersion.Id, session.GoalVersionId);
        Assert.Equal(activation.Id, session.ActivationId);
        Assert.Equal(SessionState.CREATED, session.State);
    }

    [Fact]
    public void Session_rejects_an_activation_for_another_goal_version()
    {
        var goalVersion = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var activation = GoalActivation.Activate(
            ActivationId.New(),
            GoalVersionId.New(),
            OperatorId.New(),
            "other-goal-evidence",
            GoalTestData.ReferenceTime);

        Assert.Throws<DomainRuleViolationException>(() => Session.Create(
            SessionId.New(),
            goalVersion,
            activation,
            GoalTestData.ReferenceTime.AddMinutes(1)));
    }

    [Fact]
    public void Session_requires_the_normal_transition_path()
    {
        var goalVersion = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var activation = GoalTestData.CreateActivation(goalVersion);
        var session = Session.Create(
            SessionId.New(),
            goalVersion,
            activation,
            GoalTestData.ReferenceTime.AddMinutes(1),
            DataSnapshotId.New(),
            42);

        Assert.Throws<DomainRuleViolationException>(() => session.TransitionTo(
            SessionState.RUNNING,
            GoalTestData.ReferenceTime.AddMinutes(2)));

        var completed = session
            .TransitionTo(SessionState.PREFLIGHT, GoalTestData.ReferenceTime.AddMinutes(2))
            .TransitionTo(SessionState.READY, GoalTestData.ReferenceTime.AddMinutes(3))
            .TransitionTo(SessionState.RUNNING, GoalTestData.ReferenceTime.AddMinutes(4))
            .TransitionTo(SessionState.STOPPING, GoalTestData.ReferenceTime.AddMinutes(5))
            .TransitionTo(SessionState.RECONCILING, GoalTestData.ReferenceTime.AddMinutes(6))
            .TransitionTo(SessionState.COMPLETED, GoalTestData.ReferenceTime.AddMinutes(7));

        Assert.Equal(SessionState.COMPLETED, completed.State);
        Assert.Equal(GoalTestData.ReferenceTime.AddMinutes(7), completed.EndTime);
    }

    [Fact]
    public void Paused_revoked_and_expired_activations_cannot_start_a_session()
    {
        var goalVersion = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var active = GoalTestData.CreateActivation(
            goalVersion,
            GoalTestData.ReferenceTime.AddMinutes(2));
        var paused = active.Pause();
        var revoked = active.Revoke(
            GoalTestData.ReferenceTime.AddMinutes(1),
            "Operator revoked authority.");
        var startTime = GoalTestData.ReferenceTime.AddMinutes(3);

        Assert.Throws<DomainRuleViolationException>(
            () => Session.Create(SessionId.New(), goalVersion, paused, startTime));
        Assert.Throws<DomainRuleViolationException>(
            () => Session.Create(SessionId.New(), goalVersion, revoked, startTime));
        Assert.Throws<DomainRuleViolationException>(
            () => Session.Create(SessionId.New(), goalVersion, active, startTime));
    }

    [Fact]
    public void Executable_session_needs_recorded_broker_identity_before_ready()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var activation = GoalTestData.CreateActivation(goal);
        var preflight = Session.Create(
                SessionId.New(),
                goal,
                activation,
                GoalTestData.ReferenceTime,
                DataSnapshotId.New())
            .TransitionTo(SessionState.PREFLIGHT, GoalTestData.ReferenceTime);

        Assert.Throws<DomainRuleViolationException>(() => preflight.TransitionTo(
            SessionState.READY,
            GoalTestData.ReferenceTime));

        var ready = preflight.CompleteExecutablePreflight(
            BrokerEnvironment.PAPER,
            GoalTestData.Endpoint(BrokerEnvironment.PAPER),
            GoalTestData.Account(BrokerEnvironment.PAPER),
            GoalTestData.ReferenceTime);

        Assert.Equal(SessionState.READY, ready.State);
        Assert.Equal(BrokerEnvironment.PAPER, ready.ObservedEnvironment);
        Assert.Equal(GoalTestData.Endpoint(BrokerEnvironment.PAPER), ready.ObservedEndpointIdentity);
        Assert.Equal(GoalTestData.Account(BrokerEnvironment.PAPER), ready.ObservedAccountIdentifier);
    }
}
