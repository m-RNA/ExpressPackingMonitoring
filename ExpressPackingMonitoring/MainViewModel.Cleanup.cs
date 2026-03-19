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
                    long currentSizeBytes = _db?.GetTotalFileSizeBytes() ?? 0;
                    if (currentSizeBytes == 0)
                    {
                        string folderPath = Path.IsPathRooted(Config.VideoStoragePath) ? Config.VideoStoragePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
                        if (Directory.Exists(folderPath))
                        {
                            foreach (var fi in EnumerateVideoFiles(folderPath))
                                currentSizeBytes += fi.Length;
                        }
                    }

                    if (IsRecording && !string.IsNullOrEmpty(_currentVideoFilePath))
                    {
                        try
                        {
                            if (File.Exists(_currentVideoFilePath))
                                currentSizeBytes += new FileInfo(_currentVideoFilePath).Length;
                        }
                        catch { }
                    }

                    long maxSizeBytes = (long)(Config.MaxDiskSpaceGB * 1024 * 1024 * 1024);

                    try
                    {
                        string storagePath = Path.IsPathRooted(Config.VideoStoragePath) ? Config.VideoStoragePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
                        string rootDrive = Path.GetPathRoot(Path.GetFullPath(storagePath)) ?? "";
                        if (!string.IsNullOrEmpty(rootDrive))
                        {
                            var driveInfo = new DriveInfo(rootDrive);
                            if (driveInfo.IsReady)
                            {
                                long diskAvailableForVideos = currentSizeBytes + driveInfo.AvailableFreeSpace;
                                long reserveBytes = Math.Min(2L * 1024 * 1024 * 1024, (long)(driveInfo.TotalSize * 0.05));
                                reserveBytes = Math.Max(reserveBytes, 500L * 1024 * 1024);
                                long diskLimit = diskAvailableForVideos - reserveBytes;
                                if (diskLimit < maxSizeBytes)
                                    maxSizeBytes = Math.Max(diskLimit, 0);
                            }
                        }
                    }
                    catch { }

                    if (currentSizeBytes > maxSizeBytes)
                    {
                        long bytesToDelete = currentSizeBytes - (long)(maxSizeBytes * 0.9);
                        long deletedBytes = 0;
                        int deletedCount = 0;

                        var oldestVideos = _db?.GetOldestVideos(200);
                        if (oldestVideos != null && oldestVideos.Count > 0)
                        {
                            foreach (var video in oldestVideos)
                            {
                                try
                                {
                                    long len = video.FileSizeBytes;
                                    if (File.Exists(video.FilePath))
                                    {
                                        len = new FileInfo(video.FilePath).Length;
                                        File.Delete(video.FilePath);
                                    }
                                    _db?.MarkVideoDeleted(video.FilePath, "磁盘清理");
                                    deletedBytes += len;
                                    currentSizeBytes -= len;
                                    deletedCount++;
                                    if (deletedBytes >= bytesToDelete) break;
                                }
                                catch { }
                            }
                        }
                        else
                        {
                            string folderPath = Path.IsPathRooted(Config.VideoStoragePath) ? Config.VideoStoragePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
                            if (Directory.Exists(folderPath))
                            {
                                foreach (var file in EnumerateVideoFiles(folderPath).OrderBy(fi => fi.LastWriteTime))
                                {
                                    try
                                    {
                                        long len = file.Length;
                                        string fp = file.FullName;
                                        file.Delete();
                                        _db?.MarkVideoDeleted(fp, "磁盘清理");
                                        deletedBytes += len;
                                        currentSizeBytes -= len;
                                        deletedCount++;
                                        if (deletedBytes >= bytesToDelete) break;
                                    }
                                    catch { }
                                }
                            }
                        }

                        if (deletedCount > 0)
                        {
                            double deletedMB = deletedBytes / (1024.0 * 1024.0);
                            _ = Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                if (_isDisposed) return;
                                ShowToast($"🗑 磁盘清理: 删除{deletedCount}个视频，释放{deletedMB:F0}MB");
                                RefreshTodayStats();
                            });
                        }
                    }

                    double currentGB = currentSizeBytes / (1024.0 * 1024.0 * 1024.0);
                    double effectiveMaxGB = maxSizeBytes / (1024.0 * 1024.0 * 1024.0);
                    if (effectiveMaxGB < 0.1) effectiveMaxGB = 0.1;
                    double configMaxGB = Config.MaxDiskSpaceGB > 0 ? Config.MaxDiskSpaceGB : 1.0;
                    string limitNote = effectiveMaxGB < configMaxGB - 0.1 ? $" (磁盘仅剩{effectiveMaxGB:F1}GB可用)" : "";
                    _ = Application.Current.Dispatcher.InvokeAsync(() => { 
                        if (_isDisposed) return;
                        DiskUsagePercent = Math.Min(100.0, (currentGB / effectiveMaxGB) * 100.0); 
                        DiskUsageText = $"{currentGB:F1} / {effectiveMaxGB:F1} GB{limitNote}"; 
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

        private static IEnumerable<FileInfo> EnumerateVideoFiles(string folderPath)
        {
            var dir = new DirectoryInfo(folderPath);
            if (!dir.Exists) yield break;
            foreach (var file in dir.EnumerateFiles("*.mkv", SearchOption.AllDirectories))
                yield return file;
        }
    }
}
