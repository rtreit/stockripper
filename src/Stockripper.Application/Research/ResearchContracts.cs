using Stockripper.Domain.Common;
using Stockripper.Domain.Research;
using Stockripper.Domain.Sessions;

namespace Stockripper.Application.Research;

public interface IResearchPersistence
{
    ValueTask InsertCompletedAsync(
        ResearchResult result,
        Session completedSession,
        int expectedSessionRevision,
        OperatorId actorId,
        CancellationToken cancellationToken);

    ValueTask<ResearchResult?> FindAsync(
        ResearchResultId researchResultId,
        CancellationToken cancellationToken);
}

public sealed class ResearchPreflightException(string message) : InvalidOperationException(message);
