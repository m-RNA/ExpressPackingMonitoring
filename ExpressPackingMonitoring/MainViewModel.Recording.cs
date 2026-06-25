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
                        ConvertMkvToMp4(filePath, audioFilePath);

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

                    long usedBytesInPath = 0;
                    var dirInfo = new DirectoryInfo(normalizedPath);
                    foreach (var file in dirInfo.EnumerateFiles("*.mkv", SearchOption.AllDirectories))
                    {
                        usedBytesInPath += file.Length;
                    }

                    double usedGB = usedBytesInPath / 1073741824.0;

                    string? root = Path.GetPathRoot(Path.GetFullPath(normalizedPath));
                    if (string.IsNullOrEmpty(root)) continue;

                    var drive = new DriveInfo(root);
                    if (!drive.IsReady) continue;

                    // 5% 预留或 2GB
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

                if (Config.EnableAudioRecording && HasConfiguredAudioDevice())
                {
                    bool micFound = await Task.Run(() => {
                        var audioDevices = new FilterInfoCollection(new Guid("33D9A762-90C8-11D0-BD43-00A0C911CE86"));
                        for (int i = 0; i < audioDevices.Count; i++)
                            if (IsConfiguredAudioDevice(audioDevices[i].Name, audioDevices[i].MonikerString)) return true;
                        return false;
                    });
                    if (!micFound)
                    {
                        ShowToast("⚠ 预设麦克风已断开");
                        SpeakWarning("麦克风已断开");
                        // 如果用户开了音频录制但麦克风丢了，建议停止或报错。
                        // 此处选择提示后继续，但 FFmpeg 启动会失败，报错提示更详细。
                        // 或者可以强制关闭本段录制的音频：
                        // withAudioOverride = false; 
                    }
                }

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

                if (Config.EnableAudioRecording && HasConfiguredAudioDevice())
                {
                    if (!StartAudioRecording(audioFilePath))
                    {
                        ShowToast("⚠ 麦克风录音启动失败");
                        SpeakWarning("麦克风录音启动失败");
                        return;
                    }
                }

                // 3. 开启新的生产者-消费者通道
                lock (_videoLock)
                {
                    _videoWriteQueue = new BlockingCollection<Mat>(300); // 增大缓冲区
                    _writeCts = new CancellationTokenSource();
                }

                // 4. 启动录制任务
                _writeTask = Task.Run(() => BackgroundFFmpegRecordingLoop(filePath, ffmpegPath, _writeCts.Token));

                // 5. 等待较长时间给 FFmpeg 初始化，特别是 NVENC。
                // 如果闪退，Task 会立刻完成
                await Task.Delay(1000); 
                if (_writeTask.IsCompleted) 
                {
                    DeleteAudioTempFile(StopAudioRecording());
                    Debug.WriteLine("[MainVM] 启动检测：_writeTask 已结束，FFmpeg 启动失败");
                    // 注意：此处不应手动 IsRecording = false，BackgroundFFmpegRecordingLoop 内部会处理 UI 状态重置。
                    return; 
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
                DeleteAudioTempFile(StopAudioRecording());
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
                    SpeakWarning("录制失败");
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
            Process? ffmpeg = null;
            Stream? stdin = null;
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
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(audioFilePath)!);

                var capture = CreateWasapiCapture(device);
                var writer = new WaveFileWriter(audioFilePath, capture.WaveFormat);

                lock (_audioLock)
                {
                    _audioCapture = capture;
                    _audioWriter = writer;
                    _currentAudioFilePath = audioFilePath;
                    _audioStopRequested = false;
                    _audioRestarting = false;
                    _lastAudioDataAt = DateTime.Now;
                    _audioBytesWritten = 0;
                    _audioMonitorCts = new CancellationTokenSource();
                }

                capture.StartRecording();
                _audioMonitorTask = Task.Run(() => AudioCaptureMonitorLoop(_audioMonitorCts.Token));
                Debug.WriteLine($"[Audio] 开始录音: {device.FriendlyName}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] 启动失败: {ex.Message}");
                StopAudioRecording();
                DeleteAudioTempFile(audioFilePath);
                return false;
            }
        }

        private string? StopAudioRecording()
        {
            WasapiCapture? capture;
            WaveFileWriter? writer;
            string? audioFilePath;
            CancellationTokenSource? monitorCts;
            Task? monitorTask;

            lock (_audioLock)
            {
                _audioStopRequested = true;
                capture = _audioCapture;
                writer = _audioWriter;
                audioFilePath = _currentAudioFilePath;
                monitorCts = _audioMonitorCts;
                monitorTask = _audioMonitorTask;
                _audioCapture = null;
                _audioWriter = null;
                _currentAudioFilePath = null;
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
                try { writer?.Dispose(); } catch { }
            }

            if (string.IsNullOrEmpty(audioFilePath)) return null;

            try
            {
                if (File.Exists(audioFilePath) && new FileInfo(audioFilePath).Length > 44)
                    return audioFilePath;
            }
            catch { }

            DeleteAudioTempFile(audioFilePath);
            return null;
        }

        private WasapiCapture CreateWasapiCapture(MMDevice device)
        {
            var capture = new WasapiCapture(device)
            {
                ShareMode = AudioClientShareMode.Shared
            };

            capture.DataAvailable += (_, e) =>
            {
                lock (_audioLock)
                {
                    if (_audioWriter == null || e.BytesRecorded <= 0) return;
                    PadAudioGapIfNeeded(DateTime.Now);
                    _audioWriter.Write(e.Buffer, 0, e.BytesRecorded);
                    _audioWriter.Flush();
                    _audioBytesWritten += e.BytesRecorded;
                    _lastAudioDataAt = DateTime.Now;
                }
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null)
                    Debug.WriteLine($"[Audio] 录音停止异常: {e.Exception.Message}");

                if (ShouldRestartAudioCapture())
                    _ = Task.Run(() => RestartAudioCapture("stopped"));
            };

            return capture;
        }

        private void PadAudioGapIfNeeded(DateTime now)
        {
            if (_audioWriter == null || _lastAudioDataAt == DateTime.MinValue) return;

            double gapMs = (now - _lastAudioDataAt).TotalMilliseconds;
            if (gapMs <= 750) return;

            int bytesPerSecond = _audioWriter.WaveFormat.AverageBytesPerSecond;
            int blockAlign = Math.Max(1, _audioWriter.WaveFormat.BlockAlign);
            int silenceBytes = (int)(bytesPerSecond * (gapMs / 1000.0));
            silenceBytes -= silenceBytes % blockAlign;
            if (silenceBytes <= 0) return;

            byte[] silence = new byte[Math.Min(silenceBytes, bytesPerSecond)];
            int remaining = silenceBytes;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, silence.Length);
                _audioWriter.Write(silence, 0, chunk);
                remaining -= chunk;
            }
            _audioBytesWritten += silenceBytes;
            Debug.WriteLine($"[Audio] 补齐录音间隙: {gapMs:F0}ms");
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
                    lock (_audioLock)
                    {
                        shouldMonitor = !_audioStopRequested && _audioWriter != null && _audioCapture != null;
                        lastDataAt = _lastAudioDataAt;
                    }

                    if (shouldMonitor && (DateTime.Now - lastDataAt).TotalSeconds > 5)
                        RestartAudioCapture("no-data");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Audio] 监控异常: {ex.Message}");
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
                    _audioCapture = capture;
                    _lastAudioDataAt = DateTime.Now;
                }

                capture.StartRecording();
                Debug.WriteLine($"[Audio] 已重启录音({reason}): {device.FriendlyName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] 重启异常({reason}): {ex.Message}");
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

            if (!string.IsNullOrWhiteSpace(Config.AudioDeviceMoniker))
            {
                foreach (var device in devices)
                {
                    if (AudioEndpointMatches(device.ID, Config.AudioDeviceMoniker))
                        return device;
                }
            }

            if (!string.IsNullOrWhiteSpace(Config.AudioDeviceName))
            {
                foreach (var device in devices)
                {
                    if (AudioEndpointMatches(device.FriendlyName, Config.AudioDeviceName)
                        || AudioEndpointMatches(GetEndpointDisplayName(device), Config.AudioDeviceName))
                        return device;
                }
            }

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
        private void ConvertMkvToMp4(string mkvPath, string? audioPath = null)
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

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = BuildMkvToMp4Args(mkvPath, audioPath, mp4Path),
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

                process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0)
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
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MkvToMp4] 异常: {ex.Message}");
            }
        }

        private string BuildMkvToMp4Args(string mkvPath, string? audioPath, string mp4Path)
        {
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
                return $"-y -i \"{mkvPath}\" -vcodec copy -acodec copy \"{mp4Path}\"";

            int offsetMs = Math.Clamp(Config.AudioSyncOffsetMs, -5000, 5000);
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

        private static string FindFFmpeg()
        {
            string appDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            return File.Exists(appDir) ? appDir : null!;
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
