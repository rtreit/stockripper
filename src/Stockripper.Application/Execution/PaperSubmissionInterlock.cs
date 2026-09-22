namespace Stockripper.Application.Execution;

public interface IPaperSubmissionInterlock
{
    ValueTask<IAsyncDisposable> EnterAsync(
        CancellationToken cancellationToken = default);
}

public sealed class PaperSubmissionInterlock : IPaperSubmissionInterlock, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask<IAsyncDisposable> EnterAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    public void Dispose() => gate.Dispose();

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? ownedGate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref ownedGate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
