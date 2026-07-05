#nullable disable
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ExpressPackingMonitoring.Services
{
    public sealed class ClipRangeRequest
    {
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public string PreviewSide { get; set; } = "";
        public int FrameCount { get; set; }
        public int FrameIndex { get; set; } = -1;
    }

    public sealed class VideoClipService
    {
        private readonly VideoDatabase _db;
        private readonly Action<string> _log;
        private readonly Func<VideoRecord, MkvConversionResult> _mkvConverter;
        private readonly Func<string, bool> _isCurrentRecordingFile;
        private readonly Action _requestCacheCleanup;
        private readonly ConcurrentDictionary<string, ClipTaskState> _tasks = new();
        private readonly ConcurrentDictionary<string, object> _previewLocks = new(StringComparer.OrdinalIgnoreCase);

        public VideoClipService(
            VideoDatabase db,
            Action<string> log,
            Func<VideoRecord, MkvConversionResult> mkvConverter = null,
            Func<string, bool> isCurrentRecordingFile = null,
            Action requestCacheCleanup = null)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _log = log ?? (_ => { });
            _mkvConverter = mkvConverter;
            _isCurrentRecordingFile = isCurrentRecordingFile ?? (_ => false);
            _requestCacheCleanup = requestCacheCleanup ?? (() => { });
            Directory.CreateDirectory(AppPaths.ClipPreviewDir);
            Directory.CreateDirectory(AppPaths.ClipsDir);
        }

        public ClipPreviewResult CreatePreview(long videoId, double startSeconds, double endSeconds, string previewSide = "")
        {
            var record = GetAvailableRecord(videoId);
            string sourcePath = ResolveClipSourcePath(record);
            double duration = ResolveDuration(record);
            ValidateRange(startSeconds, endSeconds, duration);

            double roundedStart = ClampPreviewSecond(RoundToTenth(startSeconds), duration);
            double roundedEnd = ClampPreviewSecond(RoundToTenth(endSeconds), duration);
            double endPreviewSeconds = ClampPreviewSecond(Math.Max(roundedStart, roundedEnd - 0.1), duration);
            string side = string.IsNullOrWhiteSpace(previewSide) ? "both" : previewSide.Trim();
            bool startRequested = !string.Equals(side, "end", StringComparison.OrdinalIgnoreCase);
            bool endRequested = !string.Equals(side, "start", StringComparison.OrdinalIgnoreCase);

            string startPath = startRequested ? EnsurePreviewFrame(videoId, sourcePath, roundedStart, "start") : null;
            string endPath = endRequested ? EnsurePreviewFrame(videoId, sourcePath, endPreviewSeconds, "end") : null;

            return new ClipPreviewResult
            {
                Success = true,
                VideoDuration = duration,
                StartSeconds = roundedStart,
                EndSeconds = roundedEnd,
                DurationSeconds = roundedEnd - roundedStart,
                StartPreviewUrl = string.IsNullOrWhiteSpace(startPath) ? "" : "/api/clip-previews/" + Uri.EscapeDataString(Path.GetFileName(startPath)),
                EndPreviewUrl = string.IsNullOrWhiteSpace(endPath) ? "" : "/api/clip-previews/" + Uri.EscapeDataString(Path.GetFileName(endPath))
            };
        }

        public void PrewarmPreviewFrames(long videoId, double startSeconds, double endSeconds, string previewSide = "")
        {
            var record = GetAvailableRecord(videoId);
            string sourcePath = ResolveClipSourcePath(record);
            double duration = ResolveDuration(record);
            ValidateRange(startSeconds, endSeconds, duration);

            string side = string.IsNullOrWhiteSpace(previewSide) ? "both" : previewSide.Trim();
            bool startRequested = !string.Equals(side, "end", StringComparison.OrdinalIgnoreCase);
            bool endRequested = !string.Equals(side, "start", StringComparison.OrdinalIgnoreCase);

            var seconds = new HashSet<double>();
            if (startRequested)
                AddNeighborPreviewSeconds(seconds, startSeconds, duration);
            if (endRequested)
                AddNeighborPreviewSeconds(seconds, Math.Max(startSeconds, endSeconds - 0.1), duration);

            if (string.Equals(side, "both", StringComparison.OrdinalIgnoreCase))
            {
                double[] offsets = { 0, 0.5, 1, 2, 3 };
                foreach (double offset in offsets)
                {
                    seconds.Add(ClampPreviewSecond(offset, duration));
                    seconds.Add(ClampPreviewSecond(duration - offset, duration));
                }
            }

            _ = Task.Run(() =>
            {
                bool generatedAny = false;
                foreach (double second in seconds.OrderBy(x => x))
                {
                    try
                    {
                        EnsurePreviewFrame(videoId, sourcePath, second, "prewarm", requestCleanup: false);
                        generatedAny = true;
                    }
                    catch (Exception ex) { _log($"VideoClip PrewarmPreviewFrames: {second:F1}s failed, {ex.Message}"); }
                }
                if (generatedAny)
                    RequestCacheCleanup();
            });
        }

        private static void AddNeighborPreviewSeconds(HashSet<double> seconds, double centerSeconds, double duration)
        {
            double center = RoundToTenth(centerSeconds);
            for (int i = -10; i <= 10; i++)
            {
                double offset = i / 10.0;
                seconds.Add(ClampPreviewSecond(RoundToTenth(center + offset), duration));
            }
        }

        public ClipTimelineResult CreateTimelinePreviews(long videoId, int frameCount)
        {
            var record = GetAvailableRecord(videoId);
            string sourcePath = ResolveClipSourcePath(record);
            double duration = ResolveDuration(record);
            int count = Math.Clamp(frameCount <= 0 ? 10 : frameCount, 4, 16);
            var frames = new List<ClipTimelineFrame>(count);

            for (int i = 0; i < count; i++)
            {
                double ratio = count == 1 ? 0 : i / (double)(count - 1);
                double second = ClampPreviewSecond(RoundToTenth(duration * ratio), duration);
                string path = EnsurePreviewFrame(videoId, sourcePath, second, "timeline");
                frames.Add(new ClipTimelineFrame
                {
                    Index = i,
                    Seconds = second,
                    Url = "/api/clip-previews/" + Uri.EscapeDataString(Path.GetFileName(path))
                });
            }

            return new ClipTimelineResult
            {
                Success = true,
                VideoDuration = duration,
                Frames = frames
            };
        }

        public ClipTimelineResult CreateTimelinePreviewFrame(long videoId, int frameCount, int frameIndex)
        {
            var record = GetAvailableRecord(videoId);
            string sourcePath = ResolveClipSourcePath(record);
            double duration = ResolveDuration(record);
            int count = Math.Clamp(frameCount <= 0 ? 10 : frameCount, 4, 16);
            int index = Math.Clamp(frameIndex, 0, count - 1);
            double ratio = count == 1 ? 0 : index / (double)(count - 1);
            double second = ClampPreviewSecond(RoundToTenth(duration * ratio), duration);
            string path = EnsurePreviewFrame(videoId, sourcePath, second, "timeline");

            return new ClipTimelineResult
            {
                Success = true,
                VideoDuration = duration,
                Frames =
                [
                    new ClipTimelineFrame
                    {
                        Index = index,
                        Seconds = second,
                        Url = "/api/clip-previews/" + Uri.EscapeDataString(Path.GetFileName(path))
                    }
                ]
            };
        }

        public string StartClip(long videoId, double startSeconds, double endSeconds)
        {
            var record = GetAvailableRecord(videoId);
            string sourcePath = ResolveClipSourcePath(record);
            double duration = ResolveDuration(record);
            ValidateRange(startSeconds, endSeconds, duration);

            string taskId = Guid.NewGuid().ToString("N");
            string sourceName = Path.GetFileNameWithoutExtension(record.FileName);
            if (string.IsNullOrWhiteSpace(sourceName))
                sourceName = Path.GetFileNameWithoutExtension(record.FilePath);
            string outputName = SanitizeFileName($"{sourceName}_clip_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            string outputPath = Path.Combine(AppPaths.ClipsDir, outputName);

            var task = new ClipTaskState
            {
                TaskId = taskId,
                Status = "running",
                Message = "监控端正在剪辑中，请稍候……",
                OutputPath = outputPath,
                DownloadUrl = "/api/clips/" + Uri.EscapeDataString(outputName)
            };
            _tasks[taskId] = task;

            _ = Task.Run(() => RunClipTask(task, sourcePath, startSeconds, endSeconds));
            return taskId;
        }

        public ClipTaskSnapshot GetTask(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId) || !_tasks.TryGetValue(taskId, out var task))
                return null;

            lock (task.Sync)
            {
                bool completed = string.Equals(task.Status, "completed", StringComparison.OrdinalIgnoreCase);
                return new ClipTaskSnapshot
                {
                    Success = !string.Equals(task.Status, "failed", StringComparison.OrdinalIgnoreCase),
                    Status = task.Status,
                    Message = task.Message,
                    DownloadUrl = completed ? task.DownloadUrl : ""
                };
            }
        }

        public ClipTaskSnapshot CancelTask(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId) || !_tasks.TryGetValue(taskId, out var task))
                return null;

            lock (task.Sync)
            {
                if (task.Status is "completed" or "failed" or "canceled")
                    return GetTask(taskId);

                task.CancelRequested = true;
                task.Status = "canceled";
                task.Message = "剪辑已取消";
                try
                {
                    if (task.Process != null && !task.Process.HasExited)
                        task.Process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _log($"VideoClip CancelTask: 终止 FFmpeg 失败 task={taskId}, {ex.Message}");
                }
            }

            TryDelete(task.OutputPath);
            TryDelete(GetTempClipPath(task.OutputPath));
            return GetTask(taskId);
        }

        public string ResolvePreviewPath(string fileName)
        {
            return ResolveFileInDirectory(AppPaths.ClipPreviewDir, fileName, ".jpg");
        }

        public string ResolveClipPath(string fileName)
        {
            return ResolveFileInDirectory(AppPaths.ClipsDir, fileName, ".mp4");
        }

        private void RunClipTask(ClipTaskState task, string inputPath, double startSeconds, double endSeconds)
        {
            string tmpPath = GetTempClipPath(task.OutputPath);
            double duration = endSeconds - startSeconds;
            string args = $"-y -ss {FormatSeconds(startSeconds)} -i {Quote(inputPath)} -t {FormatSeconds(duration)} -c copy -f mp4 {Quote(tmpPath)}";
            try
            {
                var result = RunFFmpeg(args, tmpPath, 0, task);
                lock (task.Sync)
                {
                    if (task.CancelRequested || task.Status == "canceled")
                    {
                        task.Status = "canceled";
                        task.Message = "剪辑已取消";
                        TryDelete(tmpPath);
                        return;
                    }

                    if (!result.Success)
                    {
                        task.Status = "failed";
                        task.Message = "FFmpeg 剪辑失败";
                        TryDelete(tmpPath);
                        return;
                    }

                    File.Move(tmpPath, task.OutputPath, overwrite: true);
                    task.Status = "completed";
                    task.Message = "剪辑完成";
                    RequestCacheCleanup();
                }
            }
            catch (Exception ex)
            {
                _log($"VideoClip RunClipTask 异常 task={task.TaskId}: {ex}");
                lock (task.Sync)
                {
                    if (task.Status != "canceled")
                    {
                        task.Status = "failed";
                        task.Message = "FFmpeg 剪辑失败";
                    }
                }
                TryDelete(tmpPath);
            }
            finally
            {
                lock (task.Sync)
                {
                    task.Process?.Dispose();
                    task.Process = null;
                }
            }
        }

        private VideoRecord GetAvailableRecord(long videoId)
        {
            var record = _db.GetVideoById(videoId);
            if (record == null || string.IsNullOrWhiteSpace(record.FilePath))
                throw new InvalidOperationException("文件不存在");

            if (File.Exists(record.FilePath))
                return record;

            if (TryPromoteExistingMp4(record, out _))
                return record;

            if (record.FilePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("视频还未完成音频合并，请稍后再剪辑。");

            if (!File.Exists(record.FilePath))
                throw new InvalidOperationException("文件不存在");

            return record;
        }

        private string ResolveClipSourcePath(VideoRecord record)
        {
            string filePath = record.FilePath;
            if (_isCurrentRecordingFile(filePath))
                throw new InvalidOperationException("视频正在录制或音频合并中，请稍后再剪辑。");

            if (_mkvConverter != null)
            {
                var result = _mkvConverter(record);
                if (result.Success && !string.IsNullOrWhiteSpace(result.FilePath) && File.Exists(result.FilePath))
                    return result.FilePath;

                if (TryPromoteExistingMp4(record, out string promotedPath))
                    return promotedPath;

                if (filePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? "视频音频合并失败，暂时无法剪辑。"
                        : result.ErrorMessage);
            }

            if (!filePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                return filePath;

            string mp4Path = Path.ChangeExtension(filePath, ".mp4");
            if (File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0)
            {
                _db.UpdateVideoFilePath(filePath, mp4Path);
                record.FilePath = mp4Path;
                record.FileName = Path.GetFileName(mp4Path);
                return mp4Path;
            }

            throw new InvalidOperationException("视频还未完成音频合并，请稍后再剪辑。");
        }

        private bool TryPromoteExistingMp4(VideoRecord record, out string mp4Path)
        {
            mp4Path = "";
            string filePath = record.FilePath;
            if (string.IsNullOrWhiteSpace(filePath) || !filePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                return false;

            string candidate = Path.ChangeExtension(filePath, ".mp4");
            if (!IsUsableFile(candidate))
                return false;

            _db.UpdateVideoFilePath(filePath, candidate);
            record.FilePath = candidate;
            record.FileName = Path.GetFileName(candidate);
            mp4Path = candidate;
            _log($"VideoClip: MKV 已转换，切换到 MP4 source={Path.GetFileName(candidate)}");
            return true;
        }

        private double ResolveDuration(VideoRecord record)
        {
            if (record.DurationSeconds > 0)
                return record.DurationSeconds;

            if (record.EndTime > record.StartTime)
                return (record.EndTime - record.StartTime).TotalSeconds;

            throw new InvalidOperationException("视频时长不可用");
        }

        private static void ValidateRange(double startSeconds, double endSeconds, double videoDuration)
        {
            if (double.IsNaN(startSeconds) || double.IsInfinity(startSeconds) || startSeconds < 0)
                throw new ArgumentException("开始时间无效");
            if (double.IsNaN(endSeconds) || double.IsInfinity(endSeconds) || endSeconds > videoDuration + 0.001)
                throw new ArgumentException("结束时间超出视频时长");
            if (startSeconds >= endSeconds)
                throw new ArgumentException("开始时间必须小于结束时间");
            if (endSeconds - startSeconds < 1)
                throw new ArgumentException("保留时长至少 1 秒");
        }

        private void RunFFmpegOrThrow(string args, string outputPath, int timeoutMs)
        {
            var result = RunFFmpeg(args, outputPath, timeoutMs, null);
            if (!result.Success)
                throw new InvalidOperationException("FFmpeg 处理失败");
        }

        private FFmpegResult RunFFmpeg(string args, string outputPath, int timeoutMs, ClipTaskState task)
        {
            string ffmpegPath = AppPaths.FindFFmpeg();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
                throw new FileNotFoundException("服务器未找到 ffmpeg.exe");

            _log($"VideoClip FFmpeg: \"{ffmpegPath}\" {args}");
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return new FFmpegResult(false, -1, "无法启动 FFmpeg");

            if (task != null)
            {
                lock (task.Sync)
                    task.Process = proc;
            }

            string stderr = "";
            var stderrTask = proc.StandardError.ReadToEndAsync();
            bool exited;
            if (timeoutMs <= 0)
            {
                proc.WaitForExit();
                exited = true;
            }
            else
            {
                exited = proc.WaitForExit(timeoutMs);
            }
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                stderr = "FFmpeg 执行超时";
                _log($"VideoClip FFmpeg: 退出码=-1, stderr={stderr}");
                return new FFmpegResult(false, -1, stderr);
            }

            try { stderr = stderrTask.GetAwaiter().GetResult(); } catch { }
            bool success = proc.ExitCode == 0 && IsUsableFile(outputPath);
            _log($"VideoClip FFmpeg: 退出码={proc.ExitCode}, stderr={TrimForLog(stderr)}");
            return new FFmpegResult(success, proc.ExitCode, stderr);
        }

        private string EnsurePreviewFrame(long videoId, string filePath, double seconds, string label, bool requestCleanup = true)
        {
            string key = BuildPreviewKey(videoId, filePath, seconds);
            string path = Path.Combine(AppPaths.ClipPreviewDir, key + ".jpg");
            if (IsUsableFile(path))
                return path;

            object gate = _previewLocks.GetOrAdd(path, _ => new object());
            lock (gate)
            {
                if (!IsUsableFile(path))
                {
                    RunFFmpegOrThrow($"-y -ss {FormatSeconds(seconds)} -i {Quote(filePath)} -frames:v 1 {Quote(path)}", path, 30_000);
                    if (requestCleanup)
                        RequestCacheCleanup();
                }
            }
            return path;
        }

        private void RequestCacheCleanup()
        {
            try { _requestCacheCleanup(); } catch { }
        }

        private static string BuildPreviewKey(long videoId, string filePath, double seconds)
        {
            string raw = $"{videoId}|{filePath}|{File.GetLastWriteTimeUtc(filePath).Ticks}|{seconds:F1}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).Substring(0, 24).ToLowerInvariant();
        }

        private static double ClampPreviewSecond(double value, double duration)
        {
            return Math.Max(0, Math.Min(value, Math.Max(0, duration - 0.05)));
        }

        private static string ResolveFileInDirectory(string directory, string fileName, string extension)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            string safeName = Path.GetFileName(Uri.UnescapeDataString(fileName));
            if (!safeName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return null;

            string fullPath = Path.GetFullPath(Path.Combine(directory, safeName));
            string fullDir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
                return null;

            return File.Exists(fullPath) ? fullPath : null;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string FormatSeconds(double seconds)
        {
            return Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static double RoundToTenth(double value)
        {
            return Math.Round(value, 1, MidpointRounding.AwayFromZero);
        }

        private static bool IsUsableFile(string path)
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }

        private static string SanitizeFileName(string fileName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');
            return fileName;
        }

        private static string GetTempClipPath(string outputPath)
        {
            return Path.Combine(
                Path.GetDirectoryName(outputPath) ?? AppPaths.ClipsDir,
                Path.GetFileNameWithoutExtension(outputPath) + ".tmp" + Path.GetExtension(outputPath));
        }

        private static string TrimForLog(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= 4000 ? value : value.Substring(0, 4000) + "...";
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private sealed class ClipTaskState
        {
            public string TaskId { get; set; }
            public string Status { get; set; }
            public string Message { get; set; }
            public string OutputPath { get; set; }
            public string DownloadUrl { get; set; }
            public bool CancelRequested { get; set; }
            public Process Process { get; set; }
            public object Sync { get; } = new();
        }

        private readonly record struct FFmpegResult(bool Success, int ExitCode, string Stderr);
    }

    public sealed class ClipPreviewResult
    {
        public bool Success { get; set; }
        public double VideoDuration { get; set; }
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public string StartPreviewUrl { get; set; }
        public string EndPreviewUrl { get; set; }
    }

    public sealed class ClipTaskSnapshot
    {
        public bool Success { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public string DownloadUrl { get; set; } = "";
    }

    public sealed class ClipTimelineResult
    {
        public bool Success { get; set; }
        public double VideoDuration { get; set; }
        public List<ClipTimelineFrame> Frames { get; set; } = new();
    }

    public sealed class ClipTimelineFrame
    {
        public int Index { get; set; }
        public double Seconds { get; set; }
        public string Url { get; set; } = "";
    }
}
