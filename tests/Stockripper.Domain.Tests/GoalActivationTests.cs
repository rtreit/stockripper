using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Testing;

namespace Stockripper.Domain.Tests;

public sealed class GoalActivationTests
{
    [Fact]
    public void Pause_and_resume_follow_the_explicit_state_model()
    {
        var goalVersion = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var activation = GoalTestData.CreateActivation(goalVersion);

        var paused = activation.Pause();
        var resumed = paused.Resume();

        Assert.Equal(GoalActivationState.ACTIVE, activation.State);
        Assert.Equal(GoalActivationState.PAUSED, paused.State);
        Assert.Equal(GoalActivationState.ACTIVE, resumed.State);
    }

    [Fact]
    public void Revocation_is_terminal()
    {
        var goalVersion = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var activation = GoalTestData.CreateActivation(goalVersion);
        var revoked = activation.Revoke(GoalTestData.ReferenceTime.AddMinutes(1), "Operator revoked authority.");

        Assert.Equal(GoalActivationState.REVOKED, revoked.State);
        Assert.False(revoked.CanStartSession(GoalTestData.ReferenceTime.AddMinutes(2)));
        Assert.Throws<DomainRuleViolationException>(() => revoked.Resume());
    }

    [Fact]
    public void Expired_activation_cannot_start_a_session()
    {
        var goalVersion = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var expiry = GoalTestData.ReferenceTime.AddMinutes(5);
        var activation = GoalTestData.CreateActivation(goalVersion, expiry);

        Assert.True(activation.CanStartSession(expiry.AddTicks(-1)));
        Assert.False(activation.CanStartSession(expiry));
    }
}
