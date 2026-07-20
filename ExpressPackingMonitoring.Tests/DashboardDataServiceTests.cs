using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public class DashboardDataServiceTests
{
    [Fact]
    public void ReadsPendingUploadAndVerifiedMobileVideo()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-dashboard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string stateDirectory = Path.Combine(directory, "state");
            Directory.CreateDirectory(stateDirectory);
            string uploadId = new string('a', 64);
            File.WriteAllText(
                Path.Combine(stateDirectory, $"{uploadId}.json"),
                JsonSerializer.Serialize(new
                {
                    totalBytes = 100L,
                    receivedBytes = 40L,
                    updatedAtUtc = DateTime.UtcNow
                }));
            File.WriteAllBytes(Path.Combine(stateDirectory, $"{uploadId}.part"), new byte[40]);

            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            database.InsertMobileBackupRecord(
                "TRACK-1",
                Path.Combine(directory, "video.mp4"),
                100,
                DateTime.Now,
                2,
                "phone-1",
                "打包手机",
                "session-1",
                new string('b', 64));
            var service = new DashboardDataService(database, stateDirectory);

            MobileUploadDashboardItem upload = Assert.Single(service.GetMobileUploads());
            Assert.Equal(40, upload.ReceivedBytes);
            Assert.Equal(40, upload.ProgressPercent);
            Assert.Equal("正在上传", upload.Status);

            RecentMobileVideoItem video = Assert.Single(service.GetRecentMobileVideos());
            Assert.Equal("TRACK-1", video.TrackingNumber);
            Assert.Equal("打包手机", video.DeviceName);
            Assert.Equal("SHA256 已校验", video.VerificationText);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}
