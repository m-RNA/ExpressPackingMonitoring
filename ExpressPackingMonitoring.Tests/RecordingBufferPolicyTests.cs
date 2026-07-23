using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingBufferPolicyTests
{
    [Theory]
    [InlineData(1920, 1080, 60, 32)]
    [InlineData(1280, 720, 15, 15)]
    [InlineData(3840, 2160, 60, 8)]
    [InlineData(640, 480, 120, 60)]
    public void CalculateVideoQueueCapacity_RespectsMemoryTimeAndFrameLimits(
        int width,
        int height,
        int fps,
        int expectedCapacity)
    {
        Assert.Equal(expectedCapacity, RecordingBufferPolicy.CalculateVideoQueueCapacity(width, height, fps));
    }

    [Fact]
    public void CalculateVideoQueueCapacity_InvalidFpsUsesFallback()
    {
        Assert.Equal(15, RecordingBufferPolicy.CalculateVideoQueueCapacity(1280, 720, 0));
    }
}
