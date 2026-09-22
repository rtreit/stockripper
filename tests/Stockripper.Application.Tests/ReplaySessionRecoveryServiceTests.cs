using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Sessions;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class ReadOnlySessionRecoveryServiceTests
{
    [Fact]
    public async Task Interrupted_replay_session_recovers_to_auditable_failure()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.REPLAY);
        var activation = GoalTestData.CreateActivation(goal);
        var session = Session.Create(
                SessionId.New(),
                goal,
                activation,
                GoalTestData.ReferenceTime,
                DataSnapshotId.New(),
                42)
            .TransitionTo(SessionState.PREFLIGHT, GoalTestData.ReferenceTime)
            .TransitionTo(SessionState.READY, GoalTestData.ReferenceTime)
            .TransitionTo(SessionState.RUNNING, GoalTestData.ReferenceTime);
        var persistence = new RecoveryPersistence(session);
        var service = new ReadOnlySessionRecoveryService(
            persistence,
            new ManualTimeProvider(GoalTestData.ReferenceTime.AddMinutes(1)));

        var recovered = await service.RecoverAsync(OperatorId.New());

        Assert.Equal(1, recovered);
        Assert.Equal(SessionState.FAILED, persistence.Current.State);
        Assert.Contains("restart", persistence.Current.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecoveryPersistence(Session session) : ISessionPersistence
    {
        public Session Current { get; private set; } = session;

        public ValueTask<IReadOnlyList<Session>> FindInterruptedReadOnlyAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<Session>>([Current]);

        public ValueTask UpdateAsync(
            Session value,
            SessionState expectedState,
            int expectedRevision,
            OperatorId actorId,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Current.State, expectedState);
            Assert.Equal(Current.Revision, expectedRevision);
            Current = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask InsertAsync(
            Session value,
            OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Session?> FindAsync(
            SessionId sessionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
