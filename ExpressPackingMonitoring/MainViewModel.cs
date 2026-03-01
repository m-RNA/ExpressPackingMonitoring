#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
    // 【新增】：用于本地持久化保存的统计模型
    public class DailyStatItem
    {
        public string Date { get; set; }
        public int TotalPieces { get; set; }
        public double TotalDurationSec { get; set; }
    }

    public partial class ScanRecord : ObservableObject
    {
        [ObservableProperty] private string _orderId;
        [ObservableProperty] private string _duration;
        [ObservableProperty] private string _dateStr;
        [ObservableProperty] private string _mode;

        // 【需求2】：新增活跃状态，用于前端变色
        [ObservableProperty] private bool _isActive;

        public ScanRecord(string orderId, string duration, string dateStr, string mode, bool isActive = false)
        { OrderId = orderId; Duration = duration; DateStr = dateStr; Mode = mode; IsActive = isActive; }
    }

    public class AppConfig
    {
        public int CameraIndex { get; set; } = 0;
        public int FrameWidth { get; set; } = 1280;
        public int FrameHeight { get; set; } = 720;
        public int Fps { get; set; } = 15;
        public bool EnableSmartZoom { get; set; } = false;
        public double ZoomScale { get; set; } = 1.5;
        public double ZoomDelaySeconds { get; set; } = 1.0;
        public double ZoomDurationSeconds { get; set; } = 3.0;
        public double MaxDiskSpaceGB { get; set; } = 100.0;
        public bool EnableAutoStop { get; set; } = false;
        public double AutoStopMinutes { get; set; } = 5.0;
        public bool EnableMaxDuration { get; set; } = false;

        // 【需求1】：最大时长改为分钟
        public double MaxDurationMinutes { get; set; } = 1.0;

        public double MotionDetectThreshold { get; set; } = 15.0;
        public string VideoStoragePath { get; set; } = "打包监控视频";
        public string OrderIdRegex { get; set; } = "^[a-zA-Z0-9]{6,30}$";
    }

    public class MainViewModel : ObservableObject, IDisposable
    {
        private AppConfig _config;
        private readonly string _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private readonly string _statsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "daily_stats.json");

        private VideoCaptureDevice _videoSource;
        private Mat _latestFrame;
        private readonly object _frameLock = new object();

        private VideoWriter _writer;
        private BlockingCollection<Mat> _videoWriteQueue;
        private Task _writeTask;
        private CancellationTokenSource _writeCts;

        private Mat _previousCheckFrame = new Mat();
        private WriteableBitmap _videoFrame;
        private CancellationTokenSource _cts;
        private Task _videoTask;
        private object _videoLock = new object();

        private string _currentMode = "发货";
        private string _currentOrderId = "";
        private bool _isRecording;
        private string _scanInputText = "";
        public string ScanInputText { get => _scanInputText; set => SetProperty(ref _scanInputText, value); }

        private double _diskUsagePercent;
        private string _diskUsageText = "0.0 / 0.0 GB";
        private bool _isScanning = false;
        private DateTime _lastScanTime;
        private DateTime _lastMotionTime;
        private DateTime _recordStartTime;
        private DateTime _zoomStartTime;
        private bool _isZooming = false;
        private bool _delayBeforeZooming = false;

        private ScanRecord _currentScanRecord;

        private int _totalPieces;
        private TimeSpan _totalPackTime;
        public int TotalPieces { get => _totalPieces; set { SetProperty(ref _totalPieces, value); OnPropertyChanged(nameof(AveragePackTime)); } }
        public string TotalPackTimeStr => $"{(int)_totalPackTime.TotalHours:D2}:{_totalPackTime.Minutes:D2}:{_totalPackTime.Seconds:D2}";
        public string AveragePackTime => TotalPieces == 0 ? "00:00" : TimeSpan.FromSeconds(_totalPackTime.TotalSeconds / TotalPieces).ToString(@"mm\:ss");

        private string _toastMessage;
        private bool _isToastVisible;
        private CancellationTokenSource _toastCts;
        public string ToastMessage { get => _toastMessage; set => SetProperty(ref _toastMessage, value); }
        public bool IsToastVisible { get => _isToastVisible; set => SetProperty(ref _isToastVisible, value); }

        private string _logSearchText = "";
        public string LogSearchText { get => _logSearchText; set { SetProperty(ref _logSearchText, value); FilterLogs(); } }
        private ObservableCollection<ScanRecord> _allLogs = new ObservableCollection<ScanRecord>();
        public ObservableCollection<ScanRecord> FilteredLogs { get; } = new ObservableCollection<ScanRecord>();

        public WriteableBitmap VideoFrame { get => _videoFrame; set => SetProperty(ref _videoFrame, value); }
        public string CurrentMode { get => _currentMode; set => SetProperty(ref _currentMode, value); }
        public string CurrentOrderId { get => _currentOrderId; set => SetProperty(ref _currentOrderId, value); }
        public bool IsRecording { get => _isRecording; set => SetProperty(ref _isRecording, value); }
        public double DiskUsagePercent { get => _diskUsagePercent; set => SetProperty(ref _diskUsagePercent, value); }
        public string DiskUsageText { get => _diskUsageText; set => SetProperty(ref _diskUsageText, value); }
        public AppConfig Config { get => _config; set => SetProperty(ref _config, value); }

        public ICommand ScanCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand OpenPlaybackCommand { get; }
        public ICommand ToggleModeCommand { get; }
        public ICommand ToggleRecordingCommand { get; }
        public ICommand OpenStatsCommand { get; } // 【新增】：打开统计面板

        public MainViewModel()
        {
            LoadConfig();
            LoadTodayStats(); // 【新增】：启动时加载今日统计数据
            ScanCommand = new RelayCommand<string>(HandleScan);
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            OpenPlaybackCommand = new RelayCommand(OpenPlaybackWindow);
            ToggleModeCommand = new RelayCommand(ToggleMode);
            ToggleRecordingCommand = new RelayCommand(ToggleRecording);
            OpenStatsCommand = new RelayCommand(OpenStatsWindow);
            InitializeSystem();
        }

        private void LoadTodayStats()
        {
            try
            {
                if (File.Exists(_statsFilePath))
                {
                    var stats = JsonSerializer.Deserialize<List<DailyStatItem>>(File.ReadAllText(_statsFilePath));
                    var today = stats?.FirstOrDefault(s => s.Date == DateTime.Now.ToString("yyyy-MM-dd"));
                    if (today != null)
                    {
                        TotalPieces = today.TotalPieces;
                        _totalPackTime = TimeSpan.FromSeconds(today.TotalDurationSec);
                        OnPropertyChanged(nameof(TotalPackTimeStr)); OnPropertyChanged(nameof(AveragePackTime));
                    }
                }
            }
            catch { }
        }

        private void SaveStatToDatabase(double durationSec)
        {
            try
            {
                List<DailyStatItem> stats = new List<DailyStatItem>();
                if (File.Exists(_statsFilePath)) stats = JsonSerializer.Deserialize<List<DailyStatItem>>(File.ReadAllText(_statsFilePath)) ?? new List<DailyStatItem>();

                string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
                var today = stats.FirstOrDefault(s => s.Date == dateStr);
                if (today == null) { today = new DailyStatItem { Date = dateStr }; stats.Add(today); }

                today.TotalPieces++;
                today.TotalDurationSec += durationSec;

                File.WriteAllText(_statsFilePath, JsonSerializer.Serialize(stats));
            }
            catch { }
        }

        private void ToggleMode() { CurrentMode = CurrentMode == "发货" ? "退货" : "发货"; ShowToast($"已切换为: {CurrentMode}"); }
        private void ToggleRecording() { if (IsRecording) StopRecordingManual(); else StartRecordingManual(); }

        public void ShowToast(string message) { Application.Current.Dispatcher.InvokeAsync(async () => { _toastCts?.Cancel(); _toastCts = new CancellationTokenSource(); var token = _toastCts.Token; ToastMessage = message; IsToastVisible = true; try { await Task.Delay(2500, token); } catch (OperationCanceledException) { return; } IsToastVisible = false; }); }

        private void FilterLogs() { FilteredLogs.Clear(); var keyword = LogSearchText?.ToUpper() ?? ""; foreach (var log in _allLogs) { if (string.IsNullOrEmpty(keyword) || log.OrderId.ToUpper().Contains(keyword)) FilteredLogs.Add(log); } }
        private void AddRecord(ScanRecord record) { Application.Current.Dispatcher.InvokeAsync(() => { _allLogs.Insert(0, record); if (string.IsNullOrEmpty(LogSearchText)) FilteredLogs.Insert(0, record); if (_allLogs.Count > 200) _allLogs.RemoveAt(_allLogs.Count - 1); }); }

        private void LoadConfig() { try { if (File.Exists(_configFilePath)) Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configFilePath)) ?? new AppConfig(); else { Config = new AppConfig(); SaveConfig(); } } catch { Config = new AppConfig(); } }
        private void SaveConfig() { try { File.WriteAllText(_configFilePath, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true })); } catch { } }

        public void OpenSettings()
        {
            try
            {
                var clonedConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
                var settingsWin = new SettingsWindow(clonedConfig, DiskUsagePercent, DiskUsageText);
                if (Application.Current?.MainWindow != null) settingsWin.Owner = Application.Current.MainWindow;
                if (settingsWin.ShowDialog() == true) { Config = clonedConfig; SaveConfig(); ForceCheckDiskAndCleanup(); ShowToast("⚙️ 配置已保存，重启相机"); RestartCamera(); }
            }
            catch (Exception ex) { ShowToast($"设置错误: {ex.Message}"); }
        }

        private void OpenStatsWindow()
        {
            var statsWin = new StatisticsWindow(_statsFilePath);
            if (Application.Current?.MainWindow != null) statsWin.Owner = Application.Current.MainWindow;
            statsWin.ShowDialog();
        }

        private void StartRecordingManual()
        {
            if (IsRecording) return;
            string input = ScanInputText?.Trim().ToUpper() ?? "";
            if (string.IsNullOrWhiteSpace(input) || input.Contains("CMD_") || input.Contains("MODE_") || input.Contains("发货") || input.Contains("退货") || input.Contains("录制"))
                CurrentOrderId = $"MAN_{DateTime.Now:HHmmss}";
            else CurrentOrderId = input;
            StartRecordingProcess();
        }

        private void StopRecordingManual() { if (!IsRecording) return; StopRecording(); CurrentOrderId = ""; ScanInputText = ""; ShowToast("已手动停止录制"); }

        private void OpenPlaybackWindow()
        {
            string folderPath = Path.IsPathRooted(Config.VideoStoragePath) ? Config.VideoStoragePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
            var playbackWin = new PlaybackWindow(folderPath);
            if (Application.Current?.MainWindow != null) playbackWin.Owner = Application.Current.MainWindow;
            playbackWin.ShowDialog();
        }

        private void InitializeSystem() { _cts = new CancellationTokenSource(); StartCamera(); _videoTask = Task.Run(() => VideoProcessLoop(_cts.Token), _cts.Token); Task.Run(CheckDiskAndCleanup); }
        private void RestartCamera() { StopRecording(); StopCamera(); StartCamera(); }

        private void StartCamera()
        {
            try
            {
                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (videoDevices.Count == 0) { ShowToast("未检测到摄像头"); return; }
                if (Config.CameraIndex >= videoDevices.Count) Config.CameraIndex = 0;
                _videoSource = new VideoCaptureDevice(videoDevices[Config.CameraIndex].MonikerString);
                if (_videoSource.VideoCapabilities.Length > 0) _videoSource.VideoResolution = _videoSource.VideoCapabilities[0];
                _videoSource.NewFrame += VideoSource_NewFrame; _videoSource.Start();
            }
            catch { ShowToast("摄像头启动失败"); }
        }

        private void StopCamera() { if (_videoSource != null) { if (_videoSource.IsRunning) { _videoSource.SignalToStop(); _videoSource.WaitForStop(); } _videoSource.NewFrame -= VideoSource_NewFrame; _videoSource = null; } lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = null; } }
        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs) { try { Mat newMat = BitmapToMat(eventArgs.Frame); lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = newMat; } } catch { } }

        private Mat BitmapToMat(Bitmap bitmap)
        {
            Bitmap solidBitmap = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
            using (Graphics gr = Graphics.FromImage(solidBitmap)) gr.DrawImage(bitmap, new System.Drawing.Rectangle(0, 0, solidBitmap.Width, solidBitmap.Height));
            var bmpData = solidBitmap.LockBits(new System.Drawing.Rectangle(0, 0, solidBitmap.Width, solidBitmap.Height), ImageLockMode.ReadOnly, solidBitmap.PixelFormat);
            Mat mat = Mat.FromPixelData(solidBitmap.Height, solidBitmap.Width, MatType.CV_8UC3, bmpData.Scan0, bmpData.Stride).Clone();
            solidBitmap.UnlockBits(bmpData); solidBitmap.Dispose(); return mat;
        }

        private async Task VideoProcessLoop(CancellationToken token)
        {
            double frameDurationMs = 1000.0 / (Config.Fps > 0 ? Config.Fps : 15);
            int frameTickCounter = 0;

            while (!token.IsCancellationRequested)
            {
                DateTime startTime = DateTime.Now; Mat currentFrame = null;
                lock (_frameLock) { if (_latestFrame != null && !_latestFrame.IsDisposed) currentFrame = _latestFrame.Clone(); }

                if (currentFrame != null && !currentFrame.Empty())
                {
                    if (Config.FrameWidth != currentFrame.Width || Config.FrameHeight != currentFrame.Height) { Config.FrameWidth = currentFrame.Width; Config.FrameHeight = currentFrame.Height; }
                    Mat processedFrame = currentFrame;

                    if (_isScanning && Config.EnableSmartZoom)
                    {
                        if (_delayBeforeZooming && (DateTime.Now - _lastScanTime).TotalMilliseconds >= Config.ZoomDelaySeconds * 1000.0) { _delayBeforeZooming = false; _isZooming = true; _zoomStartTime = DateTime.Now; }
                        if (_isZooming)
                        {
                            int zoomW = (int)(currentFrame.Width / Config.ZoomScale), zoomH = (int)(currentFrame.Height / Config.ZoomScale);
                            if (zoomW <= 0 || zoomW > currentFrame.Width) zoomW = currentFrame.Width; if (zoomH <= 0 || zoomH > currentFrame.Height) zoomH = currentFrame.Height;
                            OpenCvSharp.Rect zoomRect = new OpenCvSharp.Rect((currentFrame.Width - zoomW) / 2, (currentFrame.Height - zoomH) / 2, zoomW, zoomH).Intersect(new OpenCvSharp.Rect(0, 0, currentFrame.Width, currentFrame.Height));
                            if (zoomRect.Width > 0 && zoomRect.Height > 0) { processedFrame = currentFrame.Clone(zoomRect); Cv2.Resize(processedFrame, processedFrame, new OpenCvSharp.Size(Config.FrameWidth, Config.FrameHeight)); }
                            if ((DateTime.Now - _zoomStartTime).TotalMilliseconds >= Config.ZoomDurationSeconds * 1000.0) { _isZooming = false; _isScanning = false; processedFrame = currentFrame; }
                        }
                    }
                    else if (_isScanning) { _isScanning = false; }

                    if (IsRecording && _videoWriteQueue != null && !_videoWriteQueue.IsAddingCompleted) { _videoWriteQueue.Add(processedFrame.Clone()); }

                    Application.Current.Dispatcher.Invoke(() => { VideoFrame = processedFrame.ToWriteableBitmap(); });
                    if (frameTickCounter % 30 == 0) PerformMotionDetection(currentFrame);
                    if (processedFrame != currentFrame) processedFrame.Dispose(); currentFrame.Dispose();
                }

                if (IsRecording)
                {
                    double elapsedSec = (DateTime.Now - _recordStartTime).TotalSeconds;

                    if (frameTickCounter % 15 == 0 && _currentScanRecord != null)
                    {
                        _ = Application.Current.Dispatcher.InvokeAsync(() => {
                            if (_currentScanRecord != null)
                            {
                                int maxSec = (int)(Config.MaxDurationMinutes * 60);
                                _currentScanRecord.Duration = Config.EnableMaxDuration ? $"{(int)elapsedSec}s / {maxSec}s" : $"{(int)elapsedSec}s";
                            }
                        });
                    }

                    if (Config.EnableAutoStop && (DateTime.Now - _lastMotionTime).TotalSeconds >= Config.AutoStopMinutes * 60.0)
                    { _ = Application.Current.Dispatcher.InvokeAsync(() => { StopRecording(); ShowToast("画面静止超时，自动停录"); CurrentOrderId = ""; ScanInputText = ""; }); }

                    if (Config.EnableMaxDuration && elapsedSec >= Config.MaxDurationMinutes * 60.0)
                    { _ = Application.Current.Dispatcher.InvokeAsync(() => { StopRecording(); ShowToast("⏳ 已达最大录像限制时长"); CurrentOrderId = ""; ScanInputText = ""; }); }
                }

                frameTickCounter++;
                int sleepMs = (int)Math.Max(0, frameDurationMs - (DateTime.Now - startTime).TotalMilliseconds);
                if (sleepMs > 0) await Task.Delay(sleepMs, token);
            }
        }

        private void PerformMotionDetection(Mat currentFrame)
        {
            if (_previousCheckFrame.Empty()) { currentFrame.CopyTo(_previousCheckFrame); _lastMotionTime = DateTime.Now; return; }
            using var cGray = currentFrame.Resize(new OpenCvSharp.Size(320, 240)).CvtColor(ColorConversionCodes.BGR2GRAY);
            using var pGray = _previousCheckFrame.Resize(new OpenCvSharp.Size(320, 240)).CvtColor(ColorConversionCodes.BGR2GRAY);
            using var diff = new Mat(); using var thresh = new Mat();
            Cv2.Absdiff(cGray, pGray, diff); Cv2.Threshold(diff, thresh, 10.0, 255, ThresholdTypes.Binary);
            if (((double)Cv2.CountNonZero(thresh) / (thresh.Width * thresh.Height)) > 0.001) _lastMotionTime = DateTime.Now;
            currentFrame.CopyTo(_previousCheckFrame);
        }

        private void HandleScan(string scanResult)
        {
            if (string.IsNullOrWhiteSpace(scanResult)) return;
            string upperResult = scanResult.ToUpper().Trim();

            if (upperResult.Contains("CMD_CLEAR") || upperResult.Contains("清除")) { ScanInputText = ""; ShowToast("🧹 扫码框已清除"); return; }
            if (upperResult.Contains("MODE_FAHUO") || upperResult.Contains("发货")) { CurrentMode = "发货"; ScanInputText = ""; ShowToast("切换为发货模式"); return; }
            if (upperResult.Contains("MODE_TUIHUO") || upperResult.Contains("退货")) { CurrentMode = "退货"; ScanInputText = ""; ShowToast("切换为退货模式"); return; }
            if (upperResult.Contains("CMD_START") || upperResult.Contains("开始录制")) { ToggleRecording(); return; }
            if (upperResult.Contains("CMD_STOP") || upperResult.Contains("停止录制")) { StopRecordingManual(); return; }

            try { if (!System.Text.RegularExpressions.Regex.IsMatch(upperResult, Config.OrderIdRegex)) { ScanInputText = ""; ShowToast("非法单号，已拦截"); return; } } catch { }

            CurrentOrderId = upperResult;
            StartRecordingProcess();
        }

        private void StartRecordingProcess()
        {
            if (IsRecording) StopRecording();
            IsRecording = true; _lastMotionTime = DateTime.Now; _lastScanTime = DateTime.Now; _recordStartTime = DateTime.Now;
            _isScanning = true; _delayBeforeZooming = true; _isZooming = false;
            ScanInputText = "";

            string baseFolder = Path.IsPathRooted(Config.VideoStoragePath) ? Config.VideoStoragePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
            string dateFolder = Path.Combine(baseFolder, DateTime.Now.ToString("yyyy-MM-dd"));
            if (!Directory.Exists(dateFolder)) Directory.CreateDirectory(dateFolder);

            string fileName = $"{CurrentOrderId}_{DateTime.Now:yyyyMMdd_HHmmss}_{CurrentMode}.mp4";
            string filePath = Path.Combine(dateFolder, fileName);

            lock (_videoLock)
            {
                _writer = new VideoWriter(filePath, VideoWriter.FourCC('h', 'e', 'v', 'c'), Config.Fps, new OpenCvSharp.Size(Config.FrameWidth, Config.FrameHeight));
                if (!_writer.IsOpened())
                {
                    _writer?.Dispose();
                    _writer = new VideoWriter(filePath, VideoWriter.FourCC('a', 'v', 'c', '1'), Config.Fps, new OpenCvSharp.Size(Config.FrameWidth, Config.FrameHeight));
                    if (!_writer.IsOpened()) { _writer?.Dispose(); _writer = new VideoWriter(filePath, FourCC.MP4V, Config.Fps, new OpenCvSharp.Size(Config.FrameWidth, Config.FrameHeight)); }
                }

                _writeCts = new CancellationTokenSource();
                _videoWriteQueue = new BlockingCollection<Mat>(boundedCapacity: 120);
                _writeTask = Task.Run(() => BackgroundVideoWriterLoop(_writeCts.Token));
            }

            ShowToast("▶ 开始录像");
            // 【需求2】：设置 IsActive 为 true，触发红色高亮
            _currentScanRecord = new ScanRecord(CurrentOrderId, "0s", DateTime.Now.ToString("HH:mm:ss"), CurrentMode, true);
            AddRecord(_currentScanRecord);
        }

        private void BackgroundVideoWriterLoop(CancellationToken token)
        {
            try
            {
                foreach (var frame in _videoWriteQueue.GetConsumingEnumerable(token))
                {
                    if (frame != null && !frame.IsDisposed)
                    {
                        if (frame.Width != Config.FrameWidth || frame.Height != Config.FrameHeight)
                        {
                            using var resizedFrame = new Mat();
                            Cv2.Resize(frame, resizedFrame, new OpenCvSharp.Size(Config.FrameWidth, Config.FrameHeight));
                            _writer?.Write(resizedFrame);
                        }
                        else { _writer?.Write(frame); }
                        frame.Dispose();
                    }
                }
            }
            catch { }
        }

        private void StopRecording()
        {
            if (!IsRecording) return;
            IsRecording = false; _isScanning = false; _delayBeforeZooming = false; _isZooming = false;

            lock (_videoLock)
            {
                _videoWriteQueue?.CompleteAdding();
                _writeCts?.Cancel();
                try { _writeTask?.Wait(2000); } catch { }
                _writeCts?.Dispose(); _writeCts = null;
                _videoWriteQueue?.Dispose(); _videoWriteQueue = null;
                if (_writer != null) { _writer.Release(); _writer.Dispose(); _writer = null; }
            }

            var duration = DateTime.Now - _recordStartTime;
            if (duration.TotalSeconds > 0 && _recordStartTime.Year > 2000)
            {
                TotalPieces++; _totalPackTime += duration;
                OnPropertyChanged(nameof(TotalPackTimeStr)); OnPropertyChanged(nameof(AveragePackTime));

                if (_currentScanRecord != null)
                {
                    _currentScanRecord.Duration = $"{(int)duration.TotalSeconds}s";
                    _currentScanRecord.IsActive = false; // 【需求2】：录完恢复黑色
                }

                // 【核心：写入统计数据库】
                SaveStatToDatabase(duration.TotalSeconds);
            }
            _recordStartTime = DateTime.MinValue;
            _currentScanRecord = null;
        }

        private void ForceCheckDiskAndCleanup()
        {
            Task.Run(() => {
                try
                {
                    string folderPath = Path.IsPathRooted(Config.VideoStoragePath) ? Config.VideoStoragePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
                    if (Directory.Exists(folderPath))
                    {
                        var dirInfo = new DirectoryInfo(folderPath);
                        long currentSizeBytes = dirInfo.GetFiles("*.mp4", SearchOption.AllDirectories).Sum(fi => fi.Length);
                        long maxSizeBytes = (long)(Config.MaxDiskSpaceGB * 1024 * 1024 * 1024);
                        if (currentSizeBytes > maxSizeBytes)
                        {
                            var oldestFiles = dirInfo.GetFiles("*.mp4", SearchOption.AllDirectories).OrderBy(fi => fi.LastWriteTime).ToList();
                            long bytesToDelete = currentSizeBytes - (long)(maxSizeBytes * 0.9);
                            long deletedBytes = 0;
                            foreach (var file in oldestFiles) { try { long len = file.Length; file.Delete(); deletedBytes += len; if (deletedBytes >= bytesToDelete) break; } catch { } }
                            currentSizeBytes = dirInfo.GetFiles("*.mp4", SearchOption.AllDirectories).Sum(fi => fi.Length);
                        }
                        double currentGB = currentSizeBytes / (1024.0 * 1024.0 * 1024.0);
                        double maxGB = Config.MaxDiskSpaceGB > 0 ? Config.MaxDiskSpaceGB : 1.0;
                        Application.Current.Dispatcher.InvokeAsync(() => { DiskUsagePercent = Math.Min(100.0, (currentGB / maxGB) * 100.0); DiskUsageText = $"{currentGB:F1} / {maxGB:F1} GB"; });
                    }
                }
                catch { }
            });
        }
        private async Task CheckDiskAndCleanup() { while (!_cts.Token.IsCancellationRequested) { ForceCheckDiskAndCleanup(); await Task.Delay(20000, _cts.Token); } }
        public void Dispose() { StopRecording(); _cts?.Cancel(); StopCamera(); try { _videoTask?.Wait(); } catch { } _cts?.Dispose(); lock (_videoLock) { _writer?.Dispose(); _previousCheckFrame?.Dispose(); } }
    }
}