using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraLifecycleTests
{
    [Theory]
    [InlineData(false, 60, 15, 24)]
    [InlineData(false, 10, 10, 10)]
    [InlineData(true, 60, 60, 60)]
    [InlineData(true, 0, 15, 15)]
    public void CameraFrameProcessingPolicy_PreservesRecordingFpsAndSeparatesIdleCaptureFromProcessing(
        bool isRecording,
        int actualCameraFps,
        int expectedCaptureFps,
        int expectedProcessingFps)
    {
        Assert.Equal(expectedCaptureFps, CameraFrameProcessingPolicy.GetCaptureFps(isRecording, actualCameraFps));
        Assert.Equal(expectedProcessingFps, CameraFrameProcessingPolicy.GetProcessingFps(isRecording, actualCameraFps));
    }

    [Fact]
    public void CameraFrameRateGate_ThrottlesIdleFramesButAcceptsEveryRecordingFrame()
    {
        var gate = new CameraFrameRateGate();
        const long frequency = 1_000;

        Assert.True(gate.ShouldAccept(false, 60, 1_000, frequency));
        Assert.False(gate.ShouldAccept(false, 60, 1_050, frequency));
        Assert.True(gate.ShouldAccept(false, 60, 1_066, frequency));

        Assert.True(gate.ShouldAccept(true, 60, 1_067, frequency));
        Assert.True(gate.ShouldAccept(true, 60, 1_068, frequency));
    }

    [Fact]
    public void PreviewSessionGate_StaleCallbackCannotReleaseAwakenedSession()
    {
        var gate = new PreviewSessionGate();
        int sleepingSession = gate.BeginSession();
        Assert.True(gate.TryAcquire(out int oldCallbackSession));
        Assert.Equal(sleepingSession, oldCallbackSession);

        int awakenedSession = gate.BeginSession();
        Assert.NotEqual(sleepingSession, awakenedSession);
        Assert.True(gate.TryAcquire(out int awakenedCallbackSession));

        gate.Release(oldCallbackSession);

        Assert.True(gate.IsPending);
        Assert.False(gate.TryAcquire(out _));
        gate.Release(awakenedCallbackSession);
        Assert.False(gate.IsPending);
        Assert.True(gate.TryAcquire(out int nextCallbackSession));
        Assert.Equal(awakenedSession, nextCallbackSession);
    }

    [Fact]
    public async Task CameraFrameReadySignal_WakeRequiresNewSessionFrame()
    {
        var signal = new CameraFrameReadySignal();
        signal.Signal();
        Assert.True(await signal.WaitAsync(TimeSpan.FromMilliseconds(20)));

        signal.BeginSession();
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(20)));

        Task<bool> awakenedFrame = signal.WaitAsync(TimeSpan.FromSeconds(1));
        signal.Signal();

        Assert.True(await awakenedFrame);
    }

    [Fact]
    public async Task CameraFrameReadySignal_RecordingStartTimesOutWithoutFrame()
    {
        var signal = new CameraFrameReadySignal();
        signal.BeginSession();

        bool ready = await signal.WaitAsync(TimeSpan.FromMilliseconds(30));

        Assert.False(ready);
    }
}
