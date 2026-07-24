using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal static class StorageLocationResolver
{
    public static string Resolve(AppConfig config, bool allowDefaultFallback)
    {
        ArgumentNullException.ThrowIfNull(config);

        string defaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Videos");
        List<StorageLocation> locations = config.StorageLocations?
            .Where(location => !string.IsNullOrWhiteSpace(location.Path))
            .OrderBy(location => location.Priority)
            .ToList() ?? [];

        if (locations.Count == 0)
        {
            if (!allowDefaultFallback)
                throw new IOException("未配置录像存储位置，请先选择“使用电脑摄像头录像”并完成存储设置");

            RuntimeLog.Warn("Storage", $"No storage locations configured, fallback default path={defaultPath}");
            EnsureDirectoryWritable(defaultPath);
            return defaultPath;
        }

        var failures = new List<string>();
        foreach (StorageLocation location in locations)
        {
            StorageLocationEvaluation result = Evaluate(location);
            if (result.CanUse)
            {
                RuntimeLog.Info(
                    "Storage",
                    $"Selected storage path={result.Path}, priority={location.Priority}, free={FormatBytes(result.AvailableBytes)}, reserve={FormatBytes(result.ReserveBytes)}");
                return result.Path;
            }

            failures.Add($"{result.Path}：{result.Reason}");
            RuntimeLog.Warn("Storage", $"Skip storage path={result.Path}, priority={location.Priority}, reason={result.Reason}");
        }

        if (!allowDefaultFallback)
            throw new IOException($"没有可用的录像存储位置。{string.Join("；", failures)}");

        RuntimeLog.Warn("Storage", $"No configured storage path is safe for recording, fallback default path={defaultPath}");
        EnsureDirectoryWritable(defaultPath);
        return defaultPath;
    }

    private static StorageLocationEvaluation Evaluate(StorageLocation location)
    {
        string path = NormalizePath(location.Path);
        try
        {
            Directory.CreateDirectory(path);
            EnsureDirectoryWritable(path);

            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
                return StorageLocationEvaluation.Skip(path, "无法确定磁盘");

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return StorageLocationEvaluation.Skip(path, "磁盘未就绪");

            long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(location, drive);
            long availableBytes = drive.AvailableFreeSpace;
            if (availableBytes <= reserveBytes)
            {
                return StorageLocationEvaluation.Skip(
                    path,
                    $"剩余空间低于预留值（可用 {FormatBytes(availableBytes)}，需预留 {FormatBytes(reserveBytes)}）");
            }

            return StorageLocationEvaluation.Use(path, availableBytes, reserveBytes);
        }
        catch (Exception ex)
        {
            return StorageLocationEvaluation.Skip(path, ex.Message);
        }
    }

    private static string NormalizePath(string path)
    {
        string combined = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        return Path.GetFullPath(combined);
    }

    private static void EnsureDirectoryWritable(string path)
    {
        Directory.CreateDirectory(path);
        string probe = Path.Combine(path, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
        }
        finally
        {
            try { File.Delete(probe); } catch { }
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 GB";
        return $"{bytes / (double)StorageSpacePolicy.BytesPerGiB:F1} GB";
    }

    private readonly record struct StorageLocationEvaluation(
        bool CanUse,
        string Path,
        string Reason,
        long AvailableBytes,
        long ReserveBytes)
    {
        public static StorageLocationEvaluation Use(string path, long availableBytes, long reserveBytes) =>
            new(true, path, "", availableBytes, reserveBytes);

        public static StorageLocationEvaluation Skip(string path, string reason) =>
            new(false, path, reason, 0, 0);
    }
}
