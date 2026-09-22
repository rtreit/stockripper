using Stockripper.Application.Execution;
using Stockripper.Domain.Common;
using Stockripper.Testing;

namespace Stockripper.Application.Tests;

public sealed class PaperReviewExpiryServiceTests
{
    [Fact]
    public async Task Expiry_uses_the_current_UTC_time_and_exact_actor()
    {
        var now = GoalTestData.ReferenceTime.AddMinutes(3);
        var actor = OperatorId.New();
        var persistence = new RecordingPersistence();
        var service = new PaperReviewExpiryService(
            persistence,
            new ManualTimeProvider(now));

        var count = await service.ExpireDueAsync(actor);

        Assert.Equal(2, count);
        Assert.Equal(now, persistence.At);
        Assert.Equal(actor, persistence.ActorId);
    }

    private sealed class RecordingPersistence : IPaperReviewExpiryPersistence
    {
        public DateTimeOffset? At { get; private set; }
        public OperatorId? ActorId { get; private set; }

        public ValueTask<int> ExpireDueAsync(
            DateTimeOffset at,
            OperatorId actorId,
            CancellationToken cancellationToken)
        {
            At = at;
            ActorId = actorId;
            return ValueTask.FromResult(2);
        }
    }
}
