using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class SessionFactoryTests
{
    [Fact]
    public void Session_start_uses_the_injected_clock()
    {
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime.AddMinutes(1));
        var goalVersion = GoalTestData.CreateGoalVersion(ExecutionMode.RESEARCH);
        var activation = GoalTestData.CreateActivation(goalVersion);
        var factory = new SessionFactory(clock);

        var session = factory.Create(goalVersion, activation, DataSnapshotId.New());

        Assert.Equal(clock.GetUtcNow(), session.StartTime);
    }
}
