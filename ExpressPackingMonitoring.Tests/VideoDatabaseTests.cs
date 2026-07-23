using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoDatabaseTests
{
    [Fact]
    public void MobileHistory_CountsDeviceDuplicatesAndReturnsDeletedStatuses()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            string firstPath = Path.Combine(tempDirectory, "first.mp4");
            string secondPath = Path.Combine(tempDirectory, "second.mp4");
            File.WriteAllBytes(firstPath, new byte[] { 1 });
            File.WriteAllBytes(secondPath, new byte[] { 2 });
            long firstId = database.InsertMobileBackupRecord("TRACK-1", firstPath, 1, DateTime.Now, 3, "phone-a", "Phone", "session-1", "sha-1");
            long secondId = database.InsertMobileBackupRecord("TRACK-2", secondPath, 1, DateTime.Now, 3, "phone-b", "Phone", "session-2", "sha-2");
            database.MarkVideoDeleted(secondPath, "容量清理");

            Assert.Equal(1, database.CountVideosForDevice(null, null, null, "phone-a"));
            IReadOnlyDictionary<long, VideoRecord> statuses = database.QueryVideoStatuses(new[] { firstId, secondId, 999999L });
            Assert.False(statuses[firstId].IsDeleted);
            Assert.True(statuses[secondId].IsDeleted);
            Assert.Equal("容量清理", statuses[secondId].DeleteReason);
            Assert.DoesNotContain(999999L, statuses.Keys);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

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

    [Fact]
    public void GetAggregatedStats_CountsSharedFileSizeOnlyOnce()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            DateTime date = new(2026, 7, 20);
            string sharedPath = Path.Combine(tempDirectory, "shared.mp4");
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));

            long firstId = database.InsertVideoRecord("ORDER-1", "发货", "", "", sharedPath, date.AddHours(9));
            database.UpdateVideoRecordOnStop(firstId, date.AddHours(9).AddMinutes(1), 60, 1024, "手动");
            long secondId = database.InsertVideoRecord("ORDER-2", "发货", "", "", sharedPath, date.AddHours(10));
            database.UpdateVideoRecordOnStop(secondId, date.AddHours(10).AddMinutes(2), 120, 2048, "手动");

            DailyStat stat = Assert.Single(database.GetAggregatedStats(date, date));

            Assert.Equal(2, stat.TotalPieces);
            Assert.Equal(180, stat.TotalDurationSec);
            Assert.Equal(1024, stat.TotalBytes);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void QueryVideosPaged_ReturnsOnlyRequestedPageFromTenThousandRecords()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "videos.db");
            using (var database = new VideoDatabase(databasePath)) { }
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO VideoRecords (OrderId, FilePath, StartTime)
                    VALUES (@orderId, @filePath, @startTime);";
                var orderId = command.Parameters.Add("@orderId", SqliteType.Text);
                var filePath = command.Parameters.Add("@filePath", SqliteType.Text);
                var startTime = command.Parameters.Add("@startTime", SqliteType.Text);
                DateTime baseTime = new(2026, 1, 1);
                for (int index = 0; index < 10000; index++)
                {
                    orderId.Value = $"ORDER-{index:00000}";
                    filePath.Value = Path.Combine(tempDirectory, $"video-{index:00000}.mkv");
                    startTime.Value = baseTime.AddSeconds(index).ToString("yyyy-MM-dd HH:mm:ss");
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }

            using var reopened = new VideoDatabase(databasePath);
            PagedVideoResult page = reopened.QueryVideosPaged(null, null, null, 200, 50);
            PagedVideoResult searched = reopened.QueryVideosPaged(null, null, "ORDER-09999", 1, 50);

            Assert.Equal(10000, page.Total);
            Assert.Equal(50, page.Records.Count);
            Assert.Equal("ORDER-00049", page.Records[0].OrderId);
            Assert.Equal("ORDER-00000", page.Records[^1].OrderId);
            Assert.Equal("ORDER-09999", Assert.Single(searched.Records).OrderId);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void QueryVideosPaged_ExactSearchMatchesOnlyOrderIdentifiers()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            DateTime start = new(2026, 7, 21, 9, 0, 0);

            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO VideoRecords
                        (OrderId, TrackingNumber, SourceOrderId, BuyerMessage, FilePath, StartTime)
                    VALUES
                        ('ORDER-EXACT', 'TRACK-EXACT', 'SOURCE-EXACT', '', 'first.mp4', @start),
                        ('ORDER-OTHER', 'TRACK-OTHER', 'SOURCE-OTHER', 'TRACK-EXACT', 'second.mp4', @start);";
                command.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd HH:mm:ss"));
                command.ExecuteNonQuery();
            }

            PagedVideoResult trackingResult = database.QueryVideosPaged(
                null, null, "TRACK-EXACT", 1, 50, true, VideoSearchMode.ExactOrderIdentifiers);
            PagedVideoResult orderResult = database.QueryVideosPaged(
                null, null, "ORDER-EXACT", 1, 50, true, VideoSearchMode.ExactOrderIdentifiers);
            PagedVideoResult sourceResult = database.QueryVideosPaged(
                null, null, "SOURCE-EXACT", 1, 50, true, VideoSearchMode.ExactOrderIdentifiers);
            PagedVideoResult partialResult = database.QueryVideosPaged(
                null, null, "EXACT", 1, 50, true, VideoSearchMode.ExactOrderIdentifiers);
            PagedVideoResult partialIdentifierResult = database.QueryVideosPaged(
                null, null, "EXACT", 1, 50, true, VideoSearchMode.OrderIdentifierContains);

            Assert.Equal("ORDER-EXACT", Assert.Single(trackingResult.Records).OrderId);
            Assert.Equal("ORDER-EXACT", Assert.Single(orderResult.Records).OrderId);
            Assert.Equal("ORDER-EXACT", Assert.Single(sourceResult.Records).OrderId);
            Assert.Empty(partialResult.Records);
            Assert.Equal("ORDER-EXACT", Assert.Single(partialIdentifierResult.Records).OrderId);
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
