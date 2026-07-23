namespace ExpressPackingMonitoring.ViewModels;

internal static class RecordingBufferPolicy
{
    internal const long MaximumQueuedFrameBytes = 192L * 1024 * 1024;
    internal const int MaximumQueuedFrames = 60;
    private const int FallbackFps = 15;

    public static int CalculateVideoQueueCapacity(int width, int height, int fps)
    {
        int safeFps = fps > 0 ? fps : FallbackFps;
        long bytesPerFrame;

        try
        {
            bytesPerFrame = checked((long)Math.Max(1, width) * Math.Max(1, height) * 3);
        }
        catch (OverflowException)
        {
            return 1;
        }

        long memoryLimitedFrames = Math.Max(1, MaximumQueuedFrameBytes / bytesPerFrame);
        int timeLimitedFrames = Math.Clamp(safeFps, 1, MaximumQueuedFrames);
        return (int)Math.Min(memoryLimitedFrames, timeLimitedFrames);
    }
}
