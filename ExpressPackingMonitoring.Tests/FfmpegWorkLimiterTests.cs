using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class FfmpegWorkLimiterTests
{
    [Fact]
    public async Task Enter_BlocksWorkBeyondConfiguredConcurrency()
    {
        var limiter = new FfmpegWorkLimiter(2);
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;
        using IDisposable first = limiter.Enter(testCancellationToken);
        using IDisposable second = limiter.Enter(testCancellationToken);

        var thirdEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task thirdTask = Task.Run(() =>
        {
            using IDisposable third = limiter.Enter(testCancellationToken);
            thirdEntered.TrySetResult();
        }, testCancellationToken);

        await Task.Delay(50, testCancellationToken);
        Assert.False(thirdEntered.Task.IsCompleted);

        first.Dispose();
        await thirdEntered.Task.WaitAsync(TimeSpan.FromSeconds(1), testCancellationToken);
        await thirdTask;
    }

    [Fact]
    public void Enter_CanceledWaitDoesNotConsumeSlot()
    {
        var limiter = new FfmpegWorkLimiter(1);
        using IDisposable first = limiter.Enter(TestContext.Current.CancellationToken);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Throws<OperationCanceledException>(() => limiter.Enter(canceled.Token));

        first.Dispose();
        using IDisposable next = limiter.Enter(TestContext.Current.CancellationToken);
    }
}
