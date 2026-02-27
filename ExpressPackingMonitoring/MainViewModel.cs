#nullable disable
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using AForge.Video;
using AForge.Video.DirectShow;
using System.Drawing;
using System.Drawing.Imaging;

namespace ExpressPackingMonitoring.ViewModels
{
    public record LogItem(DateTime Time, string OrderId, string Status);

    public class AppConfig
    {
        public int CameraIndex { get; set; } = 0;
        public int FrameWidth { get; set; } = 1280;
        public int FrameHeight { get; set; } = 720;
        public int Fps { get; set; } = 15;

        public double ZoomScale { get; set; } = 1.5;

        public int ZoomDelayMs { get; set; } = 1000;
        public int ZoomDurationMs { get; set; } = 3000;
        public int MaxDiskSpaceGB { get; set; } = 100;
        public int NoMotionStopSeconds { get; set; } = 300;
        public double MotionDetectThreshold { get; set; } = 30.0;
        public string VideoStoragePath { get; set; } = "Videos";

        // 【新增】：单号正则表达式（默认：允许字母和数字，长度 6 到 30 位）
        public string OrderIdRegex { get; set; } = "^[a-zA-Z0-9]{6,30}$";
    }

    public class MainViewModel : ObservableObject, IDisposable
    {
        private AppConfig _config;
        private readonly string _configFilePath = "config.json";

        private VideoCaptureDevice _videoSource;
        private Mat _latestFrame;
        private readonly object _frameLock = new object();
        private bool _isFirstFrameReceived = false;

        private VideoWriter _writer;
        private Mat _previousCheckFrame = new Mat();
        private WriteableBitmap _videoFrame;
        private CancellationTokenSource _cts;
        private Task _videoTask;
        private object _videoLock = new object();

        private string _currentMode = "发货";
        private string _currentOrderId = "待命";
        private bool _isRecording;
        private double _diskUsagePercent;
        private long _diskTotalBytes;
        private bool _isScanning = false;
        private DateTime _lastScanTime;
        private DateTime _lastMotionTime;

        private DateTime _zoomStartTime;
        private bool _isZooming = false;
        private bool _delayBeforeZooming = false;

        public WriteableBitmap VideoFrame { get => _videoFrame; set => SetProperty(ref _videoFrame, value); }
        public string CurrentMode { get => _currentMode; set => SetProperty(ref _currentMode, value); }
        public string CurrentOrderId { get => _currentOrderId; set => SetProperty(ref _currentOrderId, value); }
        public bool IsRecording { get => _isRecording; set => SetProperty(ref _isRecording, value); }
        public double DiskUsagePercent { get => _diskUsagePercent; set => SetProperty(ref _diskUsagePercent, value); }
        public ObservableCollection<LogItem> HistoryLogs { get; } = new ObservableCollection<LogItem>();
        public AppConfig Config { get => _config; set => SetProperty(ref _config, value); }

        public ICommand ScanCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand StartRecordingCommand { get; }
        public ICommand StopRecordingCommand { get; }
        public ICommand OpenPlaybackCommand { get; }

        public MainViewModel()
        {
            LoadConfig();
            ScanCommand = new RelayCommand<string>(HandleScan);
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            StartRecordingCommand = new RelayCommand(StartRecordingManual);
            StopRecordingCommand = new RelayCommand(StopRecordingManual);
            OpenPlaybackCommand = new RelayCommand(OpenPlaybackWindow);
            InitializeSystem();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    string json = File.ReadAllText(_configFilePath);
                    Config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                else { Config = new AppConfig(); SaveConfig(); }
            }
            catch { Config = new AppConfig(); }
        }

        private void SaveConfig()
        {
            try
            {
                string json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configFilePath, json);
            }
            catch { }
        }

        public void OpenSettings()
        {
            try
            {
                string json = JsonSerializer.Serialize(Config);
                var clonedConfig = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                var settingsWin = new SettingsWindow(clonedConfig);

                if (Application.Current != null && Application.Current.MainWindow != null)
                {
                    settingsWin.Owner = Application.Current.MainWindow;
                }

                if (settingsWin.ShowDialog() == true)
                {
                    Config = clonedConfig;
                    SaveConfig();
                    AddLog("配置已更新，正在重启摄像头...");
                    RestartCamera();
                }
            }
            catch (Exception ex) { MessageBox.Show("打开设置失败: \n" + ex.Message, "系统提示", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void StartRecordingManual()
        {
            if (IsRecording) return;
            if (string.IsNullOrWhiteSpace(CurrentOrderId) || CurrentOrderId == "待命")
                CurrentOrderId = $"手动录像_{DateTime.Now:MMdd_HHmmss}";
            StartRecordingProcess();
        }

        private void StopRecordingManual()
        {
            if (!IsRecording) return;
            StopRecording();
            AddLog("【操作】手动停止了录制");
            CurrentOrderId = "待命";
        }

        private void OpenPlaybackWindow()
        {
            string folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
            if (Path.IsPathRooted(Config.VideoStoragePath)) folderPath = Config.VideoStoragePath;
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

            var playbackWin = new PlaybackWindow(folderPath);
            if (Application.Current != null && Application.Current.MainWindow != null)
                playbackWin.Owner = Application.Current.MainWindow;
            playbackWin.ShowDialog();
        }

        private void InitializeSystem()
        {
            _cts = new CancellationTokenSource();
            StartCamera();
            _videoTask = Task.Run(() => VideoProcessLoop(_cts.Token), _cts.Token);
            Task.Run(CheckDiskAndCleanup);
        }

        private void RestartCamera()
        {
            StopRecording(); StopCamera(); StartCamera();
        }

        private void StartCamera()
        {
            try
            {
                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (videoDevices.Count == 0) { Application.Current.Dispatcher.Invoke(() => AddLog("【错误】系统未检测到任何摄像头！")); return; }
                if (Config.CameraIndex >= videoDevices.Count) Config.CameraIndex = 0;

                _videoSource = new VideoCaptureDevice(videoDevices[Config.CameraIndex].MonikerString);

                if (_videoSource.VideoCapabilities.Length > 0)
                    _videoSource.VideoResolution = _videoSource.VideoCapabilities[0];

                _videoSource.NewFrame += VideoSource_NewFrame;
                _videoSource.Start();

                Application.Current.Dispatcher.Invoke(() => AddLog($"✅ 成功接入：{videoDevices[Config.CameraIndex].Name}"));
            }
            catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => AddLog($"摄像头启动失败: {ex.Message}")); }
        }

        private void StopCamera()
        {
            _isFirstFrameReceived = false;
            if (_videoSource != null)
            {
                if (_videoSource.IsRunning) { _videoSource.SignalToStop(); _videoSource.WaitForStop(); }
                _videoSource.NewFrame -= VideoSource_NewFrame;
                _videoSource = null;
            }
            lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = null; }
        }

        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            try
            {
                if (!_isFirstFrameReceived)
                {
                    _isFirstFrameReceived = true;
                    Application.Current.Dispatcher.Invoke(() => AddLog("【调试】📺 OBS 通道已打通，成功收到第一帧画面！"));
                }
                Mat newMat = BitmapToMat(eventArgs.Frame);
                lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = newMat; }
            }
            catch { }
        }

        private Mat BitmapToMat(Bitmap bitmap)
        {
            Bitmap solidBitmap = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
            using (Graphics gr = Graphics.FromImage(solidBitmap))
                gr.DrawImage(bitmap, new System.Drawing.Rectangle(0, 0, solidBitmap.Width, solidBitmap.Height));

            var bmpData = solidBitmap.LockBits(new System.Drawing.Rectangle(0, 0, solidBitmap.Width, solidBitmap.Height), ImageLockMode.ReadOnly, solidBitmap.PixelFormat);
            Mat mat = Mat.FromPixelData(solidBitmap.Height, solidBitmap.Width, MatType.CV_8UC3, bmpData.Scan0, bmpData.Stride).Clone();
            solidBitmap.UnlockBits(bmpData);
            solidBitmap.Dispose();
            return mat;
        }

        private async Task VideoProcessLoop(CancellationToken token)
        {
            double frameDurationMs = 1000.0 / (Config.Fps > 0 ? Config.Fps : 15);

            while (!token.IsCancellationRequested)
            {
                DateTime startTime = DateTime.Now;
                Mat currentFrame = null;

                lock (_frameLock)
                {
                    if (_latestFrame != null && !_latestFrame.IsDisposed) currentFrame = _latestFrame.Clone();
                }

                if (currentFrame != null && !currentFrame.Empty())
                {
                    if (Config.FrameWidth != currentFrame.Width || Config.FrameHeight != currentFrame.Height)
                    {
                        Config.FrameWidth = currentFrame.Width;
                        Config.FrameHeight = currentFrame.Height;
                    }

                    Mat processedFrame = currentFrame;

                    // 【核心修改】：智能中心比例缩放
                    if (_isScanning)
                    {
                        TimeSpan scanElapsed = DateTime.Now - _lastScanTime;
                        if (_delayBeforeZooming && scanElapsed.TotalMilliseconds >= Config.ZoomDelayMs)
                        {
                            _delayBeforeZooming = false; _isZooming = true; _zoomStartTime = DateTime.Now;
                        }

                        if (_isZooming)
                        {
                            // 计算需要裁剪的中心区域大小
                            int zoomW = (int)(currentFrame.Width / Config.ZoomScale);
                            int zoomH = (int)(currentFrame.Height / Config.ZoomScale);

                            // 防呆：如果设置了瞎眼的倍率，保证程序不崩
                            if (zoomW <= 0 || zoomW > currentFrame.Width) zoomW = currentFrame.Width;
                            if (zoomH <= 0 || zoomH > currentFrame.Height) zoomH = currentFrame.Height;

                            // 永远居中
                            int zoomX = (currentFrame.Width - zoomW) / 2;
                            int zoomY = (currentFrame.Height - zoomH) / 2;

                            OpenCvSharp.Rect zoomRect = new OpenCvSharp.Rect(zoomX, zoomY, zoomW, zoomH);
                            zoomRect = zoomRect.Intersect(new OpenCvSharp.Rect(0, 0, currentFrame.Width, currentFrame.Height));

                            if (zoomRect.Width > 0 && zoomRect.Height > 0)
                            {
                                processedFrame = currentFrame.Clone(zoomRect);
                                // 裁完之后拉伸回原尺寸填充屏幕
                                Cv2.Resize(processedFrame, processedFrame, new OpenCvSharp.Size(Config.FrameWidth, Config.FrameHeight));
                            }

                            if ((DateTime.Now - _zoomStartTime).TotalMilliseconds >= Config.ZoomDurationMs)
                            {
                                _isZooming = false; _isScanning = false; processedFrame = currentFrame;
                            }
                        }
                    }

                    lock (_videoLock) { if (IsRecording && _writer != null) _writer.Write(processedFrame); }
                    Application.Current.Dispatcher.Invoke(() => { VideoFrame = processedFrame.ToWriteableBitmap(); });
                    if ((DateTime.Now - startTime).TotalMilliseconds > 500) PerformMotionDetection(currentFrame);

                    if (processedFrame != currentFrame) processedFrame.Dispose();
                    currentFrame.Dispose();
                }

                if (IsRecording && (DateTime.Now - _lastMotionTime).TotalSeconds >= Config.NoMotionStopSeconds)
                {
                    Application.Current.Dispatcher.Invoke(StopRecordingAuto);
                }

                double elapsedMs = (DateTime.Now - startTime).TotalMilliseconds;
                int sleepMs = (int)Math.Max(0, frameDurationMs - elapsedMs);
                if (sleepMs > 0) await Task.Delay(sleepMs, token);
            }
        }

        private void PerformMotionDetection(Mat currentFrame)
        {
            if (_previousCheckFrame.Empty()) { currentFrame.CopyTo(_previousCheckFrame); _lastMotionTime = DateTime.Now; return; }
            using var currentSmall = currentFrame.Resize(new OpenCvSharp.Size(320, 240));
            using var currentGray = currentSmall.CvtColor(ColorConversionCodes.BGR2GRAY);
            using var prevSmall = _previousCheckFrame.Resize(new OpenCvSharp.Size(320, 240));
            using var prevGray = prevSmall.CvtColor(ColorConversionCodes.BGR2GRAY);
            using var diff = new Mat();
            using var thresholded = new Mat();

            Cv2.Absdiff(currentGray, prevGray, diff);
            Cv2.Threshold(diff, thresholded, Config.MotionDetectThreshold, 255, ThresholdTypes.Binary);

            if (((double)Cv2.CountNonZero(thresholded) / (thresholded.Width * thresholded.Height)) > 0.01) _lastMotionTime = DateTime.Now;
            currentFrame.CopyTo(_previousCheckFrame);
        }

        private void HandleScan(string scanResult)
        {
            if (string.IsNullOrWhiteSpace(scanResult)) return;
            string upperResult = scanResult.ToUpper().Trim();

            // === 1. 指令拦截层 ===
            if (upperResult == "MODE_FAHUO" || upperResult == "发货") { CurrentMode = "发货"; AddLog("【指令】切换为发货模式"); return; }
            if (upperResult == "MODE_TUIHUO" || upperResult == "退货") { CurrentMode = "退货"; AddLog("【指令】切换为退货模式"); return; }
            if (upperResult == "CMD_START" || upperResult == "开始录制") { StartRecordingManual(); return; }
            if (upperResult == "CMD_STOP" || upperResult == "停止录制") { StopRecordingManual(); return; }

            // === 2. 格式校验层 (正则防呆) ===
            try
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(upperResult, Config.OrderIdRegex))
                {
                    AddLog($"【警告】误扫非快递码或格式错误，已拦截: {upperResult}");
                    return; // 格式不对，直接不往下走，不触发录像
                }
            }
            catch { /* 如果用户把正则表达式写错了，就不拦截，直接放行 */ }

            // === 3. 业务执行层 ===
            CurrentOrderId = upperResult;
            StartRecordingProcess();
        }

        private void StartRecordingProcess()
        {
            AddLog($"捕获单号: {CurrentOrderId}，准备录制...");
            if (IsRecording) StopRecording();
            IsRecording = true; _lastMotionTime = DateTime.Now; _lastScanTime = DateTime.Now;
            _isScanning = true; _delayBeforeZooming = true; _isZooming = false;

            string folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
            if (Path.IsPathRooted(Config.VideoStoragePath)) folderPath = Config.VideoStoragePath;

            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
            string filePath = Path.Combine(folderPath, $"{CurrentMode}_{CurrentOrderId}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

            lock (_videoLock) { _writer = new VideoWriter(filePath, FourCC.MP4V, Config.Fps, new OpenCvSharp.Size(Config.FrameWidth, Config.FrameHeight)); }
            AddLog($"开始录制...");
        }

        private void StopRecording()
        {
            IsRecording = false; _isScanning = false; _delayBeforeZooming = false; _isZooming = false; _lastMotionTime = DateTime.Now;
            lock (_videoLock) { if (_writer != null) { _writer.Release(); _writer.Dispose(); _writer = null; AddLog("录制停止。"); } }
        }

        private void StopRecordingAuto() { StopRecording(); AddLog($"连续未检测到运动，自动停录。"); }

        private async Task CheckDiskAndCleanup()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                string folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
                if (Path.IsPathRooted(Config.VideoStoragePath)) folderPath = Config.VideoStoragePath;

                if (!Directory.Exists(folderPath)) { await Task.Delay(300000); continue; }

                var dirInfo = new DirectoryInfo(folderPath);
                long currentSizeBytes = dirInfo.GetFiles("*.mp4").Sum(fi => fi.Length);
                try { _diskTotalBytes = new DriveInfo(Path.GetPathRoot(folderPath)).TotalSize; }
                catch { _diskTotalBytes = Config.MaxDiskSpaceGB * 1024L * 1024 * 1024; }
                DiskUsagePercent = (double)currentSizeBytes / _diskTotalBytes * 100.0;

                long maxSizeBytes = Config.MaxDiskSpaceGB * 1024L * 1024 * 1024;
                if (currentSizeBytes > maxSizeBytes)
                {
                    var oldestFiles = dirInfo.GetFiles("*.mp4").OrderBy(fi => fi.LastWriteTime).ToList();
                    long bytesToDelete = currentSizeBytes - (maxSizeBytes * 9 / 10);
                    long deletedBytes = 0;
                    foreach (var file in oldestFiles)
                    {
                        try { long len = file.Length; file.Delete(); deletedBytes += len; if (deletedBytes >= bytesToDelete) break; } catch { }
                    }
                }
                await Task.Delay(300000, _cts.Token);
            }
        }

        private void AddLog(string status)
        {
            HistoryLogs.Insert(0, new LogItem(DateTime.Now, CurrentOrderId, status));
            if (HistoryLogs.Count > 100) HistoryLogs.RemoveAt(HistoryLogs.Count - 1);
        }

        public void Dispose()
        {
            StopRecording(); _cts?.Cancel(); StopCamera();
            try { _videoTask?.Wait(); } catch { }
            _cts?.Dispose();
            lock (_videoLock) { _writer?.Dispose(); _previousCheckFrame?.Dispose(); }
        }
    }
}