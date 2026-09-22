using Stockripper.Application.Execution;
using Stockripper.Application.Sessions;
using Stockripper.Domain.Common;
using Stockripper.Domain.Execution;
using Stockripper.Domain.Goals;
using Stockripper.Domain.Sessions;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class PaperExecutionStartupRecoveryServiceTests
{
    [Fact]
    public async Task Startup_locks_sessions_then_recovers_external_work_and_reconciles()
    {
        var calls = new List<string>();
        var goal = GoalTestData.CreateGoalVersion(ExecutionMode.PAPER_REVIEW);
        var session = Session.Create(SessionId.New(), goal,
                GoalTestData.CreateActivation(goal), GoalTestData.ReferenceTime,
                DataSnapshotId.New())
            .TransitionTo(SessionState.PREFLIGHT, GoalTestData.ReferenceTime)
            .CompleteExecutablePreflight(BrokerEnvironment.PAPER,
                GoalTestData.Endpoint(BrokerEnvironment.PAPER),
                GoalTestData.Account(BrokerEnvironment.PAPER),
                GoalTestData.ReferenceTime);
        var sessions = new SessionRecoveryPersistence(session, calls);
        var service = new PaperExecutionStartupRecoveryService(
            new ExecutableSessionRecoveryService(sessions, sessions,
                new ManualTimeProvider(GoalTestData.ReferenceTime.AddMinutes(1))),
            new SubmissionRecovery(calls), new CancellationRecovery(calls),
            new ReconciliationRecovery(calls));

        var outcome = await service.RecoverAsync(OperatorId.New());

        Assert.Equal(["sessions", "cancellations", "submissions", "reconciliation"], calls);
        Assert.Equal(SessionState.LOCKED, sessions.Current.State);
        Assert.Equal(4, outcome.TotalCount);
    }

    private sealed class ReconciliationRecovery(List<string> calls)
        : IPaperReconciliationRecovery
    {
        public ValueTask<PaperReconciliationRecoveryOutcome> RecoverAsync(
            OperatorId actorId, CancellationToken cancellationToken = default)
        {
            calls.Add("reconciliation");
            return ValueTask.FromResult(new PaperReconciliationRecoveryOutcome(1, 1, 0, 0));
        }
    }

    private sealed class SubmissionRecovery(List<string> calls) : IPaperSubmissionRecovery
    {
        public ValueTask<IReadOnlyList<OrderRecord>> RecoverAsync(OperatorId actorId,
            CancellationToken cancellationToken = default)
        {
            calls.Add("submissions");
            return ValueTask.FromResult<IReadOnlyList<OrderRecord>>([null!]);
        }
    }

    private sealed class CancellationRecovery(List<string> calls) : IPaperCancellationRecovery
    {
        public ValueTask<IReadOnlyList<CancellationRequest>> RecoverAsync(OperatorId actorId,
            CancellationToken cancellationToken = default)
        {
            calls.Add("cancellations");
            return ValueTask.FromResult<IReadOnlyList<CancellationRequest>>([null!]);
        }
    }

    private sealed class SessionRecoveryPersistence(Session session, List<string> calls)
        : ISessionPersistence, IExecutableSessionRecoveryPersistence
    {
        public Session Current { get; private set; } = session;

        public ValueTask<IReadOnlyList<Session>> FindInterruptedExecutableAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<Session>>([Current]);

        public ValueTask UpdateAsync(Session value, SessionState expectedState,
            int expectedRevision, OperatorId actorId, DateTimeOffset changedAt,
            CancellationToken cancellationToken)
        {
            calls.Add("sessions");
            Current = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask InsertAsync(Session value, OperatorId actorId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Session?> FindAsync(SessionId sessionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<Session>> FindInterruptedReadOnlyAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
