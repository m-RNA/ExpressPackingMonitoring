using System.Diagnostics;

namespace ExpressPackingMonitoring.ViewModels;

internal static class CameraFrameProcessingPolicy
{
    internal const int IdleProcessingFps = 15;
    private const int FallbackCameraFps = 15;

    public static int GetTargetFps(bool isRecording, int actualCameraFps)
    {
        int cameraFps = actualCameraFps > 0 ? actualCameraFps : FallbackCameraFps;
        return isRecording ? cameraFps : Math.Min(cameraFps, IdleProcessingFps);
    }
}

internal sealed class CameraFrameRateGate
{
    private long _lastAcceptedTimestamp;

    public void Reset() => Interlocked.Exchange(ref _lastAcceptedTimestamp, 0);

    public bool ShouldAccept(bool isRecording, int actualCameraFps) =>
        ShouldAccept(isRecording, actualCameraFps, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    internal bool ShouldAccept(bool isRecording, int actualCameraFps, long nowTimestamp, long timestampFrequency)
    {
        if (isRecording)
        {
            Interlocked.Exchange(ref _lastAcceptedTimestamp, nowTimestamp);
            return true;
        }

        int targetFps = CameraFrameProcessingPolicy.GetTargetFps(false, actualCameraFps);
        long minimumInterval = Math.Max(1, timestampFrequency / targetFps);

        while (true)
        {
            long previous = Volatile.Read(ref _lastAcceptedTimestamp);
            if (previous != 0 && nowTimestamp - previous < minimumInterval)
                return false;

            if (Interlocked.CompareExchange(ref _lastAcceptedTimestamp, nowTimestamp, previous) == previous)
                return true;
        }
    }
}

internal sealed class PreviewSessionGate
{
    private int _sessionId;
    private int _pending;

    public int CurrentSessionId => Volatile.Read(ref _sessionId);
    public bool IsPending => Volatile.Read(ref _pending) != 0;

    public int BeginSession()
    {
        int sessionId = Interlocked.Increment(ref _sessionId);
        Interlocked.Exchange(ref _pending, 0);
        return sessionId;
    }

    public bool TryAcquire(out int sessionId)
    {
        sessionId = CurrentSessionId;
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
            return false;

        if (sessionId == CurrentSessionId)
            return true;

        Interlocked.Exchange(ref _pending, 0);
        return false;
    }

    public bool IsCurrent(int sessionId) => sessionId == CurrentSessionId;

    public void Release(int sessionId)
    {
        if (IsCurrent(sessionId))
            Interlocked.Exchange(ref _pending, 0);
    }

    public void ClearCurrentPending() => Interlocked.Exchange(ref _pending, 0);
}

internal sealed class CameraFrameReadySignal
{
    private readonly object _sync = new();
    private TaskCompletionSource _source = CreateSource();

    public void BeginSession()
    {
        lock (_sync)
            _source = CreateSource();
    }

    public void Signal()
    {
        TaskCompletionSource source;
        lock (_sync)
            source = _source;
        source.TrySetResult();
    }

    public async Task<bool> WaitAsync(TimeSpan timeout)
    {
        Task task;
        lock (_sync)
            task = _source.Task;

        if (task.IsCompleted)
            return true;

        return await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) == task;
    }

    private static TaskCompletionSource CreateSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
