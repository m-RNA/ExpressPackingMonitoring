using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoDatabaseTests
{
    [Fact]
    public void OrderIdExistsRecent_ChecksThirtyDaysAndIgnoresDeletedOrExcludedRecords()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            AddCompleted(database, "RECENT", "发货", Path.Combine(tempDirectory, "recent.mp4"), DateTime.Now.AddDays(-29));
            AddCompleted(database, "OLD", "发货", Path.Combine(tempDirectory, "old.mp4"), DateTime.Now.AddDays(-31));
            string deletedPath = Path.Combine(tempDirectory, "deleted.mp4");
            AddCompleted(database, "DELETED", "发货", deletedPath, DateTime.Now.AddDays(-1));
            database.MarkVideoDeleted(deletedPath, "测试");
            long excludedId = database.InsertVideoRecord("EXCLUDED", "发货", "", "", Path.Combine(tempDirectory, "excluded.mp4"), DateTime.Now);

            Assert.True(database.OrderIdExistsRecent("RECENT"));
            Assert.False(database.OrderIdExistsRecent("OLD"));
            Assert.False(database.OrderIdExistsRecent("DELETED"));
            Assert.False(database.OrderIdExistsRecent("EXCLUDED", excludedId));
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void GetRecentOrderInfos_UsesDatabaseAsNinetyDaySourceOfTruth()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "videos.db");
            using (var database = new VideoDatabase(databasePath))
            {
                database.UpsertOrderInfos(new[]
                {
                    new OrderInfo { TrackingNumber = " recent ", IsPrintedRefund = true, RefundStatus = "SUCCESS", PushTime = DateTime.Now.AddDays(-89) },
                    new OrderInfo { TrackingNumber = "OLD", IsPrintedRefund = true, PushTime = DateTime.Now.AddDays(-91) }
                });
            }

            using var reopened = new VideoDatabase(databasePath);
            List<OrderInfo> records = reopened.GetRecentOrderInfos();

            OrderInfo recent = Assert.Single(records);
            Assert.Equal(" recent ", recent.TrackingNumber);
            Assert.True(recent.IsPrintedRefund);
            Assert.Equal("SUCCESS", recent.RefundStatus);
            Assert.DoesNotContain(records, item => item.TrackingNumber == "OLD");
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void UpsertOrderInfos_DoesNotLetOlderSnapshotOverwriteNewerRefundState()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            database.UpsertOrderInfos(new[]
            {
                new OrderInfo { TrackingNumber = "TRACK-1", IsPrintedRefund = true, RefundStatus = "SUCCESS", PushTime = DateTime.Now }
            });
            database.UpsertOrderInfos(new[]
            {
                new OrderInfo { TrackingNumber = "TRACK-1", IsPrintedRefund = false, RefundStatus = "NO_REFUND", PushTime = DateTime.Now.AddDays(-1) }
            });

            OrderInfo record = Assert.Single(database.GetRecentOrderInfos());
            Assert.True(record.IsPrintedRefund);
            Assert.Equal("SUCCESS", record.RefundStatus);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void VideoRecords_DerivesFileNameFromPathAndDoesNotPersistRedundantColumn()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "videos.db");
            string videoPath = Path.Combine(tempDirectory, "订单-ABC.mp4");
            using (var database = new VideoDatabase(databasePath))
            {
                long id = database.InsertVideoRecord("ORDER", "发货", "", "", videoPath, DateTime.Now);
                VideoRecord record = database.GetVideoById(id);
                Assert.Equal("订单-ABC.mp4", record.FileName);
                Assert.Contains(database.QueryVideos(null, null, "订单-ABC"), item => item.Id == id);
            }

            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info('VideoRecords');";
            using var reader = command.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read()) columns.Add(reader.GetString(1));
            Assert.DoesNotContain("FileName", columns);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void GetRecentCompletedVideos_ReturnsLatestTwentyValidRecordsForDate()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"ExpressPackingMonitoringTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string databasePath = Path.Combine(tempDirectory, "videos.db");
            DateTime date = new(2026, 7, 11);
            using (var database = new VideoDatabase(databasePath))
            {
                AddCompleted(database, "YESTERDAY", "发货", Path.Combine(tempDirectory, "yesterday.mp4"), date.AddDays(-1).AddHours(23));

                for (int index = 0; index < 22; index++)
                {
                    AddCompleted(
                        database,
                        $"TODAY-{index:00}",
                        index % 2 == 0 ? "发货" : "退货",
                        Path.Combine(tempDirectory, $"today-{index:00}.mp4"),
                        date.AddHours(8).AddMinutes(index));
                }

                string deletedPath = Path.Combine(tempDirectory, "deleted.mp4");
                AddCompleted(database, "DELETED", "发货", deletedPath, date.AddHours(22));
                database.MarkVideoDeleted(deletedPath, "测试清理");
                database.InsertVideoRecord("INCOMPLETE", "退货", "", "", Path.Combine(tempDirectory, "incomplete.mp4"), date.AddHours(23));

                List<VideoRecord> records = database.GetRecentCompletedVideos(date, 20);

                Assert.Equal(20, records.Count);
                Assert.Equal("TODAY-21", records[0].OrderId);
                Assert.Equal("TODAY-02", records[^1].OrderId);
                Assert.Equal("退货", records[0].Mode);
                Assert.DoesNotContain(records, record => record.OrderId is "YESTERDAY" or "DELETED" or "INCOMPLETE");
                Assert.True(records.SequenceEqual(records.OrderByDescending(record => record.StartTime)));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void MainWindowQueries_CanFilterToPcRecordingsOnly()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            DateTime date = new(2026, 7, 19);
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            AddCompleted(
                database,
                "PC-LOCAL",
                "发货",
                Path.Combine(tempDirectory, "pc.mp4"),
                date.AddHours(9));
            database.InsertMobileBackupRecord(
                "PHONE-REMOTE",
                Path.Combine(tempDirectory, "phone.mp4"),
                2048,
                date.AddHours(10),
                30,
                "phone-1",
                "打包手机",
                "session-1",
                new string('a', 64));

            List<VideoRecord> allRecent = database.GetRecentCompletedVideos(date, 20);
            List<VideoRecord> pcRecent = database.GetRecentCompletedVideos(date, 20, "pc");
            List<DailyStat> allStats = database.GetAggregatedStats(date, date);
            List<DailyStat> pcStats = database.GetAggregatedStats(date, date, "day", "pc");

            Assert.Equal(2, allRecent.Count);
            Assert.Single(pcRecent);
            Assert.Equal("PC-LOCAL", pcRecent[0].OrderId);
            Assert.Equal(2, Assert.Single(allStats).TotalPieces);
            Assert.Equal(1, Assert.Single(pcStats).TotalPieces);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    private static void AddCompleted(VideoDatabase database, string orderId, string mode, string path, DateTime startTime)
    {
        long id = database.InsertVideoRecord(orderId, mode, "", "", path, startTime);
        database.UpdateVideoRecordOnStop(id, startTime.AddMinutes(1), 60, 1024, "手动");
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ExpressPackingMonitoringTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(path, recursive: true);
    }
}
