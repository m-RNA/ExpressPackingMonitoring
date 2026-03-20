using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private async Task InternalStopRecordingAsync()
        {
            if (!IsRecording || _isDisposed) return;

            IsBusy = true;
            BusyText = "正在停止...";
            IsRecording = false; // 1. 立即改变 UI 状态
            _isScanning = false;
            _delayBeforeZooming = false;
            _isZooming = false;
            _autoStopWarned = false;
            _maxDurationWarned = false;

            CancellationTokenSource oldCts;
            BlockingCollection<Mat> oldQueue;
            Task oldWriteTask;

            lock (_videoLock)
            {
                oldCts = _writeCts;
                oldQueue = _videoWriteQueue;
                oldWriteTask = _writeTask;
                _writeCts = null;
                _videoWriteQueue = null;
                _writeTask = null;
            }

            // 2. 停止生产
            try { oldQueue?.CompleteAdding(); } catch { }
            oldCts?.Cancel(); // 3. 通知 FFmpeg 线程停止

            // 4. 等待录制线程真正退出（FFmpeg 进程关闭）
            try
            {
                if (oldWriteTask != null)
                {
                    // 给 FFmpeg 3秒时间正常写入尾部信息并关闭
                    var completedTask = await Task.WhenAny(oldWriteTask, Task.Delay(3000));
                    if (completedTask != oldWriteTask)
                    {
                        Debug.WriteLine("[MainVM] FFmpeg 正常停止超时，执行强杀...");
                        try 
                        {
                            if (_currentFfmpegProcess != null && !_currentFfmpegProcess.HasExited)
                            {
                                _currentFfmpegProcess.Kill();
                                Debug.WriteLine("[MainVM] 僵尸 FFmpeg 已强杀！");
                            }
                        } 
                        catch { }
                        
                        // 再等1秒确认彻底死亡
                        await Task.WhenAny(oldWriteTask, Task.Delay(1000));
                    }
                }
            }
            catch { }

            // 5. 彻底清空内存中的残余 Mat 对象 (防止泄漏的核心)
            if (oldQueue != null)
            {
                while (oldQueue.TryTake(out var mat)) mat?.Dispose();
                oldQueue.Dispose();
            }
            oldCts?.Dispose();

            // 6. 保存元数据到数据库
            var filePath = _currentVideoFilePath;
            var videoCodec = _currentVideoCodec;
            var videoEncoder = _currentVideoEncoder;
            var recordStart = _recordStartTime;
            var orderId = _recordingOrderId;
            var mode = _recordingMode;
            var stopReason = _stopReason;
            var scanRecord = _currentScanRecord;
            var recordId = _currentRecordId; 

            _recordStartTime = DateTime.MinValue;
            _currentScanRecord = null;
            _currentVideoFilePath = null;
            _currentVideoCodec = null;
            _currentVideoEncoder = null;
            _currentRecordId = 0;
            _currentFfmpegProcess = null;

            _lastFinalizeTask = Task.Run(() => 
            {
                if (_isDisposed) return; // 销毁中不再执行数据库后的 UI 更新
                try
                {
                    long fileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;

                    // 如果文件小于 50KB (比如启动报错或没数据)，作为异常数据丢弃
                    if (fileSize < 1024 * 50)
                    {
                        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                        _ = Application.Current.Dispatcher.InvokeAsync(() => {
                            if (!_isDisposed) {
                                _allLogs.Remove(scanRecord);
                                FilteredLogs.Remove(scanRecord);
                            }
                        });
                    }
                    else
                    {
                        double dur = (DateTime.Now - recordStart).TotalSeconds;
                        int durSec = (int)dur;
                        if (durSec < 1) durSec = 1;
                        string durStr = durSec < 60 ? $"{durSec}s" : $"{(int)durSec / 60}m {durSec % 60}s";

                        _db?.UpdateVideoRecordOnStop(recordId, DateTime.Now, durSec, fileSize, stopReason, videoCodec, videoEncoder);

                        _ = Application.Current.Dispatcher.InvokeAsync(() => {
                            if (!_isDisposed && scanRecord != null)
                            {
                                scanRecord.Duration = "已保存";
                                scanRecord.IsActive = false;
                                RefreshTodayStats();
                            }
                        });
                    }
                }
                catch { }
            });
            
            // 如果是在关闭窗口时发生，不要解除 Busy，防止被再次点击
            if (Application.Current?.MainWindow != null && !_isDisposed)
            {
                IsBusy = false;
            }

            if (_pendingCameraRestart && !_isDisposed)
            {
                _pendingCameraRestart = false;
                RestartCamera();
                ShowToast("摄像头配置已生效");
            }
        }

        private string ResolveBestStoragePath()
        {
            if (Config.StorageLocations == null || Config.StorageLocations.Count == 0)
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Videos");
            }

            var orderedLocations = Config.StorageLocations
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .OrderBy(x => x.Priority)
                .ToList();

            if (orderedLocations.Count == 0)
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Videos");
            }

            foreach (var loc in orderedLocations)
            {
                try
                {
                    string normalizedPath = Path.IsPathRooted(loc.Path)
                        ? loc.Path
                        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, loc.Path);

                    if (!Directory.Exists(normalizedPath))
                        Directory.CreateDirectory(normalizedPath);

                    long usedBytesInPath = 0;
                    var dirInfo = new DirectoryInfo(normalizedPath);
                    foreach (var file in dirInfo.EnumerateFiles("*.mkv", SearchOption.AllDirectories))
                    {
                        usedBytesInPath += file.Length;
                    }

                    double usedGB = usedBytesInPath / 1073741824.0;

                    string root = Path.GetPathRoot(Path.GetFullPath(normalizedPath));
                    if (string.IsNullOrEmpty(root)) continue;

                    var drive = new DriveInfo(root);
                    if (!drive.IsReady) continue;

                    long safeBuffer = (long)Math.Max(2147483648, drive.TotalSize * 0.05);

                    if (usedGB < loc.QuotaGB && drive.AvailableFreeSpace > safeBuffer)
                    {
                        return normalizedPath;
                    }
                }
                catch
                {
                    continue;
                }
            }

            var fallback = orderedLocations[0].Path;
            if (Path.IsPathRooted(fallback)) return fallback;
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fallback);
        }

        private async Task InternalStartRecordingAsync()
        {
            IsBusy = true;
            BusyText = "正在启动...";

            try
            {
                // 1. 彻底清理环境：如果系统残留了任何挂死的 ffmpeg，全部清掉
                await Task.Run(() => {
                    try {
                        foreach (var p in Process.GetProcessesByName("ffmpeg")) 
                        {
                            if ((DateTime.Now - p.StartTime).TotalMinutes > 2) p.Kill();
                        }
                    } catch { }
                });

                // 2. 初始化路径和文件名
                string baseFolder = ResolveBestStoragePath();
                string dateFolder = Path.Combine(baseFolder, DateTime.Now.ToString("yyyy-MM-dd"));
                if (!Directory.Exists(dateFolder)) Directory.CreateDirectory(dateFolder);

                string fileName = $"{CurrentOrderId}_{DateTime.Now:yyyyMMdd_HHmmss}_{CurrentMode}.mkv";
                string filePath = Path.Combine(dateFolder, fileName);
                _currentVideoFilePath = filePath;
                _stopReason = "手动";
                _recordingOrderId = CurrentOrderId;
                _recordingMode = CurrentMode;
                _currentVideoCodec = Config.VideoCodec?.Trim().ToLowerInvariant() ?? "h264";
                _currentVideoEncoder = ResolveEncoder();

                string ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    ShowToast("⚠ 未找到 FFmpeg，无法录制");
                    return;
                }

                // 3. 开启新的生产者-消费者通道
                lock (_videoLock)
                {
                    _videoWriteQueue = new BlockingCollection<Mat>(60);
                    _writeCts = new CancellationTokenSource();
                }

                // 4. 启动录制任务
                _writeTask = Task.Run(() => BackgroundFFmpegRecordingLoop(filePath, ffmpegPath, _writeCts.Token));

                // 5. 等待 250ms 给 FFmpeg 初始化。如果闪退，Task 会立刻完成
                await Task.Delay(250); 
                if (_writeTask.IsCompleted) 
                {
                    Debug.WriteLine("[MainVM] 启动检测：_writeTask 瞬间结束，说明 FFmpeg 启动失败");
                    return; // 内部错误处理已经在 Loop 里面通过 Dispatcher 报错了，这里只需返回
                }

                IsRecording = true;
                _recordStartTime = DateTime.Now;
                _lastMotionTime = DateTime.Now;
                _autoStopWarned = false;
                _maxDurationWarned = false;
                _previousCheckFrame?.Dispose();
                _previousCheckFrame = new Mat();

                // 6. 在数据库中创建记录占位符
                _currentRecordId = _db?.InsertVideoRecord(_recordingOrderId, _recordingMode, _currentVideoCodec, _currentVideoEncoder, filePath, _recordStartTime) ?? 0;

                ShowToast("▶ 开始录像");
                Speak("开始录制");
                _currentScanRecord = new ScanRecord(_recordingOrderId, "0s", DateTime.Now.ToString("HH:mm:ss"), _recordingMode, true);
                AddRecord(_currentScanRecord);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void BackgroundFFmpegRecordingLoop(string filePath, string ffmpegPath, CancellationToken token)
        {
            int w = Config.FrameWidth;
            int h = Config.FrameHeight;
            int fps = _actualCameraFps > 0 ? _actualCameraFps : Config.Fps;
            string encoder = ResolveEncoder();
            bool hasAudio = Config.EnableAudioRecording && !string.IsNullOrEmpty(Config.AudioDeviceName);
            string requestedEncoder = encoder;

            var (ok, err) = RunFFmpegPipeline(filePath, ffmpegPath, token, w, h, fps, encoder, hasAudio);
            
            if (ok)
            {
                _currentVideoEncoder = encoder;
                _currentVideoCodec = EncodingHelper.GetCodecFromEncoder(encoder);
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    ShowToast($"编码器 {EncodingHelper.GetEncoderLabel(encoder)}"));
                return;
            }

            // 如果失败，强制重置 UI 状态
            if (!token.IsCancellationRequested)
            {
                try { if (File.Exists(filePath) && new FileInfo(filePath).Length == 0) File.Delete(filePath); } catch { }
                string errMsg = string.IsNullOrEmpty(err) ? "" : $"\n{err}";

                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    IsRecording = false;
                    IsBusy = false; // 释放 Busy 状态
                    CurrentOrderId = "";
                    ScanInputText = "";

                    lock (_videoLock)
                    {
                        if (_videoWriteQueue != null)
                        {
                            _videoWriteQueue.CompleteAdding();
                            while (_videoWriteQueue.TryTake(out var m)) m?.Dispose();
                            _videoWriteQueue.Dispose();
                            _videoWriteQueue = null;
                        }
                    }

                    ShowToast($"⚠ 录制启动失败");
                    Speak("录制失败");
                    MessageBox.Show(
                        $"当前设置的编码器无法完成录制，视频未保存。\n\n请求编码器: {EncodingHelper.GetEncoderLabel(requestedEncoder)}\n错误详情: {err}\n\n建议在设置中更换编码器或尝试 CPU 软编码。",
                        "录制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            }
        }

        private string GetCpuEncoder()
        {
            return (Config.VideoCodec?.ToLowerInvariant() ?? "h264") switch
            {
                "h265" => "libx265",
                "av1" => "libsvtav1",
                _ => "libx264"
            };
        }

        private (bool ok, string error) RunFFmpegPipeline(string filePath, string ffmpegPath, CancellationToken token,
            int w, int h, int fps, string encoder, bool withAudio)
        {
            Process ffmpeg = null;
            Stream stdin = null;
            bool anyFrameWritten = false;
            string stderrText = "";
            bool stdinClosed = false;

            try
            {
                string args = BuildFFmpegArgs(w, h, fps, filePath, encoder, withAudio);
                Debug.WriteLine($"[FFmpeg] encoder={encoder} audio={withAudio} args={args}");

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true
                };

                ffmpeg = Process.Start(psi);
                if (ffmpeg == null) return (false, "FFmpeg 进程启动失败");

                // 将进程保存为全局变量，允许从外部强制 Kill
                _currentFfmpegProcess = ffmpeg;

                var stderrTask = Task.Run(() => { try { return ffmpeg.StandardError.ReadToEnd(); } catch { return ""; } });

                for (int wait = 0; wait < 150 && !ffmpeg.HasExited; wait += 30)
                    Thread.Sleep(30);
                if (ffmpeg.HasExited)
                {
                    stderrText = stderrTask.GetAwaiter().GetResult();
                    Debug.WriteLine($"[FFmpeg] early exit ({encoder}): {stderrText}");
                    string shortErr = ExtractFFmpegError(stderrText);
                    return (false, shortErr);
                }

                stdin = ffmpeg.StandardInput.BaseStream;

                int expectedBytes = w * h * 3; 
                byte[] buffer = new byte[expectedBytes];

                foreach (var frame in _videoWriteQueue.GetConsumingEnumerable())
                {
                    if (token.IsCancellationRequested) { frame?.Dispose(); break; }
                    if (frame == null || frame.IsDisposed) continue;

                    bool pipeError = false;
                    try
                    {
                        Mat toWrite = frame;
                        bool needResize = frame.Width != w || frame.Height != h;

                        if (needResize)
                        {
                            toWrite = new Mat();
                            Cv2.Resize(frame, toWrite, new OpenCvSharp.Size(w, h));
                        }

                        try
                        {
                            if (toWrite.IsContinuous() && toWrite.Type() == MatType.CV_8UC3)
                            {
                                Marshal.Copy(toWrite.Data, buffer, 0, expectedBytes);
                                stdin.Write(buffer, 0, expectedBytes);
                                anyFrameWritten = true;
                            }
                        }
                        finally
                        {
                            if (needResize) toWrite.Dispose();
                        }
                    }
                    catch (IOException) { pipeError = true; }
                    finally
                    {
                        frame.Dispose();
                    }

                    if (pipeError) break;
                }

                try { stdin?.Close(); stdinClosed = true; } catch { }

                if (ffmpeg != null && !ffmpeg.HasExited)
                {
                    if (!ffmpeg.WaitForExit(15000))
                    {
                        try { ffmpeg.Kill(); } catch { }
                    }
                }

                stderrText = stderrTask.GetAwaiter().GetResult();
                bool fileOk = false;
                try { fileOk = File.Exists(filePath) && new FileInfo(filePath).Length > 0; } catch { }
                bool processOk = ffmpeg != null && ffmpeg.HasExited && ffmpeg.ExitCode == 0;

                if (token.IsCancellationRequested)
                    return (fileOk, fileOk ? "" : ExtractFFmpegError(stderrText));

                if (anyFrameWritten && processOk && fileOk)
                    return (true, "");

                string finalErr = ExtractFFmpegError(stderrText);
                if (string.IsNullOrWhiteSpace(finalErr))
                    finalErr = !fileOk ? "FFmpeg 未生成有效视频文件" : $"FFmpeg 退出码: {ffmpeg?.ExitCode}";
                return (false, finalErr);
            }
            catch (OperationCanceledException) { return (anyFrameWritten, ""); }
            catch (IOException) { return (anyFrameWritten, ""); }
            catch (Exception ex) { return (false, ex.Message); }
            finally
            {
                if (!stdinClosed) { try { stdin?.Close(); } catch { } }

                try
                {
                    if (ffmpeg != null && !ffmpeg.HasExited)
                    {
                        if (!ffmpeg.WaitForExit(8000))
                        {
                            try { ffmpeg.Kill(); } catch { }
                        }
                    }
                }
                catch { }
                finally
                {
                    try { ffmpeg?.Dispose(); } catch { }
                }
            }
        }

        private static string ExtractFFmpegError(string stderr)
        {
            if (string.IsNullOrEmpty(stderr)) return "";
            var lines = stderr.Split('\n');
            for (int i = lines.Length - 1; i >= Math.Max(0, lines.Length - 10); i--)
            {
                string line = lines[i].Trim();
                if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Could not", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("No such", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    return line.Length > 80 ? line[..80] : line;
            }
            return "";
        }

        private string BuildFFmpegArgs(int w, int h, int fps, string filePath, string encoder, bool withAudio)
        {
            string args = $"-y -fflags +genpts -use_wallclock_as_timestamps 1 -f rawvideo -video_size {w}x{h} -pixel_format bgr24 -framerate {fps} -i pipe:0";
            int cqp = GetVideoCqp();
            int gop = Math.Max(1, fps * 2);

            bool hasAudio = withAudio && Config.EnableAudioRecording && !string.IsNullOrEmpty(Config.AudioDeviceName);
            if (hasAudio)
            {
                args += $" -thread_queue_size 256 -use_wallclock_as_timestamps 1 -f dshow -audio_buffer_size 50 -i audio=\"{Config.AudioDeviceName}\"";
            }

            if (encoder == "h264_nvenc") args += $" -c:v h264_nvenc -pix_fmt yuv420p -preset p4 -rc vbr -cq {cqp} -b:v 0 -g {gop}";
            else if (encoder == "h264_amf") args += $" -c:v h264_amf -pix_fmt yuv420p -quality balanced -rc cqp -qp_i {cqp} -qp_p {cqp} -g {gop}";
            else if (encoder == "h264_qsv") args += $" -c:v h264_qsv -pix_fmt nv12 -preset medium -global_quality {cqp} -g {gop}";
            else if (encoder == "libx264") args += $" -c:v libx264 -pix_fmt yuv420p -preset fast -crf {cqp} -g {gop}";
            else if (encoder == "hevc_nvenc") args += $" -c:v hevc_nvenc -pix_fmt yuv420p -preset p4 -rc vbr -cq {cqp} -b:v 0 -g {gop}";
            else if (encoder == "hevc_amf") args += $" -c:v hevc_amf -pix_fmt yuv420p -quality balanced -rc cqp -qp_i {cqp} -qp_p {cqp} -g {gop}";
            else if (encoder == "hevc_qsv") args += $" -c:v hevc_qsv -pix_fmt nv12 -preset medium -global_quality {cqp} -g {gop}";
            else if (encoder == "libx265") args += $" -c:v libx265 -pix_fmt yuv420p -preset fast -crf {cqp} -g {gop}";
            else if (encoder == "av1_nvenc") args += $" -c:v av1_nvenc -pix_fmt yuv420p -preset p4 -rc vbr -cq {cqp} -b:v 0 -g {gop}";
            else if (encoder == "av1_amf") args += $" -c:v av1_amf -pix_fmt yuv420p -quality balanced -rc cqp -qp_i {cqp} -qp_p {cqp} -g {gop}";
            else if (encoder == "av1_qsv") args += $" -c:v av1_qsv -pix_fmt nv12 -preset medium -global_quality {cqp} -g {gop}";
            else if (encoder == "libsvtav1") args += $" -c:v libsvtav1 -pix_fmt yuv420p -preset {GetCpuAv1Preset(w, h, fps)} -crf {cqp} -svtav1-params tune=0 -g {gop}";
            else args += $" -c:v {encoder} -pix_fmt yuv420p -g {gop}";

            args += $" -r {fps} -fps_mode cfr";

            if (hasAudio)
            {
                string audioFilter = BuildAudioSyncFilter();
                args += $" -af {audioFilter} -c:a aac -b:a 64k -shortest";
            }

            args += " -muxdelay 0 -muxpreload 0";
            args += $" \"{filePath}\"";
            return args;
        }

        private static int GetCpuAv1Preset(int w, int h, int fps)
        {
            long pixels = (long)w * h;
            if (pixels >= 1920L * 1080 && fps >= 30) return 10;
            if (pixels >= 1920L * 1080 || fps >= 25) return 9;
            return 8;
        }

        private string BuildAudioSyncFilter()
        {
            int offsetMs = Math.Clamp(Config.AudioSyncOffsetMs, -5000, 5000);
            if (offsetMs > 0) return $"adelay={offsetMs}:all=1,aresample=async=1:first_pts=0";
            if (offsetMs < 0)
            {
                double trimStartSec = Math.Abs(offsetMs) / 1000.0;
                return $"atrim=start={trimStartSec:0.###},asetpts=PTS-STARTPTS,aresample=async=1:first_pts=0";
            }
            return "aresample=async=1:first_pts=0";
        }

        private int GetVideoCqp() => Config.VideoCqp > 0 ? Config.VideoCqp : 25;

        private static string FindFFmpeg()
        {
            string appDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            return File.Exists(appDir) ? appDir : null;
        }

        private string ResolveEncoder()
        {
            string codec = Config.VideoCodec?.Trim().ToLowerInvariant() ?? "h264";
            if (codec != "h264" && codec != "h265" && codec != "av1") codec = "h264";
            string cpuEncoder = codec switch { "h265" => "libx265", "av1" => "libsvtav1", _ => "libx264" };

            string gpu = EncodingHelper.NormalizeGpuSetting(Config.GpuEncoder?.Trim().ToLowerInvariant() ?? "auto");

            if (gpu != "auto")
            {
                string encoder = EncodingHelper.ResolveRequestedEncoder(gpu, codec);
                if (encoder == cpuEncoder || (ValidatedEncoders != null && ValidatedEncoders.Contains(encoder)))
                    return encoder;
                return cpuEncoder;
            }

            foreach (var g in new[] { "nvidia", "amd", "intel" })
            {
                string encoder = EncodingHelper.ResolveRequestedEncoder(g, codec);
                if (ValidatedEncoders != null && ValidatedEncoders.Contains(encoder))
                    return encoder;
            }
            return cpuEncoder;
        }
    }
}
