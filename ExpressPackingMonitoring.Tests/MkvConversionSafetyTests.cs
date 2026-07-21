using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MkvConversionSafetyTests
{
    [Fact]
    public void IsCompletedConcurrentMkvConversion_AcceptsNonEmptyMp4AfterMkvWasRemoved()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string mkvPath = Path.Combine(directory, "recording.mkv");
            string mp4Path = Path.Combine(directory, "recording.mp4");
            File.WriteAllBytes(mp4Path, [1, 2, 3]);

            Assert.True(MainViewModel.IsCompletedConcurrentMkvConversion(mkvPath, mp4Path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IsCompletedConcurrentMkvConversion_RejectsOutputWhileSourceStillExists()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string mkvPath = Path.Combine(directory, "recording.mkv");
            string mp4Path = Path.Combine(directory, "recording.mp4");
            File.WriteAllBytes(mkvPath, [1]);
            File.WriteAllBytes(mp4Path, [1, 2, 3]);

            Assert.False(MainViewModel.IsCompletedConcurrentMkvConversion(mkvPath, mp4Path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IsCompletedConcurrentMkvConversion_RejectsEmptyOrMissingOutput()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string mkvPath = Path.Combine(directory, "recording.mkv");
            string mp4Path = Path.Combine(directory, "recording.mp4");
            File.WriteAllBytes(mp4Path, []);

            Assert.False(MainViewModel.IsCompletedConcurrentMkvConversion(mkvPath, mp4Path));
            File.Delete(mp4Path);
            Assert.False(MainViewModel.IsCompletedConcurrentMkvConversion(mkvPath, mp4Path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ExpressPackingMonitoring.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
