namespace Edith.Core;

/// <summary>
/// Serializes one mutable local-model session. Callers must release the lease
/// in finally; cancelling an owner lets the next request acquire it as soon as
/// its HTTP/tool cleanup has actually completed.
/// </summary>
public sealed class CancellableRequestGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Lease(_gate);
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Lease(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
