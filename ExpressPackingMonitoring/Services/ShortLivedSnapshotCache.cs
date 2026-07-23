namespace ExpressPackingMonitoring.Services;

internal sealed class ShortLivedSnapshotCache<T> where T : class
{
    private readonly object _sync = new();
    private readonly TimeSpan _lifetime;
    private T? _value;
    private DateTimeOffset _expiresAt;

    public ShortLivedSnapshotCache(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));

        _lifetime = lifetime;
    }

    public T GetOrCreate(Func<T> factory) =>
        GetOrCreate(factory, DateTimeOffset.UtcNow);

    internal T GetOrCreate(Func<T> factory, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (_sync)
        {
            if (_value != null && now < _expiresAt)
                return _value;

            T value = factory() ?? throw new InvalidOperationException("缓存构建结果不能为空");
            _value = value;
            _expiresAt = now + _lifetime;
            return value;
        }
    }
}
