using Stockripper.Domain.Common;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Sessions;

public sealed class ExecutableSessionRecoveryService(
    IExecutableSessionRecoveryPersistence recoveryPersistence,
    ISessionPersistence sessionPersistence,
    TimeProvider timeProvider)
{
    public async ValueTask<int> RecoverAsync(
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var interrupted = await recoveryPersistence
            .FindInterruptedExecutableAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var session in interrupted)
        {
            var changedAt = timeProvider.GetUtcNow();
            var locked = session.TransitionTo(SessionState.LOCKED, changedAt);
            await sessionPersistence.UpdateAsync(
                locked,
                session.State,
                session.Revision,
                actorId,
                changedAt,
                cancellationToken).ConfigureAwait(false);
        }

        return interrupted.Count;
    }
}
