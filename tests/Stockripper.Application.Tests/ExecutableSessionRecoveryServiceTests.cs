using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Sessions;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class ExecutableSessionRecoveryServiceTests
{
    [Fact]
    public async Task Restart_locks_executable_session_before_new_work()
    {
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var session = Session.Create(
                SessionId.New(),
                goal,
                GoalTestData.CreateActivation(goal),
                GoalTestData.ReferenceTime,
                DataSnapshotId.New())
            .TransitionTo(SessionState.PREFLIGHT, GoalTestData.ReferenceTime)
            .CompleteExecutablePreflight(
                BrokerEnvironment.PAPER,
                GoalTestData.Endpoint(BrokerEnvironment.PAPER),
                GoalTestData.Account(BrokerEnvironment.PAPER),
                GoalTestData.ReferenceTime);
        var persistence = new RecoveryPersistence(session);
        var clock = new ManualTimeProvider(GoalTestData.ReferenceTime.AddMinutes(1));

        var recovered = await new ExecutableSessionRecoveryService(persistence, persistence, clock)
            .RecoverAsync(OperatorId.New());

        Assert.Equal(1, recovered);
        Assert.Equal(SessionState.LOCKED, persistence.Current.State);
    }

    private sealed class RecoveryPersistence(Session session)
        : ISessionPersistence, IExecutableSessionRecoveryPersistence
    {
        public Session Current { get; private set; } = session;

        public ValueTask<IReadOnlyList<Session>> FindInterruptedExecutableAsync(
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
        public ValueTask<IReadOnlyList<Session>> FindInterruptedReadOnlyAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
