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

                        totalConfigQuotaBytes += (long)(loc.QuotaGB * 1073741824.0);

                        foreach (var fi in EnumerateVideoFiles(normalizedPath))
                            totalCurrentBytes += fi.Length;
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

                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_isDisposed) return;
                        DiskUsagePercent = totalQuotaGB > 0 ? Math.Min(100.0, (totalUsedGB / totalQuotaGB) * 100.0) : 0;
                        DiskUsageText = $"{totalUsedGB:F1} / {totalQuotaGB:F1} GB (多盘汇总)";
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
