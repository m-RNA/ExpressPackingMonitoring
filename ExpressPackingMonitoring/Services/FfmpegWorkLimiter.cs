namespace ExpressPackingMonitoring.Services;

internal sealed class FfmpegWorkLimiter
{
    internal const int DefaultMaximumConcurrency = 2;
    private readonly SemaphoreSlim _slots;

    public FfmpegWorkLimiter(int maximumConcurrency = DefaultMaximumConcurrency)
    {
        if (maximumConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));

        _slots = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
    }

    public IDisposable Enter(CancellationToken cancellationToken)
    {
        _slots.Wait(cancellationToken);
        return new Lease(_slots);
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _owner;

        public Lease(SemaphoreSlim owner) => _owner = owner;

        public void Dispose()
        {
            SemaphoreSlim? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }
}
