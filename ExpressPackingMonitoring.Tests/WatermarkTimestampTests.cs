using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WatermarkTimestampTests
{
    [Theory]
    [InlineData(8, 0, "UTC+08: 2026/07/12 09:10:11")]
    [InlineData(-4, 0, "UTC-04: 2026/07/12 09:10:11")]
    [InlineData(5, 30, "UTC+05:30: 2026/07/12 09:10:11")]
    [InlineData(0, 0, "UTC+00: 2026/07/12 09:10:11")]
    public void FormatWatermarkTimestamp_UsesTimestampOffset(int hours, int minutes, string expected)
    {
        var offset = new TimeSpan(hours, minutes, 0);
        var timestamp = new DateTimeOffset(2026, 7, 12, 9, 10, 11, offset);

        Assert.Equal(expected, MainViewModel.FormatWatermarkTimestamp(timestamp));
    }
}
