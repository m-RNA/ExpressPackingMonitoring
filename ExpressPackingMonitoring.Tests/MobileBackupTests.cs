using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MobileBackupTests
{
    private const string AccessKey = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void ComputerIdIsGeneratedOnceAndThenRemainsStable()
    {
        var config = new AppConfig { WebAccessKey = AccessKey };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        string generated = config.MobileBackupComputerId;
        Assert.True(Guid.TryParse(generated, out _));
        Assert.False(AppConfig.NormalizeAfterLoad(config));
        Assert.Equal(generated, config.MobileBackupComputerId);
    }

    [Fact]
    public void ExistingVideoRowsMigrateToPcSource()
    {
        string directory = CreateTempDirectory();
        string databasePath = Path.Combine(directory, "videos.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE VideoRecords (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT, OrderId TEXT NOT NULL, Mode TEXT NOT NULL DEFAULT '',
                        VideoCodec TEXT DEFAULT '', VideoEncoder TEXT DEFAULT '', FilePath TEXT NOT NULL,
                        FileSizeBytes INTEGER DEFAULT 0, StartTime TEXT NOT NULL, EndTime TEXT,
                        DurationSeconds REAL DEFAULT 0, StopReason TEXT DEFAULT '', IsDeleted INTEGER DEFAULT 0,
                        DeletedAt TEXT, DeleteReason TEXT DEFAULT '', TrackingNumber TEXT DEFAULT '',
                        SourceOrderId TEXT DEFAULT '', BuyerMessage TEXT DEFAULT '', SellerMemo TEXT DEFAULT '',
                        ProductInfo TEXT DEFAULT '', OrderInfoPushTime TEXT, OrderInfoJson TEXT DEFAULT ''
                    );
                    INSERT INTO VideoRecords (OrderId, FilePath, StartTime) VALUES ('OLD-1', 'old.mp4', '2026-07-01 10:00:00');
                    """;
                command.ExecuteNonQuery();
            }

            using var database = new VideoDatabase(databasePath);
            VideoRecord migrated = Assert.Single(database.QueryVideos(null, null));
            Assert.Equal("pc", migrated.SourceType);
            Assert.Equal("", migrated.SourceDeviceId);
            Assert.Equal("", migrated.SourceSessionId);
            Assert.Equal("", migrated.ContentSha256);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void UploadResumesValidatesChunksAndCompletesIdempotentlyWithOrderSnapshot()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] file = Encoding.UTF8.GetBytes("mobile backup video payload");
            string fileSha = Sha256(file);
            var order = new OrderInfo
            {
                TrackingNumber = "TRACK-001",
                OrderId = "ORDER-001",
                BuyerMessage = "买家留言",
                SellerMemo = "卖家备注",
                ProductInfo = "商品 A",
                IsPrintedRefund = true,
                PushTime = DateTime.Now
            };
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = CreateService(database, directory, tracking => tracking == "TRACK-001" ? order : null);
            MobileBackupCreateRequest createRequest = CreateRequest(fileSha, file.Length);

            MobileBackupCreateResult created = service.CreateOrResume(createRequest);
            Assert.Equal(0, created.Offset);
            Assert.Equal(4 * 1024 * 1024, created.ChunkSize);

            byte[] first = file[..8];
            Assert.Equal(8, service.AppendChunk(fileSha, 0, 7, file.Length, first, Sha256(first)));
            Assert.Equal(8, service.CreateOrResume(createRequest).Offset);
            Assert.Throws<MobileBackupOffsetException>(() =>
                service.AppendChunk(fileSha, 0, file.Length - 9, file.Length, file[8..], Sha256(file[8..])));
            Assert.Throws<MobileBackupValidationException>(() =>
                service.AppendChunk(fileSha, 8, file.Length - 1, file.Length, file[8..], new string('0', 64)));

            byte[] remaining = file[8..];
            Assert.Equal(file.Length, service.AppendChunk(fileSha, 8, file.Length - 1, file.Length, remaining, Sha256(remaining)));
            MobileBackupCompleteRequest completeRequest = CompleteRequest(fileSha, "session-1", "TRACK-001", "phone-1", "打包手机");
            MobileBackupCompleteResult completed = service.Complete(fileSha, completeRequest);
            MobileBackupCompleteResult repeated = service.Complete(fileSha, completeRequest);

            Assert.Equal("verified", completed.Status);
            Assert.False(completed.AlreadyCompleted);
            Assert.True(repeated.AlreadyCompleted);
            Assert.Equal(completed.RecordId, repeated.RecordId);
            VideoRecord record = database.GetVideoById(completed.RecordId);
            Assert.Equal("external", record.SourceType);
            Assert.Equal("phone-1", record.SourceDeviceId);
            Assert.Equal("打包手机", record.SourceDeviceName);
            Assert.Equal("session-1", record.SourceSessionId);
            Assert.Equal(fileSha, record.ContentSha256);
            Assert.Equal("买家留言", record.BuyerMessage);
            Assert.Equal("卖家备注", record.SellerMemo);
            Assert.Equal("商品 A", record.ProductInfo);
            Assert.True(File.Exists(record.FilePath));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void FullFileHashMismatchDeletesTemporaryUploadAndRestartsAtZero()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] expected = Encoding.UTF8.GetBytes("expected video");
            byte[] corrupted = Encoding.UTF8.GetBytes("corrupted data");
            Assert.Equal(expected.Length, corrupted.Length);
            string expectedSha = Sha256(expected);
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = CreateService(database, directory);
            MobileBackupCreateRequest request = CreateRequest(expectedSha, expected.Length);
            service.CreateOrResume(request);
            service.AppendChunk(expectedSha, 0, corrupted.Length - 1, corrupted.Length, corrupted, Sha256(corrupted));

            Assert.Throws<MobileBackupFileHashException>(() =>
                service.Complete(expectedSha, CompleteRequest(expectedSha, "session-bad", "", "phone-1", "手机")));
            Assert.Equal(0, service.CreateOrResume(request).Offset);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void SameShaReusesPhysicalFileButCreatesIndependentSearchRecords()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] file = Encoding.UTF8.GetBytes("shared physical video");
            string sha = Sha256(file);
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = CreateService(database, directory);
            service.CreateOrResume(CreateRequest(sha, file.Length));
            service.AppendChunk(sha, 0, file.Length - 1, file.Length, file, sha);
            MobileBackupCompleteResult first = service.Complete(sha, CompleteRequest(sha, "session-a", "TRACK-A", "phone-a", "手机 A"));

            Assert.True(service.CreateOrResume(CreateRequest(sha, file.Length)).FileReady);
            MobileBackupCompleteResult second = service.Complete(sha, CompleteRequest(sha, "session-b", "TRACK-B", "phone-b", "手机 B"));
            VideoRecord firstRecord = database.GetVideoById(first.RecordId);
            VideoRecord secondRecord = database.GetVideoById(second.RecordId);
            Assert.NotEqual(first.RecordId, second.RecordId);
            Assert.Equal(firstRecord.FilePath, secondRecord.FilePath);
            Assert.Equal("TRACK-A", firstRecord.TrackingNumber);
            Assert.Equal("TRACK-B", secondRecord.TrackingNumber);
            Assert.Equal(file.Length, database.GetTotalFileSizeBytes());
            Assert.Single(database.GetActiveStorageVideoFiles());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void OnePhysicalFileCanCreateMultipleLogicalSessionRecords()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] file = Encoding.UTF8.GetBytes("multi session physical video");
            string sha = Sha256(file);
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = CreateService(database, directory);
            service.CreateOrResume(CreateRequest(sha, file.Length));
            service.AppendChunk(sha, 0, file.Length - 1, file.Length, file, sha);
            var request = new MobileBackupCompleteRequest
            {
                FileSha256 = sha,
                SourceDeviceId = "phone-multi",
                SourceDeviceName = "打包手机",
                Sessions = new List<MobileBackupSessionRequest>
                {
                    new()
                    {
                        SessionId = "segment-1", TrackingNumber = "TRACK-1",
                        StartedAt = new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.FromHours(8)),
                        DurationMilliseconds = 5000
                    },
                    new()
                    {
                        SessionId = "segment-2", TrackingNumber = "TRACK-2",
                        StartedAt = new DateTimeOffset(2026, 7, 19, 10, 0, 5, TimeSpan.FromHours(8)),
                        DurationMilliseconds = 6000
                    }
                }
            };

            MobileBackupCompleteResult completed = service.Complete(sha, request);
            MobileBackupCompleteResult repeated = service.Complete(sha, request);

            Assert.Equal(2, completed.RecordIds.Count);
            Assert.True(repeated.AlreadyCompleted);
            Assert.Equal(completed.RecordIds, repeated.RecordIds);
            Assert.Equal(
                database.GetVideoById(completed.RecordIds[0]).FilePath,
                database.GetVideoById(completed.RecordIds[1]).FilePath);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void LaterOrderPushEnrichesOldExternalVideoWithoutPendingFlag()
    {
        string directory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            long id = database.InsertMobileBackupRecord(
                "TRACK-LATE", Path.Combine(directory, "late.mp4"), 10, DateTime.Now.AddDays(-30), 5,
                "phone-1", "手机", "late-session", new string('a', 64));
            database.UpdateRecentVideoOrderInfos(new[]
            {
                new OrderInfo
                {
                    TrackingNumber = "TRACK-LATE",
                    BuyerMessage = "后来补全的留言",
                    ProductInfo = "后来补全的商品",
                    IsPrintedRefund = true,
                    PushTime = DateTime.Now
                }
            });

            VideoRecord enriched = database.GetVideoById(id);
            Assert.Equal("后来补全的留言", enriched.BuyerMessage);
            Assert.Equal("后来补全的商品", enriched.ProductInfo);
            Assert.Contains("IsPrintedRefund", enriched.OrderInfoJson, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task BackupApiRequiresHeaderKeyAndReturnsVerificationConfirmation()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: false,
                accessKey: AccessKey,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: "computer-1",
                mobileBackupComputerName: "打包电脑",
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                mobileBackupRecordingDirectory: Path.Combine(directory, "recordings"));
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;

            using HttpResponseMessage missing = await client.GetAsync("/api/mobile-backup/capabilities", cancellationToken);
            using HttpResponseMessage queryOnly = await client.GetAsync($"/api/mobile-backup/capabilities?key={AccessKey}", cancellationToken);
            using var wrongRequest = new HttpRequestMessage(HttpMethod.Get, "/api/mobile-backup/capabilities");
            wrongRequest.Headers.Add("X-EPM-Access-Key", "wrong-key");
            using HttpResponseMessage wrong = await client.SendAsync(wrongRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, queryOnly.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

            client.DefaultRequestHeaders.Add("X-EPM-Access-Key", AccessKey);
            using HttpResponseMessage capabilities = await client.GetAsync("/api/mobile-backup/capabilities", cancellationToken);
            using JsonDocument capabilityJson = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal("mobile-backup-v1", capabilityJson.RootElement.GetProperty("protocol").GetString());
            Assert.Equal(1, capabilityJson.RootElement.GetProperty("version").GetInt32());
            Assert.True(capabilityJson.RootElement.GetProperty("features").GetProperty("videoLibrary").GetBoolean());
            Assert.Equal(4 * 1024 * 1024, capabilityJson.RootElement.GetProperty("maxChunkBytes").GetInt32());

            byte[] file = Encoding.UTF8.GetBytes("http upload payload");
            string sha = Sha256(file);
            using HttpResponseMessage create = await client.PostAsJsonAsync("/api/mobile-backup/uploads", CreateRequest(sha, file.Length), cancellationToken);
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            using var chunk = new HttpRequestMessage(HttpMethod.Put, $"/api/mobile-backup/uploads/{sha}/chunks")
            {
                Content = new ByteArrayContent(file)
            };
            chunk.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes 0-{file.Length - 1}/{file.Length}");
            chunk.Headers.Add("X-Chunk-SHA256", sha);
            using HttpResponseMessage chunkResponse = await client.SendAsync(chunk, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, chunkResponse.StatusCode);

            using HttpResponseMessage complete = await client.PostAsJsonAsync(
                $"/api/mobile-backup/uploads/{sha}/complete",
                CompleteRequest(sha, "http-session", "", "phone-http", "测试手机"),
                cancellationToken);
            string completeBody = await complete.Content.ReadAsStringAsync(cancellationToken);
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
            using JsonDocument completeJson = JsonDocument.Parse(completeBody);
            Assert.Equal("电脑校验完成，备份成功", completeJson.RootElement.GetProperty("message").GetString());
            Assert.Equal("verified", completeJson.RootElement.GetProperty("status").GetString());

            using HttpResponseMessage videos = await client.GetAsync("/api/videos?size=50", cancellationToken);
            using JsonDocument videoJson = JsonDocument.Parse(await videos.Content.ReadAsStringAsync(cancellationToken));
            JsonElement video = videoJson.RootElement.GetProperty("data")[0];
            Assert.Equal("phone-http", video.GetProperty("sourceDeviceId").GetString());
            Assert.Equal("http-session", video.GetProperty("sourceSessionId").GetString());
            Assert.Equal(sha, video.GetProperty("contentSha256").GetString());
            Assert.Contains("/play?compat=1", video.GetProperty("playUrl").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static MobileBackupService CreateService(VideoDatabase database, string directory, Func<string, OrderInfo?>? resolver = null) =>
        new(database, Path.Combine(directory, "state"), Path.Combine(directory, "recordings"), resolver);

    private static MobileBackupCreateRequest CreateRequest(string sha, long length) =>
        new() { FileSha256 = sha, TotalBytes = length, MimeType = "video/mp4" };

    private static MobileBackupCompleteRequest CompleteRequest(
        string sha, string sessionId, string trackingNumber, string deviceId, string deviceName) =>
        new()
        {
            FileSha256 = sha,
            SessionId = sessionId,
            TrackingNumber = trackingNumber,
            StartedAt = new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.FromHours(8)),
            DurationMilliseconds = 5000,
            SourceDeviceId = deviceId,
            SourceDeviceName = deviceName
        };

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"epm-mobile-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
