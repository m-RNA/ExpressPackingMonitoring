using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ShortLivedSnapshotCacheTests
{
    [Fact]
    public void GetOrCreate_ReusesValueBeforeExpiryAndRefreshesAfterwards()
    {
        var cache = new ShortLivedSnapshotCache<string>(TimeSpan.FromSeconds(10));
        DateTimeOffset start = new(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);
        int buildCount = 0;

        string first = cache.GetOrCreate(() => $"value-{++buildCount}", start);
        string reused = cache.GetOrCreate(() => $"value-{++buildCount}", start.AddSeconds(9));
        string refreshed = cache.GetOrCreate(() => $"value-{++buildCount}", start.AddSeconds(10));

        Assert.Equal("value-1", first);
        Assert.Same(first, reused);
        Assert.Equal("value-2", refreshed);
        Assert.Equal(2, buildCount);
    }

    [Fact]
    public async Task GetOrCreate_ConcurrentMissBuildsOnce()
    {
        var cache = new ShortLivedSnapshotCache<object>(TimeSpan.FromSeconds(10));
        using var buildStarted = new ManualResetEventSlim();
        using var releaseBuild = new ManualResetEventSlim();
        int buildCount = 0;

        object Factory()
        {
            Interlocked.Increment(ref buildCount);
            buildStarted.Set();
            releaseBuild.Wait(TestContext.Current.CancellationToken);
            return new object();
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task<object> firstTask = Task.Factory.StartNew(
            () => cache.GetOrCreate(Factory),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(buildStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken));
        Task<object> secondTask = Task.Factory.StartNew(
            () => cache.GetOrCreate(Factory),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        releaseBuild.Set();
        object[] results = await Task.WhenAll(firstTask, secondTask);

        Assert.Same(results[0], results[1]);
        Assert.Equal(1, buildCount);
    }
}
