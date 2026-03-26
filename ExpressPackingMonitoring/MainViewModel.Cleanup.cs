using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private void ForceCheckDiskAndCleanup()
        {
            Task.Run(() =>
            {
                try
                {
                    if (Config.StorageLocations == null || Config.StorageLocations.Count == 0) return;

                    long totalCurrentBytes = 0;
                    long totalConfigQuotaBytes = 0;

                    foreach (var loc in Config.StorageLocations)
                    {
                        if (string.IsNullOrWhiteSpace(loc.Path)) continue;
                        string normalizedPath = Path.IsPathRooted(loc.Path) ? loc.Path : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, loc.Path);
                        if (!Directory.Exists(normalizedPath)) continue;

                        long locVideoBytes = 0;
                        foreach (var fi in EnumerateVideoFiles(normalizedPath))
                            locVideoBytes += fi.Length;
                        totalCurrentBytes += locVideoBytes;

                        long configQuota = (long)(loc.QuotaGB * 1073741824.0);
                        try
                        {
                            var driveRoot = Path.GetPathRoot(Path.GetFullPath(normalizedPath));
                            if (!string.IsNullOrEmpty(driveRoot))
                            {
                                var driveInfo = new DriveInfo(driveRoot);
                                if (driveInfo.IsReady)
                                {
                                    // 磁盘实际可供本目录使用的上限 = 当前剩余空间 + 该目录已用视频空间
                                    long realCapacity = driveInfo.AvailableFreeSpace + locVideoBytes;
                                    configQuota = Math.Min(configQuota, realCapacity);
                                }
                            }
                        }
                        catch { }
                        totalConfigQuotaBytes += configQuota;
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

                    // 2. 判断是否溢出
                    if (totalConfigQuotaBytes > 0 && totalCurrentBytes > totalConfigQuotaBytes)
                    {
                        long bytesToRelease = totalCurrentBytes - (long)(totalConfigQuotaBytes * 0.9);
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
                            _ = Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                if (_isDisposed) return;
                                ShowToast($"🗑 磁盘清理: 已从多盘回收 {count} 个旧视频");
                                RefreshTodayStats();
                            });
                        }
                    }

                    double totalUsedGB = totalCurrentBytes / 1073741824.0;
                    double totalQuotaGB = totalConfigQuotaBytes / 1073741824.0;

                    // 根据历史录制数据估算剩余可录制时长
                    string estimateText = "";
                    try
                    {
                        var (dbTotalBytes, dbTotalSec) = _db?.GetGlobalSizeAndDuration() ?? (0, 0);
                        if (dbTotalBytes > 0 && dbTotalSec > 0)
                        {
                            double bytesPerSec = dbTotalBytes / dbTotalSec;
                            long remainingBytes = totalConfigQuotaBytes - totalCurrentBytes;
                            if (remainingBytes > 0)
                            {
                                double remainingHours = remainingBytes / bytesPerSec / 3600.0;
                                estimateText = remainingHours >= 1
                                    ? $"，预估可录 {remainingHours:F0} 小时"
                                    : $"，预估可录 {remainingHours * 60:F0} 分钟";
                            }
                            else
                            {
                                estimateText = "，空间已满";
                            }
                        }
                    }
                    catch { }

                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_isDisposed) return;
                        DiskUsagePercent = totalQuotaGB > 0 ? Math.Min(100.0, (totalUsedGB / totalQuotaGB) * 100.0) : 0;
                        DiskUsageText = $"{totalUsedGB:F1} / {totalQuotaGB:F1} GB{estimateText}";
                    });
                }
                catch { }
            });
        }

        private async Task CheckDiskAndCleanup() 
        { 
            while (!_cts.Token.IsCancellationRequested) 
            { 
                ForceCheckDiskAndCleanup(); 
                int interval = IsRecording ? 10000 : 60000; 
                try { await Task.Delay(interval, _cts.Token); } catch { break; }
            } 
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
