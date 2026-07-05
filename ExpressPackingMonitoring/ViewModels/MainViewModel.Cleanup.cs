using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private void ForceCheckDiskAndCleanup()
        {
            _ = Task.Run(() => RunDiskCleanupCore(forceFullScan: true));
        }

        private async Task CheckDiskAndCleanup()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                RunDiskCleanupCore(forceFullScan: false);
                int interval = IsRecording ? 10000 : 60000;
                try { await Task.Delay(interval, _cts.Token); } catch { break; }
            }
        }

        private int _diskCleanupRunning;
        private DateTime _lastFullDiskCleanup = DateTime.MinValue;
        private long _lastKnownDiskTotalBytes;
        private long _lastKnownDiskCapacityBytes;

        private void RunDiskCleanupCore(bool forceFullScan)
        {
            if (Interlocked.Exchange(ref _diskCleanupRunning, 1) == 1) return;
            try
            {
                if (Config.StorageLocations == null || Config.StorageLocations.Count == 0) return;

                bool fullScan = forceFullScan
                    || _lastFullDiskCleanup == DateTime.MinValue
                    || (DateTime.Now - _lastFullDiskCleanup).TotalSeconds >= (IsRecording ? 60 : 180);

                long totalCurrentBytes = fullScan ? 0 : _lastKnownDiskTotalBytes;
                long totalCapacityBytes = fullScan ? 0 : _lastKnownDiskCapacityBytes;

                if (fullScan)
                {
                    foreach (var loc in Config.StorageLocations)
                    {
                        if (string.IsNullOrWhiteSpace(loc.Path)) continue;
                        string normalizedPath = Path.IsPathRooted(loc.Path) ? loc.Path : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, loc.Path);
                        if (!Directory.Exists(normalizedPath)) continue;

                        long locVideoBytes = 0;
                        foreach (var fi in EnumerateVideoFiles(normalizedPath))
                            locVideoBytes += fi.Length;
                        totalCurrentBytes += locVideoBytes;

                        long storageCapacity = 0;
                        try
                        {
                            var driveRoot = Path.GetPathRoot(Path.GetFullPath(normalizedPath));
                            if (!string.IsNullOrEmpty(driveRoot))
                            {
                                var driveInfo = new DriveInfo(driveRoot);
                                if (driveInfo.IsReady)
                                {
                                    long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(loc, driveInfo);
                                    storageCapacity = Math.Max(0, driveInfo.AvailableFreeSpace - reserveBytes) + locVideoBytes;
                                }
                            }
                        }
                        catch { }
                        totalCapacityBytes += storageCapacity;
                    }

                    _lastFullDiskCleanup = DateTime.Now;
                    _lastKnownDiskTotalBytes = totalCurrentBytes;
                    _lastKnownDiskCapacityBytes = totalCapacityBytes;
                }

                if (IsRecording && !string.IsNullOrEmpty(_currentVideoFilePath))
                {
                    try
                    {
                        if (File.Exists(_currentVideoFilePath))
                            totalCurrentBytes += new FileInfo(_currentVideoFilePath).Length;
                    }
                    catch { }
                }

                if (fullScan && totalCapacityBytes > 0 && totalCurrentBytes > totalCapacityBytes)
                    CleanupOldVideos(totalCurrentBytes, totalCapacityBytes);

                UpdateDiskUsageText(totalCurrentBytes, totalCapacityBytes);
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _diskCleanupRunning, 0);
            }
        }

        private void CleanupOldVideos(long totalCurrentBytes, long totalCapacityBytes)
        {
            long bytesToRelease = totalCurrentBytes - (long)(totalCapacityBytes * 0.9);
            long releasedBytes = 0;
            int count = 0;

            var oldestRecords = _db?.GetOldestVideos(500);
            if (oldestRecords != null)
            {
                foreach (var video in oldestRecords)
                {
                    try
                    {
                        if (File.Exists(video.FilePath))
                        {
                            long size = new FileInfo(video.FilePath).Length;
                            File.Delete(video.FilePath);
                            releasedBytes += size;
                            count++;
                        }
                        _db?.MarkVideoDeleted(video.FilePath, "全局配额清理");
                        if (releasedBytes >= bytesToRelease) break;
                    }
                    catch { }
                }
            }

            if (count > 0)
            {
                _lastKnownDiskTotalBytes = Math.Max(0, _lastKnownDiskTotalBytes - releasedBytes);
                _ = Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    ShowToast($"清理：已从多盘回收 {count} 个旧视频");
                    RefreshTodayStats();
                });
            }
        }

        private void UpdateDiskUsageText(long totalCurrentBytes, long totalCapacityBytes)
        {
            double totalUsedGB = totalCurrentBytes / 1073741824.0;
            double totalCapacityGB = totalCapacityBytes / 1073741824.0;
            string estimateText = "";
            try
            {
                var (dbTotalBytes, dbTotalSec) = _db?.GetGlobalSizeAndDuration() ?? (0, 0);
                if (dbTotalBytes > 0 && dbTotalSec > 0)
                {
                    double bytesPerSec = dbTotalBytes / dbTotalSec;
                    if (totalCapacityBytes > 0)
                    {
                        double retentionHours = totalCapacityBytes / bytesPerSec / 3600.0;
                        estimateText = retentionHours >= 1
                            ? $"，预计循环可录 {retentionHours:F0} 小时"
                            : $"，预计循环可录 {retentionHours * 60:F0} 分钟";
                    }
                }
            }
            catch { }

            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_isDisposed) return;
                DiskUsagePercent = totalCapacityGB > 0 ? Math.Min(100.0, (totalUsedGB / totalCapacityGB) * 100.0) : 0;
                DiskUsageText = $"{totalUsedGB:F1} / {totalCapacityGB:F1} GB{estimateText}";
            });
        }

        private static readonly string[] _videoExtensions = [".mkv", ".mp4"];

        private static IEnumerable<FileInfo> EnumerateVideoFiles(string folderPath)
        {
            var dir = new DirectoryInfo(folderPath);
            if (!dir.Exists) yield break;
            foreach (var file in dir.EnumerateFiles("*.*", SearchOption.AllDirectories))
            {
                if (_videoExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }
}
