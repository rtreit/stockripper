using Stockripper.Domain.Common;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Sessions;

public interface ISessionPersistence
{
    ValueTask InsertAsync(
        Session session,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<Session?> FindAsync(
        SessionId sessionId,
        CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        Session session,
        SessionState expectedState,
        int expectedRevision,
        OperatorId actorId,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<Session>> FindInterruptedReadOnlyAsync(
        CancellationToken cancellationToken);
}

public sealed class SessionConcurrencyException : InvalidOperationException
{
    public SessionConcurrencyException()
        : base("The Session changed before this operation completed.")
    {
    }
}
