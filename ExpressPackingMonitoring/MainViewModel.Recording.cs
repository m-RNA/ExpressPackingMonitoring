using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using OpenCvSharp;
using AForge.Video.DirectShow;
using NAudio.CoreAudioApi;
using NAudio.Wave;

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
            _zoomPhase = ZoomPhase.None;
            _autoStopWarned = false;
            _maxDurationWarned = false;

            CancellationTokenSource oldCts;
            BlockingCollection<Mat> oldQueue;
            Task oldWriteTask;
            string? audioFilePath;
            bool audioFailedForThisRecording;
            long audioBytesWrittenForThisRecording;

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
            audioFilePath = StopAudioRecording();
            audioFailedForThisRecording = _audioFailedForCurrentRecording;
            audioBytesWrittenForThisRecording = _audioBytesWritten;

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
            var audioLogPath = _currentAudioLogPath;

            _recordStartTime = DateTime.MinValue;
            _currentScanRecord = null;
            _currentVideoFilePath = null;
            _currentVideoCodec = null;
            _currentVideoEncoder = null;
            _currentRecordId = 0;
            _currentFfmpegProcess = null;
            _recordingOrderId = null;

            _lastFinalizeTask = Task.Run(() => 
            {
                if (_isDisposed) return; // 销毁中不再执行数据库后的 UI 更新
                try
                {
                    long fileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
                    double recordDuration = (DateTime.Now - recordStart).TotalSeconds;

                    // 如果文件小于 50KB (比如启动报错或没数据)，或录制时长不足最低时长要求，作为无效数据丢弃
                    bool tooShort = Config.MinRecordingSeconds > 0 && recordDuration < Config.MinRecordingSeconds;
                    if (fileSize < 1024 * 50 || tooShort)
                    {
                        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                        DeleteAudioTempFile(audioFilePath);
                        _ = Application.Current.Dispatcher.InvokeAsync(() => {
                            if (!_isDisposed) {
                                _allLogs.Remove(scanRecord);
                                FilteredLogs.Remove(scanRecord);
                                if (tooShort && fileSize >= 1024 * 50)
                                {
                                    ShowToast($"⚠ 录像过短({recordDuration:F1}s)，已丢弃");
                                    SpeakWarning("录像过短，已丢弃");
                                }
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

                        // 自动将 MKV 转换为 MP4（无损容器转换）
                        ConvertMkvToMp4(filePath, audioFilePath, audioLogPath, audioFailedForThisRecording, audioBytesWrittenForThisRecording);

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
                catch (Exception ex)
                {
                    WriteAudioDiagnostic($"Finalize exception: {ex.Message}", audioLogPath);
                    WriteAudioDiagnostic($"Finalize 异常: {ex.Message}");
                }
                finally
                {
                    if (string.Equals(_currentAudioLogPath, audioLogPath, StringComparison.OrdinalIgnoreCase))
                        _currentAudioLogPath = null;
                }
            });
            
            // 如果是在关闭窗口时发生，不要解除 Busy，防止被再次点击
            if (Application.Current?.MainWindow != null && !_isDisposed)
            {
                IsBusy = false;
            }

            if (_pendingCameraRestart && !_isDisposed)
            {
                _pendingCameraRestart = false;
                _consecutiveRestartFailures = 0;
                RestartCamera();
                ShowToast("摄像头配置已生效");
            }
        }

        private string ResolveBestStoragePath()
        {
            string defaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Videos");

            if (Config.StorageLocations == null || Config.StorageLocations.Count == 0)
            {
                EnsureDirectoryWritable(defaultPath);
                return defaultPath;
            }

            var orderedLocations = Config.StorageLocations
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .OrderBy(x => x.Priority)
                .ToList();

            if (orderedLocations.Count == 0)
            {
                EnsureDirectoryWritable(defaultPath);
                return defaultPath;
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

                    // 检查写入权限
                    if (!IsDirectoryWritable(normalizedPath)) continue;

                    string? root = Path.GetPathRoot(Path.GetFullPath(normalizedPath));
                    if (string.IsNullOrEmpty(root)) continue;

                    var drive = new DriveInfo(root);
                    if (!drive.IsReady) continue;

                    // 5% 预留或 2GB
                    long safeBuffer = (long)Math.Max(2147483648, drive.TotalSize * 0.05);

                    if (drive.AvailableFreeSpace > safeBuffer)
                    {
                        return normalizedPath;
                    }
                }
                catch
                {
                    continue;
                }
            }

            // 如果都没有找到合适的（配额已满或剩余空间不足），尝试第一个能写的路径
            foreach (var loc in orderedLocations)
            {
                string path = Path.IsPathRooted(loc.Path) ? loc.Path : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, loc.Path);
                if (IsDirectoryWritable(path)) return path;
            }

            EnsureDirectoryWritable(defaultPath);
            return defaultPath;
        }

        private bool IsDirectoryWritable(string dirPath)
        {
            try
            {
                if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
                string testFile = Path.Combine(dirPath, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch { return false; }
        }

        private void EnsureDirectoryWritable(string dirPath)
        {
            try
            {
                if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Storage] 无法创建默认目录: {ex.Message}");
            }
        }

        private async Task InternalStartRecordingAsync()
        {
            var startupWatch = Stopwatch.StartNew();
            IsBusy = true;
            BusyText = "正在启动...";

            try
            {
                // 0. 环境预检查 (摄像头、麦克风)
                if (_videoSource == null || !_videoSource.IsRunning)
                {
                    // 尝试重启一次摄像头，以防万一用户刚插上
                    RestartCamera();
                    await Task.Delay(1000); // 给一点点启动时间

                    if (_videoSource == null || !_videoSource.IsRunning)
                    {
                        ShowToast("⚠ 摄像头未就绪，请检查连接");
                        SpeakWarning("摄像头未就绪");
                        return;
                    }
                }

                bool startAudioAfterVideo = Config.EnableAudioRecording && HasConfiguredAudioDevice();

                // 1. 彻底清理环境：如果系统残留了任何挂死的 ffmpeg，全部清掉
                _ = Task.Run(() => {
                    try {
                        foreach (var p in Process.GetProcessesByName("ffmpeg")) 
                        {
                            if ((DateTime.Now - p.StartTime).TotalMinutes > 2) p.Kill();
                        }
                    } catch { }
                });

                // 2. 初始化路径和文件名
                string baseFolder;
                try
                {
                    baseFolder = ResolveBestStoragePath();
                    if (!IsDirectoryWritable(baseFolder))
                    {
                        ShowToast("⚠ 存储路径不可写，请检查磁盘");
                        SpeakWarning("存储路径不可写");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    ShowToast($"⚠ 存储初始化失败: {ex.Message}");
                    return;
                }

                string dateFolder = Path.Combine(baseFolder, DateTime.Now.ToString("yyyy-MM-dd"));
                try
                {
                    if (!Directory.Exists(dateFolder)) Directory.CreateDirectory(dateFolder);
                }
                catch (Exception ex)
                {
                    ShowToast($"⚠ 无法创建日期目录: {ex.Message}");
                    return;
                }

                string fileName = $"{CurrentOrderId}_{DateTime.Now:yyyyMMdd_HHmmss}_{CurrentMode}.mkv";
                string filePath = Path.Combine(dateFolder, fileName);
                string audioFilePath = Path.ChangeExtension(filePath, ".wav");
                string audioLogPath = Path.ChangeExtension(filePath, ".audio.log");
                _currentAudioLogPath = audioLogPath;
                _audioFailedForCurrentRecording = false;
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
                    ClearCurrentAudioLogPath(audioLogPath);
                    return;
                }

                // 3. 开启新的生产者-消费者通道
                lock (_videoLock)
                {
                    _videoWriteQueue = new BlockingCollection<Mat>(300); // 增大缓冲区
                    _writeCts = new CancellationTokenSource();
                }

                // 4. 启动录制任务
                _recordingStartTimestamp = Stopwatch.GetTimestamp();
                _firstRecordingFrameWritten = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                _writeTask = Task.Run(() => BackgroundFFmpegRecordingLoop(filePath, ffmpegPath, _writeCts.Token));

                IsRecording = true;
                _recordStartTime = DateTime.Now;
                EnqueueLatestFrameForRecording();
                _lastMotionTime = DateTime.Now;
                _autoStopWarned = false;
                _maxDurationWarned = false;
                _previousCheckFrame?.Dispose();
                _previousCheckFrame = new Mat();

                // 5. 快速确认首帧是否已经写入 FFmpeg，避免固定等待拖慢开录。
                var firstFrameTask = _firstRecordingFrameWritten.Task;
                var startupCheck = await Task.WhenAny(firstFrameTask, _writeTask, Task.Delay(200));
                if (startupCheck == firstFrameTask)
                    Debug.WriteLine($"[RecordingStartup] first frame written in {firstFrameTask.Result} ms (total {startupWatch.ElapsedMilliseconds} ms)");
                else if (startupCheck != _writeTask)
                    Debug.WriteLine($"[RecordingStartup] first frame not confirmed within 200 ms (total {startupWatch.ElapsedMilliseconds} ms)");
                if (_writeTask.IsCompleted) 
                {
                    DeleteAudioTempFile(StopAudioRecording());
                    ClearCurrentAudioLogPath(audioLogPath);
                    IsRecording = false;
                    Debug.WriteLine("[MainVM] 启动检测：_writeTask 已结束，FFmpeg 启动失败");
                    // 启动阶段已经提前进入录制状态，失败时要回滚 UI 状态。
                    return; 
                }

                if (startAudioAfterVideo)
                {
                    WriteAudioDiagnostic($"准备启动麦克风录制: name={Config.AudioDeviceName}, moniker={(string.IsNullOrWhiteSpace(Config.AudioDeviceMoniker) ? "(empty)" : Config.AudioDeviceMoniker)}");
                    if (!StartAudioRecording(audioFilePath))
                    {
                        WriteAudioDiagnostic("麦克风录音启动失败");
                        ShowToast("音频录制启动失败");
                        SpeakWarning("音频录制启动失败");
                        try
                        {
                            lock (_videoLock)
                            {
                                _videoWriteQueue?.CompleteAdding();
                                _writeCts?.Cancel();
                            }
                            await Task.WhenAny(_writeTask, Task.Delay(3000));
                        }
                        catch { }
                        IsRecording = false;
                        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                        ClearCurrentAudioLogPath(audioLogPath);
                        return;
                    }
                }

                // 6. 在数据库中创建记录占位符
                _currentRecordId = _db?.InsertVideoRecord(_recordingOrderId, _recordingMode, _currentVideoCodec, _currentVideoEncoder, filePath, _recordStartTime) ?? 0;

                ShowToast("▶ 开始录像");
                Speak("开始录制", cancelPrevious: false);
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
            bool hasAudio = false;
            string requestedEncoder = encoder;
            string? firstError = null;

            var (ok, err) = RunFFmpegPipeline(filePath, ffmpegPath, token, w, h, fps, encoder, hasAudio);
            if (!ok && !token.IsCancellationRequested)
            {
                firstError = err;
                string fallbackEncoder = GetCpuEncoder();
                if (!string.Equals(encoder, fallbackEncoder, StringComparison.OrdinalIgnoreCase))
                {
                    WriteAudioDiagnostic($"视频编码器启动失败，改用 CPU 软编码重试: requested={encoder}, fallback={fallbackEncoder}, error={err}");
                    try { if (File.Exists(filePath) && new FileInfo(filePath).Length == 0) File.Delete(filePath); } catch { }

                    encoder = fallbackEncoder;
                    (ok, err) = RunFFmpegPipeline(filePath, ffmpegPath, token, w, h, fps, encoder, hasAudio);
                }
            }
            
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
                DeleteAudioTempFile(StopAudioRecording());
                try { if (File.Exists(filePath) && new FileInfo(filePath).Length == 0) File.Delete(filePath); } catch { }
                string errorDetail = string.IsNullOrWhiteSpace(firstError) || string.Equals(firstError, err, StringComparison.Ordinal)
                    ? err
                    : $"{firstError}\nCPU 软编码重试: {err}";

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
                    SpeakWarning("录制失败");
                    MessageBox.Show(
                        $"当前设置的编码器无法完成录制，视频未保存。\n\n请求编码器: {EncodingHelper.GetEncoderLabel(requestedEncoder)}\n错误详情: {errorDetail}\n\n已自动尝试 CPU 软编码；若仍失败，请检查摄像头画面和存储路径。",
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

        private void EnqueueLatestFrameForRecording()
        {
            try
            {
                BlockingCollection<Mat>? queue = _videoWriteQueue;
                if (queue == null || queue.IsAddingCompleted) return;

                Mat? frame = null;
                lock (_frameLock)
                {
                    if (_latestFrame != null && !_latestFrame.IsDisposed && !_latestFrame.Empty())
                        frame = _latestFrame.Clone();
                }

                if (frame == null) return;
                if (!queue.TryAdd(frame, 5))
                    frame.Dispose();
            }
            catch { }
        }

        private (bool ok, string error) RunFFmpegPipeline(string filePath, string ffmpegPath, CancellationToken token,
            int w, int h, int fps, string encoder, bool withAudio)
        {
            Process? ffmpeg = null;
            Stream? stdin = null;
            bool anyFrameWritten = false;
            string stderrText = "";
            bool stdinClosed = false;

            try
            {
                string args = BuildFFmpegArgs(w, h, fps, filePath, encoder, withAudio, GetVideoCqp());
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

                for (int wait = 0; wait < 30 && !ffmpeg.HasExited; wait += 30)
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
                    // 检查 FFmpeg 进程是否已经崩溃。如果已经退出，直接退出循环
                    if (ffmpeg.HasExited) 
                    {
                        frame?.Dispose();
                        break;
                    }

                    if (token.IsCancellationRequested) 
                    { 
                        frame?.Dispose(); 
                        break; 
                    }
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
                            if (ffmpeg.HasExited) { pipeError = true; break; }

                            if (toWrite.IsContinuous() && toWrite.Type() == MatType.CV_8UC3)
                            {
                                Marshal.Copy(toWrite.Data, buffer, 0, expectedBytes);
                                // 此处可能会抛出 IOException/InvalidOperationException，标志着管道断开
                                stdin.Write(buffer, 0, expectedBytes);
                                anyFrameWritten = true;
                                var firstFrameSignal = _firstRecordingFrameWritten;
                                if (firstFrameSignal != null && !firstFrameSignal.Task.IsCompleted)
                                {
                                    long elapsedMs = (long)(1000.0 * (Stopwatch.GetTimestamp() - _recordingStartTimestamp) / Stopwatch.Frequency);
                                    firstFrameSignal.TrySetResult(elapsedMs);
                                }
                            }
                        }
                        finally
                        {
                            if (needResize) toWrite.Dispose();
                        }
                    }
                    catch (Exception ex) 
                    { 
                        Debug.WriteLine($"[FFmpeg] 管道写入异常: {ex.Message}");
                        pipeError = true; 
                    }
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
                {
                    if (!string.IsNullOrWhiteSpace(stderrText))
                        Debug.WriteLine($"[FFmpeg] stderr (success): {stderrText[..Math.Min(stderrText.Length, 500)]}");
                    return (true, "");
                }

                string finalErr = ExtractFFmpegError(stderrText);
                if (string.IsNullOrWhiteSpace(finalErr))
                    finalErr = !fileOk ? "FFmpeg 未生成有效视频文件" : $"FFmpeg 退出码: {ffmpeg?.ExitCode}";
                return (false, finalErr);
            }
            catch (OperationCanceledException) { return (anyFrameWritten, ""); }
            catch (IOException ex)
            {
                return token.IsCancellationRequested && anyFrameWritten
                    ? (true, "")
                    : (false, ex.Message);
            }
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

        internal static string BuildFFmpegArgs(int w, int h, int fps, string filePath, string encoder, bool withAudio, int videoCqp)
        {
            string args = $"-y -fflags +genpts -use_wallclock_as_timestamps 1 -f rawvideo -video_size {w}x{h} -pixel_format bgr24 -framerate {fps} -i pipe:0";
            int cqp = videoCqp > 0 ? videoCqp : 25;
            int gop = Math.Max(1, fps * 2);

            if (encoder == "h264_nvenc") args += $" -c:v h264_nvenc -pix_fmt yuv420p -preset p4 -rc vbr -cq {cqp} -b:v 0 -g {gop} -max_muxing_queue_size 1024";
            else if (encoder == "h264_amf") args += $" -c:v h264_amf -pix_fmt yuv420p -quality balanced -rc cqp -qp_i {cqp} -qp_p {cqp} -g {gop}";
            else if (encoder == "h264_qsv") args += $" -c:v h264_qsv -pix_fmt nv12 -preset medium -global_quality {cqp} -g {gop}";
            else if (encoder == "libx264") args += $" -c:v libx264 -pix_fmt yuv420p -preset fast -crf {cqp} -g {gop}";
            else if (encoder == "hevc_nvenc") args += $" -c:v hevc_nvenc -pix_fmt yuv420p -preset p4 -rc vbr -cq {cqp} -b:v 0 -g {gop} -max_muxing_queue_size 1024";
            else if (encoder == "hevc_amf") args += $" -c:v hevc_amf -pix_fmt yuv420p -quality balanced -rc cqp -qp_i {cqp} -qp_p {cqp} -g {gop}";
            else if (encoder == "hevc_qsv") args += $" -c:v hevc_qsv -pix_fmt nv12 -preset medium -global_quality {cqp} -g {gop}";
            else if (encoder == "libx265") args += $" -c:v libx265 -pix_fmt yuv420p -preset fast -crf {cqp} -g {gop}";
            else if (encoder == "av1_nvenc") args += $" -c:v av1_nvenc -pix_fmt yuv420p -preset p4 -rc vbr -cq {cqp} -b:v 0 -g {gop} -max_muxing_queue_size 1024";
            else if (encoder == "av1_amf") args += $" -c:v av1_amf -pix_fmt yuv420p -quality balanced -rc cqp -qp_i {cqp} -qp_p {cqp} -g {gop}";
            else if (encoder == "av1_qsv") args += $" -c:v av1_qsv -pix_fmt nv12 -preset medium -global_quality {cqp} -g {gop}";
            else if (encoder == "libsvtav1") args += $" -c:v libsvtav1 -pix_fmt yuv420p -preset {GetCpuAv1Preset(w, h, fps)} -crf {cqp} -svtav1-params tune=0 -g {gop}";
            else args += $" -c:v {encoder} -pix_fmt yuv420p -g {gop}";

            args += $" -r {fps} -fps_mode cfr";

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

        private bool StartAudioRecording(string audioFilePath)
        {
            try
            {
                StopAudioRecording();

                var device = ResolveAudioEndpoint();
                if (device == null)
                {
                    Debug.WriteLine("[Audio] 未找到可用麦克风端点");
                    WriteAudioDiagnostic("未找到可用麦克风端点");
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(audioFilePath)!);

                var capture = CreateWasapiCapture(device);
                var writerFormat = CreatePcm16WaveFormat(capture.WaveFormat);
                var writer = new WaveFileWriter(audioFilePath, writerFormat);
                var writeQueue = new BlockingCollection<byte[]>(boundedCapacity: 150);
                var writeTask = Task.Run(() => AudioFileWriteLoop(writer, writeQueue));

                lock (_audioLock)
                {
                    _audioCapture = capture;
                    _audioWriter = writer;
                    _audioWriteQueue = writeQueue;
                    _audioFileWriteTask = writeTask;
                    _currentAudioFilePath = audioFilePath;
                    _audioStopRequested = false;
                    _audioRestarting = false;
                    _lastAudioDataAt = DateTime.Now;
                    _lastAudioPacketAt = DateTime.Now;
                    _audioSuppressUntil = DateTime.MinValue;
                    _audioBytesWritten = 0;
                    _audioPeakSinceLastCheck = 0;
                    _audioBytesSinceLastCheck = 0;
                    _silentAudioCheckCount = 0;
                    _audioMonitorLogTick = 0;
                    _audioConvertFailureCount = 0;
                    _audioSelectedSourceChannel = -1;
                    _audioResamplePosition = 0;
                    _audioPreviousSourceSample = 0;
                    _audioHasPreviousSourceSample = false;
                    _audioCaptureUnstable = false;
                    _audioGapCount = 0;
                    _audioMaxGapMs = 0;
                    _audioGapPaddingBytes = 0;
                    _audioWriteFailed = false;
                    _audioWriteQueueFullLogged = false;
                    _audioWriteQueueFullReported = false;
                    _audioFailedForCurrentRecording = false;
                    _audioMonitorCts = new CancellationTokenSource();
                }

                capture.StartRecording();
                _audioMonitorTask = Task.Run(() => AudioCaptureMonitorLoop(_audioMonitorCts.Token));
                Debug.WriteLine($"[Audio] 开始录音: {device.FriendlyName}");
                WriteAudioDiagnostic($"开始录音: device={device.FriendlyName}, sourceFormat={capture.WaveFormat}, wavFormat={writerFormat}");
                WriteAudioDiagnostic("WASAPI 采集模式: eventSync=true, bufferMs=20");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] 启动失败: {ex.Message}");
                WriteAudioDiagnostic($"启动失败: {ex.Message}");
                StopAudioRecording();
                DeleteAudioTempFile(audioFilePath);
                return false;
            }
        }

        private string? StopAudioRecording()
        {
            WasapiCapture? capture;
            WaveFileWriter? writer;
            BlockingCollection<byte[]>? writeQueue;
            Task? writeTask;
            bool writeFailed;
            byte[]? resampleTailBytes;
            string? audioFilePath;
            CancellationTokenSource? monitorCts;
            Task? monitorTask;

            lock (_audioLock)
            {
                _audioStopRequested = true;
                capture = _audioCapture;
                monitorCts = _audioMonitorCts;
                monitorTask = _audioMonitorTask;
                _audioMonitorCts = null;
                _audioMonitorTask = null;
                _audioRestarting = false;
            }

            try { monitorCts?.Cancel(); } catch { }
            try { capture?.StopRecording(); } catch { }
            try { capture?.Dispose(); } catch { }
            try { monitorTask?.Wait(1000); } catch { }
            try { monitorCts?.Dispose(); } catch { }

            lock (_audioLock)
            {
                writer = _audioWriter;
                writeQueue = _audioWriteQueue;
                writeTask = _audioFileWriteTask;
                writeFailed = _audioWriteFailed;
                resampleTailBytes = FlushResamplerTail(_audioPreviousSourceSample, _audioHasPreviousSourceSample, ref _audioResamplePosition);
                audioFilePath = _currentAudioFilePath;
                _audioCapture = null;
                _audioWriter = null;
                _audioWriteQueue = null;
                _audioFileWriteTask = null;
                _currentAudioFilePath = null;
            }

            if (resampleTailBytes != null && resampleTailBytes.Length > 0 && writeQueue != null && !writeQueue.IsAddingCompleted && !writeFailed)
            {
                try
                {
                    if (writeQueue.TryAdd(resampleTailBytes))
                        _audioBytesWritten += resampleTailBytes.Length;
                    else
                        writeFailed = true;
                }
                catch
                {
                    writeFailed = true;
                }
            }
            try { writeQueue?.CompleteAdding(); } catch { }

            bool writeCompleted = true;
            try
            {
                if (writeTask != null)
                    writeCompleted = writeTask.Wait(5000);
            }
            catch
            {
                writeCompleted = false;
            }
            if (!writeCompleted)
            {
                writeFailed = true;
                WriteAudioDiagnostic("WAV 写入超时，放弃本次音频");
            }
            if (_audioWriteQueueFullLogged && !_audioWriteQueueFullReported)
            {
                _audioWriteQueueFullReported = true;
                WriteAudioDiagnostic("WAV 写入队列已满，放弃本次音频");
            }
            if (writeTask == null)
            {
                try { writer?.Flush(); } catch { }
                try { writer?.Dispose(); } catch { }
            }
            try { writeQueue?.Dispose(); } catch { }

            lock (_audioLock)
            {
                writeFailed = writeFailed || _audioWriteFailed;
            }

            if (string.IsNullOrEmpty(audioFilePath)) return null;

            if (writeFailed)
            {
                _audioFailedForCurrentRecording = true;
                DeleteAudioTempFile(audioFilePath);
                return null;
            }

            try
            {
                if (IsCompletedAudioFileUsable(audioFilePath))
                {
                    if (_audioCaptureUnstable)
                    {
                        _audioFailedForCurrentRecording = true;
                        WriteAudioDiagnostic($"WAV 采集不稳定，跳过 MP4 合成并保留诊断文件: gaps={_audioGapCount}, maxGapMs={_audioMaxGapMs:F0}, paddedBytes={_audioGapPaddingBytes}");
                    }
                    return audioFilePath;
                }
            }
            catch { }

            _audioFailedForCurrentRecording = true;
            DeleteAudioTempFile(audioFilePath);
            return null;
        }

        private bool IsCompletedAudioFileUsable(string audioFilePath)
        {
            if (!File.Exists(audioFilePath) || new FileInfo(audioFilePath).Length <= 44)
                return false;

            try
            {
                using var reader = new WaveFileReader(audioFilePath);
                long dataBytes = reader.Length;
                long expectedBytes = _audioBytesWritten;
                long toleranceBytes = Math.Max(reader.WaveFormat.AverageBytesPerSecond, expectedBytes / 100);
                double durationSeconds = reader.TotalTime.TotalSeconds;
                bool byteCountOk = expectedBytes <= 0 || Math.Abs(dataBytes - expectedBytes) <= toleranceBytes;
                bool durationOk = durationSeconds > 0 && durationSeconds < TimeSpan.FromHours(12).TotalSeconds;

                if (!byteCountOk || !durationOk)
                {
                    WriteAudioDiagnostic($"WAV 完整性校验失败: dataBytes={dataBytes}, expectedBytes={expectedBytes}, duration={durationSeconds:F1}s, tolerance={toleranceBytes}");
                    return false;
                }
                WriteAudioDiagnostic($"WAV 完整性校验通过: dataBytes={dataBytes}, duration={durationSeconds:F1}s");
                return true;
            }
            catch (Exception ex)
            {
                WriteAudioDiagnostic($"WAV 完整性校验异常: {ex.Message}");
                return false;
            }
        }

        private WasapiCapture CreateWasapiCapture(MMDevice device)
        {
            var capture = new WasapiCapture(device, true, 20)
            {
                ShareMode = AudioClientShareMode.Shared
            };

            capture.DataAvailable += (_, e) =>
            {
                string? diagnosticMessage = null;
                lock (_audioLock)
                {
                    if (_audioWriter == null || e.BytesRecorded <= 0) return;
                    var now = DateTime.Now;
                    _lastAudioPacketAt = now;
                    int selectedChannel = _audioSelectedSourceChannel;
                    byte[]? pcmBytes = ConvertCaptureBufferToPcm16(
                        e.Buffer,
                        e.BytesRecorded,
                        capture.WaveFormat,
                        _audioWriter.WaveFormat,
                        ref selectedChannel,
                        ref _audioResamplePosition,
                        ref _audioPreviousSourceSample,
                        ref _audioHasPreviousSourceSample);
                    if (pcmBytes == null || pcmBytes.Length == 0)
                    {
                        _audioConvertFailureCount++;
                        if (_audioConvertFailureCount == 1 || _audioConvertFailureCount % 10 == 0)
                            diagnosticMessage = $"麦克风格式暂不支持转换: format={capture.WaveFormat}, bytes={e.BytesRecorded}, failures={_audioConvertFailureCount}";
                    }
                    else
                    {
                        _audioConvertFailureCount = 0;
                        bool suppressing = now < _audioSuppressUntil;
                        if (suppressing)
                        {
                            Array.Clear(pcmBytes, 0, pcmBytes.Length);
                            _audioSelectedSourceChannel = -1;
                            _audioResamplePosition = 0;
                            _audioPreviousSourceSample = 0;
                            _audioHasPreviousSourceSample = false;
                        }
                        else if (selectedChannel != _audioSelectedSourceChannel)
                        {
                            _audioSelectedSourceChannel = selectedChannel;
                            diagnosticMessage = $"麦克风输入通道已选择: channel={selectedChannel}, sourceChannels={capture.WaveFormat.Channels}";
                        }
                        var gapDiagnostic = PadAudioGapIfNeeded(now);
                        if (!string.IsNullOrEmpty(gapDiagnostic))
                            diagnosticMessage = string.IsNullOrEmpty(diagnosticMessage)
                                ? gapDiagnostic
                                : $"{diagnosticMessage}; {gapDiagnostic}";
                        UpdateAudioLevelStats(pcmBytes, pcmBytes.Length, _audioWriter.WaveFormat);
                        _audioBytesWritten += EnqueueAudioBytes(pcmBytes);
                        _lastAudioDataAt = DateTime.Now;
                    }
                }
                if (!string.IsNullOrEmpty(diagnosticMessage))
                    WriteAudioDiagnostic(diagnosticMessage);
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null)
                {
                    Debug.WriteLine($"[Audio] 录音停止异常: {e.Exception.Message}");
                    WriteAudioDiagnostic($"录音停止异常: {e.Exception.Message}");
                }

                if (ShouldRestartAudioCapture())
                    _ = Task.Run(() => RestartAudioCapture("stopped"));
            };

            return capture;
        }

        private string? PadAudioGapIfNeeded(DateTime now)
        {
            if (_audioWriter == null || _lastAudioDataAt == DateTime.MinValue) return null;

            double gapMs = (now - _lastAudioDataAt).TotalMilliseconds;
            if (gapMs <= 750) return null;

            int bytesPerSecond = _audioWriter.WaveFormat.AverageBytesPerSecond;
            int blockAlign = Math.Max(1, _audioWriter.WaveFormat.BlockAlign);
            int silenceBytes = (int)(bytesPerSecond * (gapMs / 1000.0));
            silenceBytes -= silenceBytes % blockAlign;
            if (silenceBytes <= 0) return null;

            _audioGapCount++;
            _audioGapPaddingBytes += silenceBytes;
            if (gapMs > _audioMaxGapMs)
                _audioMaxGapMs = gapMs;

            byte[] silence = new byte[Math.Min(silenceBytes, bytesPerSecond)];
            int remaining = silenceBytes;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, silence.Length);
                if (chunk == silence.Length)
                {
                    _audioBytesWritten += EnqueueAudioBytes(silence);
                }
                else
                {
                    byte[] partialSilence = new byte[chunk];
                    _audioBytesWritten += EnqueueAudioBytes(partialSilence);
                }
                remaining -= chunk;
            }
            bool unstable = gapMs >= 3000 || _audioGapPaddingBytes >= bytesPerSecond * 5L;
            if (unstable)
            {
                _audioCaptureUnstable = true;
                _audioFailedForCurrentRecording = true;
                return $"录音采集断流过长: gapMs={gapMs:F0}, gaps={_audioGapCount}, paddedBytes={_audioGapPaddingBytes}, maxGapMs={_audioMaxGapMs:F0}";
            }
            Debug.WriteLine($"[Audio] 补齐录音间隙: {gapMs:F0}ms");
            return $"补齐录音间隙: {gapMs:F0}ms, silenceBytes={silenceBytes}";
        }

        private int EnqueueAudioBytes(byte[] bytes)
        {
            if (_audioWriteFailed || _audioWriteQueue == null || _audioWriteQueue.IsAddingCompleted || bytes.Length == 0) return 0;
            try
            {
                if (!_audioWriteQueue.TryAdd(bytes))
                {
                    MarkAudioWriteQueueFull();
                    return 0;
                }
                return bytes.Length;
            }
            catch
            {
                MarkAudioWriteQueueFull();
                return 0;
            }
        }

        private void MarkAudioWriteQueueFull()
        {
            _audioWriteFailed = true;
            _audioWriteQueueFullLogged = true;
        }

        private void AudioFileWriteLoop(WaveFileWriter writer, BlockingCollection<byte[]> queue)
        {
            try
            {
                foreach (var bytes in queue.GetConsumingEnumerable())
                    writer.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] WAV 写入异常: {ex.Message}");
                lock (_audioLock)
                {
                    _audioWriteFailed = true;
                }
                WriteAudioDiagnostic($"WAV 写入异常: {ex.Message}");
            }
            finally
            {
                try { writer.Flush(); } catch { }
                try { writer.Dispose(); } catch { }
            }
        }

        private void UpdateAudioLevelStats(byte[] buffer, int bytesRecorded, WaveFormat format)
        {
            short peak;
            bool knownFormat = TryGetAudioPeak(buffer, bytesRecorded, format, out peak);

            if (knownFormat && peak > _audioPeakSinceLastCheck)
                _audioPeakSinceLastCheck = peak;
            else if (!knownFormat)
                _audioPeakSinceLastCheck = short.MaxValue;

            _audioBytesSinceLastCheck += bytesRecorded;
        }

        internal static WaveFormat CreatePcm16WaveFormat(WaveFormat sourceFormat)
        {
            return new WaveFormat(48000, 16, 1);
        }

        internal static byte[]? ConvertCaptureBufferToPcm16(
            byte[] buffer,
            int bytesRecorded,
            WaveFormat sourceFormat,
            WaveFormat targetFormat,
            ref int selectedSourceChannel,
            ref double resamplePosition,
            ref short previousSourceSample,
            ref bool hasPreviousSourceSample)
        {
            if (bytesRecorded <= 0) return Array.Empty<byte>();

            int sourceChannels = sourceFormat.Channels;
            int targetChannels = targetFormat.Channels;
            int blockAlign = sourceFormat.BlockAlign;
            int bitsPerSample = sourceFormat.BitsPerSample;
            if (sourceChannels <= 0 || targetChannels != 1 || blockAlign <= 0 || bitsPerSample <= 0) return null;

            int bytesPerSample = bitsPerSample / 8;
            if (bytesPerSample <= 0 || blockAlign < sourceChannels * bytesPerSample) return null;

            int frames = bytesRecorded / blockAlign;
            if (frames <= 0) return Array.Empty<byte>();

            bool isFloat = IsFloatWaveFormat(sourceFormat);
            bool isPcm = IsPcmWaveFormat(sourceFormat);
            if (!isFloat && !isPcm) return null;

            int selectedChannel = SelectAudioSourceChannel(buffer, frames, sourceChannels, blockAlign, bytesPerSample, isFloat, selectedSourceChannel);
            if (selectedChannel != selectedSourceChannel)
                selectedSourceChannel = selectedChannel;

            short[] monoSamples = new short[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                int frameOffset = frame * blockAlign;
                int sampleOffset = frameOffset + selectedChannel * bytesPerSample;
                monoSamples[frame] = isFloat
                    ? ReadFloatSampleAsPcm16(buffer, sampleOffset, bytesPerSample)
                    : ReadPcmSampleAsPcm16(buffer, sampleOffset, bytesPerSample);
            }
            return ResampleMonoPcm16ToBytes(monoSamples, sourceFormat.SampleRate, targetFormat.SampleRate, ref resamplePosition, ref previousSourceSample, ref hasPreviousSourceSample);
        }

        private static byte[] ResampleMonoPcm16ToBytes(
            short[] sourceSamples,
            int sourceSampleRate,
            int targetSampleRate,
            ref double resamplePosition,
            ref short previousSourceSample,
            ref bool hasPreviousSourceSample)
        {
            if (sourceSamples.Length == 0) return Array.Empty<byte>();

            if (sourceSampleRate == targetSampleRate)
            {
                previousSourceSample = sourceSamples[^1];
                hasPreviousSourceSample = true;
                return Pcm16SamplesToBytes(sourceSamples);
            }

            int prefix = hasPreviousSourceSample ? 1 : 0;
            short[] samples = new short[sourceSamples.Length + prefix];
            if (hasPreviousSourceSample)
                samples[0] = previousSourceSample;
            Array.Copy(sourceSamples, 0, samples, prefix, sourceSamples.Length);

            if (samples.Length < 2)
            {
                previousSourceSample = samples[^1];
                hasPreviousSourceSample = true;
                return Array.Empty<byte>();
            }

            double step = (double)sourceSampleRate / targetSampleRate;
            var output = new List<short>(Math.Max(1, (int)(sourceSamples.Length / step) + 2));
            while (resamplePosition + 1 < samples.Length)
            {
                int index = (int)resamplePosition;
                double fraction = resamplePosition - index;
                double sample = samples[index] + (samples[index + 1] - samples[index]) * fraction;
                output.Add((short)Math.Clamp((int)Math.Round(sample), short.MinValue, short.MaxValue));
                resamplePosition += step;
            }

            resamplePosition -= samples.Length - 1;
            if (resamplePosition < 0) resamplePosition = 0;
            previousSourceSample = sourceSamples[^1];
            hasPreviousSourceSample = true;
            return Pcm16SamplesToBytes(output);
        }

        internal static byte[]? FlushResamplerTail(short previousSourceSample, bool hasPreviousSourceSample, ref double resamplePosition)
        {
            if (!hasPreviousSourceSample || resamplePosition <= 0) return null;

            var output = new List<short>(1);
            if (resamplePosition < 1)
                output.Add(previousSourceSample);
            resamplePosition = 0;
            return output.Count == 0 ? null : Pcm16SamplesToBytes(output);
        }

        private static byte[] Pcm16SamplesToBytes(IReadOnlyList<short> samples)
        {
            byte[] output = new byte[samples.Count * 2];
            int outOffset = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                short sample = samples[i];
                output[outOffset++] = (byte)(sample & 0xff);
                output[outOffset++] = (byte)((sample >> 8) & 0xff);
            }
            return output;
        }

        private static int SelectAudioSourceChannel(byte[] buffer, int frames, int channels, int blockAlign, int bytesPerSample, bool isFloat, int currentChannel)
        {
            if (channels <= 1)
                return 0;

            long[] energy = new long[channels];
            for (int frame = 0; frame < frames; frame++)
            {
                int frameOffset = frame * blockAlign;
                for (int channel = 0; channel < channels; channel++)
                {
                    int sampleOffset = frameOffset + channel * bytesPerSample;
                    short sample = isFloat
                        ? ReadFloatSampleAsPcm16(buffer, sampleOffset, bytesPerSample)
                        : ReadPcmSampleAsPcm16(buffer, sampleOffset, bytesPerSample);
                    energy[channel] += Math.Abs((int)sample);
                }
            }

            int selected = 0;
            long strongest = energy[0];
            for (int channel = 1; channel < channels; channel++)
            {
                if (energy[channel] > strongest)
                {
                    selected = channel;
                    strongest = energy[channel];
                }
            }

            long activeThreshold = (long)frames * 16;
            if (currentChannel < 0 || currentChannel >= channels)
                return strongest > activeThreshold ? selected : 0;

            long currentEnergy = energy[currentChannel];
            bool candidateIsActive = strongest > activeThreshold;
            bool candidateClearlyStronger = strongest > Math.Max(currentEnergy * 4, currentEnergy + activeThreshold);
            if (selected != currentChannel && candidateIsActive && candidateClearlyStronger)
                return selected;

            selected = currentChannel;
            return selected;
        }

        private static bool IsFloatWaveFormat(WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat) return true;
            if (format.Encoding != WaveFormatEncoding.Extensible) return false;

            return format is WaveFormatExtensible extensible
                && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        }

        private static bool IsPcmWaveFormat(WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.Pcm) return true;
            if (format.Encoding != WaveFormatEncoding.Extensible) return false;

            return format is WaveFormatExtensible extensible
                && extensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71");
        }

        private static short ReadFloatSampleAsPcm16(byte[] buffer, int offset, int bytesPerSample)
        {
            if (bytesPerSample != 4 || offset + 4 > buffer.Length) return 0;
            float value = BitConverter.ToSingle(buffer, offset);
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 0;
            value = Math.Clamp(value, -1.0f, 1.0f);
            return (short)Math.Round(value * short.MaxValue);
        }

        private static short ReadPcmSampleAsPcm16(byte[] buffer, int offset, int bytesPerSample)
        {
            if (offset + bytesPerSample > buffer.Length) return 0;
            return bytesPerSample switch
            {
                1 => (short)((buffer[offset] - 128) << 8),
                2 => BitConverter.ToInt16(buffer, offset),
                3 => (short)(ReadInt24(buffer, offset) >> 8),
                4 => (short)(BitConverter.ToInt32(buffer, offset) >> 16),
                _ => 0
            };
        }

        private static int ReadInt24(byte[] buffer, int offset)
        {
            int value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
            if ((value & 0x800000) != 0)
                value |= unchecked((int)0xff000000);
            return value;
        }

        internal static bool TryGetAudioPeak(byte[] buffer, int bytesRecorded, WaveFormat format, out short peak)
        {
            peak = 0;

            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                for (int i = 0; i + 3 < bytesRecorded; i += 4)
                {
                    float sample = BitConverter.ToSingle(buffer, i);
                    int scaled = (int)Math.Clamp(Math.Abs(sample) * short.MaxValue, 0, short.MaxValue);
                    if (scaled > peak) peak = (short)scaled;
                }
                return true;
            }

            if (format.Encoding != WaveFormatEncoding.Pcm)
                return false;

            if (format.BitsPerSample == 16)
            {
                for (int i = 0; i + 1 < bytesRecorded; i += 2)
                {
                    short sample = BitConverter.ToInt16(buffer, i);
                    short abs = sample == short.MinValue ? short.MaxValue : (short)Math.Abs(sample);
                    if (abs > peak) peak = abs;
                }
                return true;
            }

            if (format.BitsPerSample == 24)
            {
                for (int i = 0; i + 2 < bytesRecorded; i += 3)
                {
                    int sample = buffer[i] | (buffer[i + 1] << 8) | (buffer[i + 2] << 16);
                    if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                    int scaled = Math.Min(short.MaxValue, Math.Abs(sample >> 8));
                    if (scaled > peak) peak = (short)scaled;
                }
                return true;
            }

            if (format.BitsPerSample == 32)
            {
                for (int i = 0; i + 3 < bytesRecorded; i += 4)
                {
                    int sample = BitConverter.ToInt32(buffer, i);
                    int scaled = Math.Min(short.MaxValue, Math.Abs(sample >> 16));
                    if (scaled > peak) peak = (short)scaled;
                }
                return true;
            }

            return false;
        }

        private async Task AudioCaptureMonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, token);
                    if (token.IsCancellationRequested) break;

                    DateTime lastDataAt;
                    bool shouldMonitor;
                    short peak;
                    long bytes;
                    int silentCount;
                    bool shouldLogLevel;
                    bool shouldReportQueueFull;
                    lock (_audioLock)
                    {
                        shouldMonitor = !_audioStopRequested && _audioWriter != null && _audioCapture != null;
                        lastDataAt = _lastAudioDataAt;
                        if (_lastAudioPacketAt > lastDataAt)
                            lastDataAt = _lastAudioPacketAt;
                        peak = _audioPeakSinceLastCheck;
                        bytes = _audioBytesSinceLastCheck;
                        _audioPeakSinceLastCheck = 0;
                        _audioBytesSinceLastCheck = 0;

                        if (shouldMonitor && bytes > 0 && peak <= 1)
                            _silentAudioCheckCount++;
                        else if (bytes > 0 && peak > 1)
                            _silentAudioCheckCount = 0;
                        silentCount = _silentAudioCheckCount;
                        _audioMonitorLogTick++;
                        shouldLogLevel = silentCount > 0 || _audioMonitorLogTick % 5 == 0;
                        shouldReportQueueFull = _audioWriteQueueFullLogged && !_audioWriteQueueFullReported;
                        if (shouldReportQueueFull)
                            _audioWriteQueueFullReported = true;
                    }

                    if (shouldReportQueueFull)
                        WriteAudioDiagnostic("WAV 写入队列已满，放弃本次音频");

                    if (shouldMonitor && (DateTime.Now - lastDataAt).TotalSeconds > 5)
                    {
                        WriteAudioDiagnostic($"音频数据断流: lastDataAge={(DateTime.Now - lastDataAt).TotalSeconds:F1}s");
                        RestartAudioCapture("no-data");
                    }
                    else if (shouldMonitor && bytes > 0)
                    {
                        if (shouldLogLevel)
                            WriteAudioDiagnostic($"音频电平: peak={peak}, bytes={bytes}, silentCount={silentCount}, silentRestart=disabled");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Audio] 监控异常: {ex.Message}");
                    WriteAudioDiagnostic($"监控异常: {ex.Message}");
                }
            }
        }

        private bool ShouldRestartAudioCapture()
        {
            lock (_audioLock)
            {
                return !_audioStopRequested && _audioWriter != null && !_audioRestarting;
            }
        }

        private void RestartAudioCapture(string reason)
        {
            WasapiCapture? oldCapture = null;

            lock (_audioLock)
            {
                if (_audioStopRequested || _audioWriter == null || _audioRestarting) return;
                _audioRestarting = true;
                oldCapture = _audioCapture;
                _audioCapture = null;
            }

            try { oldCapture?.StopRecording(); } catch { }
            try { oldCapture?.Dispose(); } catch { }

            try
            {
                var device = ResolveAudioEndpoint();
                if (device == null)
                {
                    Debug.WriteLine($"[Audio] 重启失败({reason}): 未找到麦克风端点");
                    WriteAudioDiagnostic($"重启失败({reason}): 未找到麦克风端点");
                    return;
                }

                var capture = CreateWasapiCapture(device);
                lock (_audioLock)
                {
                    if (_audioStopRequested || _audioWriter == null)
                    {
                        try { capture.Dispose(); } catch { }
                        return;
                    }
                    var restartFormat = CreatePcm16WaveFormat(capture.WaveFormat);
                    if (restartFormat.SampleRate != _audioWriter.WaveFormat.SampleRate
                        || restartFormat.Channels != _audioWriter.WaveFormat.Channels)
                    {
                        try { capture.Dispose(); } catch { }
                        WriteAudioDiagnostic($"重启失败({reason}): 麦克风格式变化 sourceFormat={capture.WaveFormat}, wavFormat={_audioWriter.WaveFormat}");
                        return;
                    }
                    _audioCapture = capture;
                    _lastAudioDataAt = DateTime.Now;
                    _lastAudioPacketAt = DateTime.Now;
                    _audioSuppressUntil = DateTime.Now.AddMilliseconds(500);
                    _audioPeakSinceLastCheck = 0;
                    _audioBytesSinceLastCheck = 0;
                    _silentAudioCheckCount = 0;
                    _audioMonitorLogTick = 0;
                    _audioConvertFailureCount = 0;
                    _audioResamplePosition = 0;
                    _audioPreviousSourceSample = 0;
                    _audioHasPreviousSourceSample = false;
                }

                capture.StartRecording();
                Debug.WriteLine($"[Audio] 已重启录音({reason}): {device.FriendlyName}");
                WriteAudioDiagnostic($"已重启录音({reason}): device={device.FriendlyName}, sourceFormat={capture.WaveFormat}, wavFormat={CreatePcm16WaveFormat(capture.WaveFormat)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] 重启异常({reason}): {ex.Message}");
                WriteAudioDiagnostic($"重启异常({reason}): {ex.Message}");
            }
            finally
            {
                lock (_audioLock)
                {
                    _audioRestarting = false;
                }
            }
        }

        private MMDevice? ResolveAudioEndpoint()
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            if (devices == null || devices.Count == 0) return null;

            MMDevice? defaultDevice = null;
            try { defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); } catch { }

            bool hasConfiguredEndpoint = false;
            if (!string.IsNullOrWhiteSpace(Config.AudioDeviceMoniker))
            {
                hasConfiguredEndpoint = true;
                foreach (var device in devices)
                {
                    if (AudioEndpointMatches(device.ID, Config.AudioDeviceMoniker))
                        return device;
                }
            }

            if (!string.IsNullOrWhiteSpace(Config.AudioDeviceName))
            {
                hasConfiguredEndpoint = true;
                foreach (var device in devices)
                {
                    if (AudioEndpointMatches(device.FriendlyName, Config.AudioDeviceName)
                        || AudioEndpointMatches(GetEndpointDisplayName(device), Config.AudioDeviceName))
                        return device;
                }
            }

            if (hasConfiguredEndpoint)
                return null;

            return defaultDevice ?? devices[0];
        }

        private static string GetEndpointDisplayName(MMDevice device)
        {
            try { return device.DeviceFriendlyName; } catch { return device.FriendlyName; }
        }

        private static bool AudioEndpointMatches(string endpointName, string configuredName)
        {
            if (string.IsNullOrWhiteSpace(endpointName) || string.IsNullOrWhiteSpace(configuredName))
                return false;

            return endpointName.Equals(configuredName, StringComparison.OrdinalIgnoreCase)
                || endpointName.Contains(configuredName, StringComparison.OrdinalIgnoreCase)
                || configuredName.Contains(endpointName, StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteAudioTempFile(string? audioFilePath)
        {
            try
            {
                if (!string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath))
                    File.Delete(audioFilePath);
            }
            catch { }
        }

        private bool HasConfiguredAudioDevice()
        {
            return !string.IsNullOrWhiteSpace(Config.AudioDeviceMoniker)
                || (!string.IsNullOrWhiteSpace(Config.AudioDeviceName)
                    && Config.AudioDeviceName != "未检测到麦克风");
        }

        private bool IsConfiguredAudioDevice(string name, string moniker)
        {
            if (!string.IsNullOrWhiteSpace(Config.AudioDeviceMoniker)
                && string.Equals(moniker, Config.AudioDeviceMoniker, StringComparison.OrdinalIgnoreCase))
                return true;

            return !string.IsNullOrWhiteSpace(Config.AudioDeviceName)
                && string.Equals(name, Config.AudioDeviceName, StringComparison.OrdinalIgnoreCase);
        }

        private int GetVideoCqp() => Config.VideoCqp > 0 ? Config.VideoCqp : 25;

        /// <summary>
        /// 录制完成后自动将 MKV 无损转换为 MP4（容器转换，不重新编码）
        /// </summary>
        private void ConvertMkvToMp4(string mkvPath, string? audioPath = null, string? audioLogPath = null, bool audioFailed = false, long audioBytesWritten = 0)
        {
            try
            {
                if (!File.Exists(mkvPath) || !mkvPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                    return;

                string ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    Debug.WriteLine("[MkvToMp4] FFmpeg 未找到，跳过自动转换");
                    return;
                }

                string mp4Path = Path.ChangeExtension(mkvPath, ".mp4");
                if (audioFailed)
                {
                    WriteAudioDiagnostic($"音频录制失败，已保留 MKV: mkv={mkvPath}", audioLogPath);
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_isDisposed)
                            ShowToast("⚠️ 音频录制失败，已保留原始文件");
                    });
                    return;
                }
                if (!ValidateAudioCaptureForMux(audioPath, audioLogPath, audioBytesWritten))
                {
                    Debug.WriteLine("[MkvToMp4] WAV 音频疑似提前静音，跳过 MP4 合成");
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_isDisposed)
                            ShowToast("⚠️ 音频疑似提前静音，已保留原始文件");
                    });
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = BuildMkvToMp4Args(mkvPath, audioPath, mp4Path, Config.AudioSyncOffsetMs),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Debug.WriteLine("[MkvToMp4] FFmpeg 进程启动失败");
                    return;
                }

                string convertStderr;
                bool convertExited = WaitForProcessExit(process, GetMediaProcessTimeoutMs(mkvPath, audioPath), out convertStderr);

                bool hasExternalAudio = !string.IsNullOrEmpty(audioPath) && File.Exists(audioPath);
                bool convertedOk = convertExited
                    && process.ExitCode == 0
                    && File.Exists(mp4Path)
                    && new FileInfo(mp4Path).Length > 0
                    && ValidateConvertedMp4(ffmpegPath, mp4Path, hasExternalAudio, audioLogPath);
                if (!convertExited)
                    WriteAudioDiagnostic($"MP4 转换超时: {convertStderr}", audioLogPath);

                if (convertedOk)
                {
                    // 转换成功，删除原始 MKV
                    try { File.Delete(mkvPath); } catch { }
                    DeleteAudioTempFile(audioPath);
                    // 更新数据库中的文件路径
                    _db?.UpdateVideoFilePath(mkvPath, mp4Path);
                    Debug.WriteLine($"[MkvToMp4] 转换成功: {mp4Path}");
                }
                else
                {
                    // 转换失败，保留 MKV，清理可能的残留 MP4
                    try { if (File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }
                    Debug.WriteLine($"[MkvToMp4] 转换失败，保留原始 MKV");
                    WriteAudioDiagnostic($"MP4 转换或音轨校验失败，已保留 MKV/WAV: mkv={mkvPath}, wav={audioPath}", audioLogPath);
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_isDisposed)
                            ShowToast("⚠ 音轨校验失败，已保留原始文件");
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MkvToMp4] 异常: {ex.Message}");
            }
        }

        internal static string BuildMkvToMp4Args(string mkvPath, string? audioPath, string mp4Path, int audioSyncOffsetMs)
        {
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
                return $"-y -i \"{mkvPath}\" -vcodec copy -acodec copy \"{mp4Path}\"";

            int offsetMs = Math.Clamp(audioSyncOffsetMs, -5000, 5000);
            string audioMap = "[a]";
            string filter;

            if (offsetMs > 0)
            {
                filter = $" -filter_complex \"[1:a]adelay={offsetMs}:all=1,apad[a]\"";
            }
            else if (offsetMs < 0)
            {
                double trimStartSec = Math.Abs(offsetMs) / 1000.0;
                filter = $" -filter_complex \"[1:a]atrim=start={trimStartSec:0.###},asetpts=PTS-STARTPTS,apad[a]\"";
            }
            else
            {
                filter = " -filter_complex \"[1:a]apad[a]\"";
            }

            return $"-y -i \"{mkvPath}\" -i \"{audioPath}\"{filter} -map 0:v:0 -map \"{audioMap}\" -c:v copy -c:a aac -b:a 128k -shortest \"{mp4Path}\"";
        }

        private bool ValidateConvertedMp4(string ffmpegPath, string mp4Path, bool requireAudio, string? audioLogPath)
        {
            if (!File.Exists(mp4Path)) return false;
            if (!requireAudio) return true;

            string decodedWavPath = Path.Combine(
                Path.GetDirectoryName(mp4Path) ?? AppDomain.CurrentDomain.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(mp4Path)}.mp4_audio_check_{Guid.NewGuid():N}.wav");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -v error -i \"{mp4Path}\" -map 0:a:0 -ac 1 -ar 48000 -sample_fmt s16 \"{decodedWavPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Debug.WriteLine("[MkvToMp4] 音轨校验进程启动失败");
                    return false;
                }

                string stderr;
                bool exited = WaitForProcessExit(process, GetMediaProcessTimeoutMs(mp4Path), out stderr);

                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    Debug.WriteLine("[MkvToMp4] 音轨校验超时");
                    return false;
                }

                bool ok = process.ExitCode == 0;
                WriteAudioDiagnostic($"MP4 audio validation result: ok={ok}, exitCode={process.ExitCode}", audioLogPath);
                if (!ok)
                {
                    Debug.WriteLine($"[MkvToMp4] 音轨校验失败: {stderr}");
                    WriteAudioDiagnostic($"MP4 音轨校验失败: {stderr}");
                    return false;
                }

                using var reader = new WaveFileReader(decodedWavPath);
                WriteAudioDiagnostic($"MP4 解码音轨: duration={reader.TotalTime.TotalSeconds:F2}s, format={reader.WaveFormat}, bytes={reader.Length}", audioLogPath);
                var timeline = LogAudioPeakTimeline(reader, audioLogPath, "MP4");
                if (!IsAudioTimelineUsable(reader.TotalTime.TotalSeconds, timeline.FirstActiveSecond, timeline.LastActiveSecond, timeline.ActiveWindowCount, timeline.MaxConsecutiveActiveWindows, out string reason))
                {
                    WriteAudioDiagnostic($"MP4 音轨疑似提前静音: {reason}", audioLogPath);
                    return false;
                }

                WriteAudioDiagnostic("MP4 音轨完整解码和时间线校验通过", audioLogPath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MkvToMp4] 音轨校验异常: {ex.Message}");
                WriteAudioDiagnostic($"MP4 音轨校验异常: {ex.Message}");
                return false;
            }
            finally
            {
                try { if (File.Exists(decodedWavPath)) File.Delete(decodedWavPath); } catch { }
            }
        }

        private bool ValidateAudioCaptureForMux(string? audioPath, string? audioLogPath, long audioBytesWritten)
        {
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath)) return true;

            var summary = LogAudioCaptureSummary(audioPath, audioLogPath, audioBytesWritten);
            if (!summary.Checked) return true;

            if (!IsAudioTimelineUsable(summary.DurationSeconds, summary.FirstActiveSecond, summary.LastActiveSecond, summary.ActiveWindowCount, summary.MaxConsecutiveActiveWindows, out string reason))
            {
                WriteAudioDiagnostic($"WAV 音频疑似提前静音: {reason}", audioLogPath);
                return false;
            }
            return true;
        }

        private (bool Checked, double DurationSeconds, double FirstActiveSecond, double LastActiveSecond, int ActiveWindowCount, int MaxConsecutiveActiveWindows) LogAudioCaptureSummary(string? audioPath, string? audioLogPath, long audioBytesWritten)
        {
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
                return (false, 0, -1, -1, 0, 0);

            try
            {
                using var reader = new WaveFileReader(audioPath);
                string summary = $"WAV 时长={reader.TotalTime.TotalSeconds:F1}s 大小={new FileInfo(audioPath).Length} bytes 写入字节={audioBytesWritten}";
                Debug.WriteLine($"[Audio] {summary}");
                WriteAudioDiagnostic(summary, audioLogPath);
                var timeline = LogAudioPeakTimeline(reader, audioLogPath);
                return (true, reader.TotalTime.TotalSeconds, timeline.FirstActiveSecond, timeline.LastActiveSecond, timeline.ActiveWindowCount, timeline.MaxConsecutiveActiveWindows);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] WAV 检查失败: {ex.Message}");
                WriteAudioDiagnostic($"WAV 检查失败: {ex.Message}");
                return (false, 0, -1, -1, 0, 0);
            }
        }

        internal static bool IsAudioTimelineUsable(double durationSeconds, double firstActiveSecond, double lastActiveSecond, int activeWindowCount, int maxConsecutiveActiveWindows, out string reason)
        {
            reason = string.Empty;
            if (durationSeconds < 10) return true;

            if (lastActiveSecond < 0)
            {
                reason = $"duration={durationSeconds:F1}s, lastActive=none";
                return false;
            }

            double leadingSilentSeconds = firstActiveSecond < 0 ? durationSeconds : firstActiveSecond;
            double trailingSilentSeconds = durationSeconds - lastActiveSecond;
            int sparsePulseLimit = Math.Max(2, (int)Math.Floor(durationSeconds / 6.0));
            if (durationSeconds >= 10
                && leadingSilentSeconds >= Math.Max(5, durationSeconds * 0.5)
                && activeWindowCount <= 3)
            {
                reason = $"duration={durationSeconds:F1}s, firstActive={firstActiveSecond:F1}s, activeWindows={activeWindowCount}, leadingSilent={leadingSilentSeconds:F1}s";
                return false;
            }

            if (durationSeconds >= 10
                && activeWindowCount > 0
                && activeWindowCount <= sparsePulseLimit
                && maxConsecutiveActiveWindows <= 2)
            {
                reason = $"duration={durationSeconds:F1}s, firstActive={firstActiveSecond:F1}s, lastActive={lastActiveSecond:F1}s, activeWindows={activeWindowCount}, maxConsecutive={maxConsecutiveActiveWindows}, sparsePulseLimit={sparsePulseLimit}";
                return false;
            }

            if (durationSeconds >= 10
                && trailingSilentSeconds >= Math.Max(5, durationSeconds * 0.5)
                && activeWindowCount <= 3)
            {
                reason = $"duration={durationSeconds:F1}s, lastActive={lastActiveSecond:F1}s, activeWindows={activeWindowCount}, trailingSilent={trailingSilentSeconds:F1}s";
                return false;
            }

            if (durationSeconds >= 30
                && activeWindowCount > 0
                && activeWindowCount <= 4
                && maxConsecutiveActiveWindows <= 2)
            {
                reason = $"duration={durationSeconds:F1}s, firstActive={firstActiveSecond:F1}s, lastActive={lastActiveSecond:F1}s, activeWindows={activeWindowCount}, maxConsecutive={maxConsecutiveActiveWindows}";
                return false;
            }

            if (durationSeconds >= 30
                && trailingSilentSeconds >= 15
                && activeWindowCount <= 3)
            {
                reason = $"duration={durationSeconds:F1}s, lastActive={lastActiveSecond:F1}s, activeWindows={activeWindowCount}, trailingSilent={trailingSilentSeconds:F1}s";
                return false;
            }

            return true;
        }

        private (double FirstActiveSecond, double LastActiveSecond, int ActiveWindowCount, int MaxConsecutiveActiveWindows) LogAudioPeakTimeline(WaveFileReader reader, string? audioLogPath, string sourceLabel = "WAV")
        {
            try
            {
                reader.Position = 0;
                int bytesPerSecond = Math.Max(1, reader.WaveFormat.AverageBytesPerSecond);
                int blockAlign = Math.Max(1, reader.WaveFormat.BlockAlign);
                int windowBytes = bytesPerSecond;
                windowBytes -= windowBytes % blockAlign;
                if (windowBytes <= 0) windowBytes = bytesPerSecond;

                byte[] buffer = new byte[windowBytes];
                int windowIndex = 0;
                double firstActiveSecond = -1;
                double lastActiveSecond = -1;
                int activeWindowCount = 0;
                int consecutiveActiveWindows = 0;
                int maxConsecutiveActiveWindows = 0;
                var parts = new List<string>();

                while (true)
                {
                    int totalRead = 0;
                    while (totalRead < buffer.Length)
                    {
                        int read = reader.Read(buffer, totalRead, buffer.Length - totalRead);
                        if (read <= 0) break;
                        totalRead += read;
                    }
                    if (totalRead <= 0) break;

                    short peak;
                    bool known = TryGetAudioPeak(buffer, totalRead, reader.WaveFormat, out peak);
                    double start = windowIndex;
                    double end = Math.Min(reader.TotalTime.TotalSeconds, start + (double)totalRead / bytesPerSecond);
                    parts.Add(known ? $"{start:F0}-{end:F0}s={peak}" : $"{start:F0}-{end:F0}s=unknown");
                    if (known && peak > 32)
                    {
                        if (firstActiveSecond < 0)
                            firstActiveSecond = start;
                        lastActiveSecond = end;
                        activeWindowCount++;
                        consecutiveActiveWindows++;
                        if (consecutiveActiveWindows > maxConsecutiveActiveWindows)
                            maxConsecutiveActiveWindows = consecutiveActiveWindows;
                    }
                    else
                    {
                        consecutiveActiveWindows = 0;
                    }
                    windowIndex++;
                }

                string timeline = $"{sourceLabel} 分段峰值: {string.Join("; ", parts)}; firstActive={firstActiveSecond:F1}s; lastActive={lastActiveSecond:F1}s; activeWindows={activeWindowCount}; maxConsecutive={maxConsecutiveActiveWindows}; activeThreshold=32";
                Debug.WriteLine($"[Audio] {timeline}");
                WriteAudioDiagnostic(timeline, audioLogPath);
                return (firstActiveSecond, lastActiveSecond, activeWindowCount, maxConsecutiveActiveWindows);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] {sourceLabel} 分段峰值检查失败: {ex.Message}");
                WriteAudioDiagnostic($"{sourceLabel} 分段峰值检查失败: {ex.Message}", audioLogPath);
                return (-1, -1, 0, 0);
            }
        }

        private static bool WaitForProcessExit(Process process, int timeoutMs, out string stderr)
        {
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            bool exited = process.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { process.Kill(); } catch { }
                try { process.WaitForExit(3000); } catch { }
            }

            stderr = TryGetProcessOutput(stderrTask);
            _ = TryGetProcessOutput(stdoutTask);
            return exited;
        }

        private static string TryGetProcessOutput(Task<string> outputTask)
        {
            try
            {
                return outputTask.Wait(3000) ? outputTask.Result : "";
            }
            catch
            {
                return "";
            }
        }

        private static int GetMediaProcessTimeoutMs(params string?[] paths)
        {
            long totalBytes = 0;
            foreach (var path in paths)
            {
                try
                {
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        totalBytes += new FileInfo(path).Length;
                }
                catch { }
            }

            long totalMb = Math.Max(1, totalBytes / (1024L * 1024L));
            long timeout = 60_000L + totalMb * 500L;
            return (int)Math.Clamp(timeout, 60_000L, 600_000L);
        }

        private void WriteAudioDiagnostic(string message, string? explicitLogPath = null)
        {
#if DEBUG
            if (explicitLogPath == null && message.StartsWith("Finalize ", StringComparison.Ordinal))
                return;
            if (explicitLogPath == null
                && (message.StartsWith("MP4 ", StringComparison.Ordinal) || message.StartsWith("WAV ", StringComparison.Ordinal)))
                return;

            Debug.WriteLine($"[AudioDiagnostic] {message}");

            string? logPath = explicitLogPath ?? _currentAudioLogPath;
            if (string.IsNullOrWhiteSpace(logPath)) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { }
#endif
        }

        private void ClearCurrentAudioLogPath(string? audioLogPath)
        {
            if (string.Equals(_currentAudioLogPath, audioLogPath, StringComparison.OrdinalIgnoreCase))
                _currentAudioLogPath = null;
        }

        private static string FindFFmpeg()
        {
            return AppPaths.FindFFmpeg();
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
