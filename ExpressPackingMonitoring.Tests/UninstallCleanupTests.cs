using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class UninstallCleanupTests
{
    [Fact]
    public void CreatePlan_DeduplicatesRegisteredFilesAndPreservesUnregisteredFiles()
    {
        using var fixture = new CleanupFixture();
        string registered = fixture.CreateFile("recordings", "registered.mp4", "video-data");
        string unregistered = fixture.CreateFile("recordings", "unregistered.mp4", "keep");
        fixture.Register(registered, "ORDER-1");
        fixture.Register(registered, "ORDER-2");

        UninstallCleanupResult result = UninstallCleanupService.CreateRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath);

        Assert.True(result.Success);
        Assert.Equal(1, result.ProcessedFiles);
        UninstallRecordingPlan plan = fixture.ReadPlan();
        Assert.Equal(1, plan.TotalFiles);
        Assert.Equal(new FileInfo(registered).Length, plan.TotalBytes);
        Assert.Equal(Path.GetFullPath(registered), Assert.Single(plan.Files).Path);
        Assert.True(File.Exists(unregistered));
    }

    [Fact]
    public void ExecutePlan_DeletesOnlyRegisteredFilesAndWritesDatabaseDeleteLog()
    {
        using var fixture = new CleanupFixture();
        string registered = fixture.CreateFile("recordings", "registered.mp4", "video-data");
        string unregistered = fixture.CreateFile("recordings", "unregistered.mp4", "keep");
        fixture.Register(registered, "ORDER-1");
        fixture.CreatePlan();

        UninstallCleanupResult result = UninstallCleanupService.ExecuteRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath);

        Assert.True(result.Success);
        Assert.False(File.Exists(registered));
        Assert.True(File.Exists(unregistered));
        using var database = new VideoDatabase(fixture.DatabasePath);
        VideoRecord record = Assert.Single(database.QueryVideosPaged(null, null, "", 1, 20, includeDeleted: true).Records);
        Assert.True(record.IsDeleted);
        Assert.Equal("卸载时删除", record.DeleteReason);
        Assert.Equal("卸载时删除", Assert.Single(database.GetDeleteLogs()).Reason);
    }

    [Fact]
    public void ExecutePlan_PreservesFilesChangedAfterConfirmation()
    {
        using var fixture = new CleanupFixture();
        string registered = fixture.CreateFile("recordings", "changed.mp4", "original");
        fixture.Register(registered, "ORDER-1");
        fixture.CreatePlan();
        File.AppendAllText(registered, "-changed", Encoding.UTF8);

        UninstallCleanupResult result = UninstallCleanupService.ExecuteRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath);

        Assert.False(result.Success);
        Assert.True(File.Exists(registered));
        Assert.Contains("发生变化", result.Message);
        Assert.True(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public void ExecutePlan_OnPartialFailureKeepsDatabaseAndUnchangedFailedFile()
    {
        using var fixture = new CleanupFixture();
        string first = fixture.CreateFile("recordings", "first.mp4", "first");
        string second = fixture.CreateFile("recordings", "second.mp4", "second");
        fixture.Register(first, "ORDER-1");
        fixture.Register(second, "ORDER-2");
        fixture.CreatePlan();
        File.AppendAllText(second, "-changed", Encoding.UTF8);

        UninstallCleanupResult result = UninstallCleanupService.ExecuteRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath);

        Assert.False(result.Success);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(fixture.DatabasePath));
        using var database = new VideoDatabase(fixture.DatabasePath);
        Assert.Single(database.GetDeleteLogs());
    }

    [Fact]
    public void CreatePlan_WithCorruptDatabaseRefusesToDeleteAnything()
    {
        using var fixture = new CleanupFixture(createDatabase: false);
        string recording = fixture.CreateFile("recordings", "keep.mp4", "keep");
        File.WriteAllText(fixture.DatabasePath, "not-a-database", Encoding.UTF8);

        Assert.ThrowsAny<SqliteException>(() => UninstallCleanupService.CreateRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath));
        Assert.True(File.Exists(recording));
        Assert.False(File.Exists(fixture.PlanPath));
    }

    [Fact]
    public void ExecutePlan_RejectsPathInjectedAfterConfirmation()
    {
        using var fixture = new CleanupFixture();
        string registered = fixture.CreateFile("recordings", "registered.mp4", "registered");
        string unregistered = fixture.CreateFile("recordings", "unregistered.mp4", "keep");
        fixture.Register(registered, "ORDER-1");
        fixture.CreatePlan();

        UninstallRecordingPlan plan = fixture.ReadPlan();
        FileInfo unregisteredInfo = new(unregistered);
        plan.Files.Add(new UninstallRecordingPlanFile
        {
            Path = unregistered,
            Length = unregisteredInfo.Length,
            LastWriteTimeUtcTicks = unregisteredInfo.LastWriteTimeUtc.Ticks
        });
        plan.TotalFiles = plan.Files.Count;
        plan.TotalBytes = plan.Files.Sum(file => file.Length);
        File.WriteAllText(
            fixture.PlanPath,
            JsonSerializer.Serialize(plan),
            new UTF8Encoding(false));

        UninstallCleanupResult result = UninstallCleanupService.ExecuteRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath);

        Assert.False(result.Success);
        Assert.False(File.Exists(registered));
        Assert.True(File.Exists(unregistered));
        Assert.Contains("数据库未登记", result.Message);
    }

    [Fact]
    public void ExecutePlan_RejectsTamperedSummaryBeforeDeletingFiles()
    {
        using var fixture = new CleanupFixture();
        string registered = fixture.CreateFile("recordings", "registered.mp4", "keep");
        fixture.Register(registered, "ORDER-1");
        fixture.CreatePlan();

        UninstallRecordingPlan plan = fixture.ReadPlan();
        plan.TotalFiles = 0;
        File.WriteAllText(
            fixture.PlanPath,
            JsonSerializer.Serialize(plan),
            new UTF8Encoding(false));

        Assert.Throws<InvalidDataException>(() => UninstallCleanupService.ExecuteRecordingPlan(
            fixture.DatabasePath,
            fixture.PlanPath,
            fixture.LogPath));
        Assert.True(File.Exists(registered));
    }

    private sealed class CleanupFixture : IDisposable
    {
        private readonly string _root;

        public string DatabasePath { get; }
        public string PlanPath { get; }
        public string LogPath { get; }

        public CleanupFixture(bool createDatabase = true)
        {
            _root = Path.Combine(Path.GetTempPath(), "epm-uninstall-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "videos.db");
            PlanPath = Path.Combine(_root, "recording-plan.json");
            LogPath = Path.Combine(_root, "uninstall.log");
            if (createDatabase)
            {
                using var database = new VideoDatabase(DatabasePath);
            }
        }

        public string CreateFile(string directoryName, string fileName, string content)
        {
            string directory = Path.Combine(_root, directoryName);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public void Register(string path, string orderId)
        {
            using var database = new VideoDatabase(DatabasePath);
            long id = database.InsertVideoRecord(orderId, "发货", "", "", path, DateTime.Now);
            database.UpdateVideoRecordOnStop(id, DateTime.Now.AddMinutes(1), 60, new FileInfo(path).Length, "手动");
        }

        public void CreatePlan()
        {
            UninstallCleanupService.CreateRecordingPlan(DatabasePath, PlanPath, LogPath);
        }

        public UninstallRecordingPlan ReadPlan()
        {
            return JsonSerializer.Deserialize<UninstallRecordingPlan>(
                File.ReadAllText(PlanPath, Encoding.UTF8))!;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
