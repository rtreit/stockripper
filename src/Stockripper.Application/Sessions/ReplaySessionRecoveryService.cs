using Stockripper.Domain.Common;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Sessions;

public sealed class ReadOnlySessionRecoveryService(
    ISessionPersistence sessionPersistence,
    TimeProvider timeProvider)
{
    public async ValueTask<int> RecoverAsync(
        OperatorId actorId,
        CancellationToken cancellationToken = default)
    {
        var interrupted = await sessionPersistence
            .FindInterruptedReadOnlyAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var session in interrupted)
        {
            var failed = session.TransitionTo(
                SessionState.FAILED,
                timeProvider.GetUtcNow(),
                "Application restart interrupted the read-only Session.");
            await sessionPersistence
                .UpdateAsync(
                    failed,
                    session.State,
                    session.Revision,
                    actorId,
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return interrupted.Count;
    }
}
