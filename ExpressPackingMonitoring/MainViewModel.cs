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
using System.Speech.Synthesis;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.ViewModels
{

    public partial class ScanRecord : ObservableObject
    {
        [ObservableProperty] private string _orderId;
        [ObservableProperty] private string _duration;
        [ObservableProperty] private string _dateStr;
        [ObservableProperty] private string _mode;

        // 新增活跃状态，用于前端变色
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
        public double MaxDiskSpaceGB { get; set; } = 950.0;
        public bool EnableAutoStop { get; set; } = true;
        public double AutoStopMinutes { get; set; } = 1.0;
        public bool EnableMaxDuration { get; set; } = false;
        public double MaxDurationMinutes { get; set; } = 5.0;

        public double MotionDetectThreshold { get; set; } = 15.0;
        public string VideoStoragePath { get; set; } = "D:\\快递打包视频";
        public string OrderIdRegex { get; set; } = "^[a-zA-Z0-9-]{12,25}$";
        public bool EnableSoundPrompt { get; set; } = true;
        public double TimeoutWarningSeconds { get; set; } = 10.0;
        public string Theme { get; set; } = "Auto";
        public bool ShowDeletedVideos { get; set; } = true;
        public bool AutoStartOnBoot { get; set; } = false;
        public bool EnableAudioRecording { get; set; } = true;
        public string AudioDeviceName { get; set; } = "";
    }

    public class MainViewModel : ObservableObject, IDisposable
    {
        private AppConfig _config;
        private readonly string _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private readonly string _dbFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "videos.db");
        private VideoDatabase _db;

        private VideoCaptureDevice _videoSource;
        private Mat _latestFrame;
        private readonly object _frameLock = new object();

        private BlockingCollection<Mat> _videoWriteQueue;
        private Task _writeTask;
        private CancellationTokenSource _writeCts;
        private int _actualCameraFps = 15; // 摄像头硬件实际帧率

        private Mat _previousCheckFrame = new Mat();
        private BitmapSource _videoFrame;
        private CancellationTokenSource _cts;
        private Task _videoTask;
        private object _videoLock = new object();

        private SpeechSynthesizer _speechSynth;
        private readonly object _speechLock = new object();

        private string _currentMode = "发货";
        private string _currentOrderId = "";
        private bool _isRecording;
        private string _scanInputText = "";
        public string ScanInputText { get => _scanInputText; set { if (SetProperty(ref _scanInputText, value)) ScheduleRefreshBarcodes(); } }

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
        private long _currentVideoRecordId;    // 数据库中当前录制记录 ID
        private string _currentVideoFilePath;  // 当前录制文件路径
        private string _stopReason = "手动";     // 停止录制的原因
        private bool _autoStopWarned = false;
        private bool _maxDurationWarned = false;
        private bool _pendingCameraRestart = false; // 录制中修改了摄像头配置，录制结束后重启

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

        public BitmapSource VideoFrame { get => _videoFrame; set => SetProperty(ref _videoFrame, value); }
        public string CurrentMode { get => _currentMode; set { if (SetProperty(ref _currentMode, value)) ScheduleRefreshBarcodes(); } }
        public string CurrentOrderId { get => _currentOrderId; set => SetProperty(ref _currentOrderId, value); }
        public bool IsRecording { get => _isRecording; set { if (SetProperty(ref _isRecording, value)) ScheduleRefreshBarcodes(); } }
        public double DiskUsagePercent { get => _diskUsagePercent; set => SetProperty(ref _diskUsagePercent, value); }
        public string DiskUsageText { get => _diskUsageText; set => SetProperty(ref _diskUsageText, value); }
        public AppConfig Config { get => _config; set => SetProperty(ref _config, value); }

        // 条形码（自动计算）
        private string _barcode1Label;
        private string _barcode2Label;
        private BitmapSource _barcode1Image;
        private BitmapSource _barcode2Image;
        public string Barcode1Label { get => _barcode1Label; set => SetProperty(ref _barcode1Label, value); }
        public string Barcode2Label { get => _barcode2Label; set => SetProperty(ref _barcode2Label, value); }
        public BitmapSource Barcode1Image { get => _barcode1Image; set => SetProperty(ref _barcode1Image, value); }
        public BitmapSource Barcode2Image { get => _barcode2Image; set => SetProperty(ref _barcode2Image, value); }
        public ICommand ClearScanInputCommand { get; }
        public ICommand ClearSearchCommand { get; }
        private void ScheduleRefreshBarcodes()
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(RefreshBarcodes), System.Windows.Threading.DispatcherPriority.Background);
        }
        private void RefreshBarcodes()
        {
            try
            {
                // 行1: 扫码框有内容→清除；空→切换模式
                string cmd1; string label1;
                if (!string.IsNullOrEmpty(_scanInputText))
                { cmd1 = "CMD_CLEAR"; label1 = "清除"; }
                else if (_currentMode == "发货")
                { cmd1 = "MODE_TUIHUO"; label1 = "退货"; }
                else
                { cmd1 = "MODE_FAHUO"; label1 = "发货"; }
                // 行2: 未录制→开录；录制中→停录
                string cmd2 = _isRecording ? "CMD_STOP" : "CMD_START";
                string label2 = _isRecording ? "停录" : "开录";
                Barcode1Label = label1;
                Barcode2Label = label2;
                Barcode1Image = BarcodeHelper.Generate(cmd1, 50, 2);
                Barcode2Image = BarcodeHelper.Generate(cmd2, 50, 2);
            }
            catch { }
        }

        public ICommand ScanCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand OpenPlaybackCommand { get; }
        public ICommand ToggleModeCommand { get; }
        public ICommand ToggleRecordingCommand { get; }
        public ICommand OpenStatsCommand { get; } // 【新增】：打开统计面板

        public MainViewModel()
        {
            LoadConfig();
            InitDatabase();
            RefreshTodayStats();
            InitSpeechSynthesizer();
            ScanCommand = new RelayCommand<string>(HandleScan);
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            OpenPlaybackCommand = new RelayCommand(OpenPlaybackWindow);
            ToggleModeCommand = new RelayCommand(ToggleMode);
            ToggleRecordingCommand = new RelayCommand(ToggleRecording);
            OpenStatsCommand = new RelayCommand(OpenStatsWindow);
            ClearScanInputCommand = new RelayCommand(() => ScanInputText = "");
            ClearSearchCommand = new RelayCommand(() => LogSearchText = "");
            InitializeSystem();
            RefreshBarcodes();
        }

        private void InitDatabase()
        {
            try
            {
                _db = new VideoDatabase(_dbFilePath);
            }
            catch { }
        }

        private void RefreshTodayStats()
        {
            try
            {
                var today = _db?.GetTodayStat();
                if (today != null)
                {
                    TotalPieces = today.TotalPieces;
                    _totalPackTime = TimeSpan.FromSeconds(today.TotalDurationSec);
                    OnPropertyChanged(nameof(TotalPackTimeStr)); OnPropertyChanged(nameof(AveragePackTime));
                }
            }
            catch { }
        }

        private void ToggleMode() { CurrentMode = CurrentMode == "发货" ? "退货" : "发货"; ShowToast($"已切换为: {CurrentMode}"); Speak(CurrentMode == "发货" ? "切换发货" : "切换退货"); }
        private void ToggleRecording() { if (IsRecording) StopRecordingManual(); else StartRecordingManual(); }

        public void ShowToast(string message) { Application.Current.Dispatcher.InvokeAsync(async () => { _toastCts?.Cancel(); _toastCts = new CancellationTokenSource(); var token = _toastCts.Token; ToastMessage = message; IsToastVisible = true; try { await Task.Delay(2500, token); } catch (OperationCanceledException) { return; } IsToastVisible = false; }); }

        private void FilterLogs() { FilteredLogs.Clear(); var keyword = LogSearchText?.ToUpper() ?? ""; foreach (var log in _allLogs) { if (string.IsNullOrEmpty(keyword) || log.OrderId.ToUpper().Contains(keyword)) FilteredLogs.Add(log); } }
        private void AddRecord(ScanRecord record) { Application.Current.Dispatcher.InvokeAsync(() => { _allLogs.Insert(0, record); if (string.IsNullOrEmpty(LogSearchText)) FilteredLogs.Insert(0, record); if (_allLogs.Count > 200) _allLogs.RemoveAt(_allLogs.Count - 1); }); }

        private void LoadConfig() 
        { 
            try 
            { 
                if (File.Exists(_configFilePath)) 
                    Config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configFilePath)) ?? new AppConfig(); 
                else 
                { 
                    Config = new AppConfig(); 
                    SaveConfig(); 
                } 
            } 
            catch { Config = new AppConfig(); } 
            
            // Apply Theme
            if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(Config.Theme, out var themeEnum))
            {
                ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
            }
            else
            {
                ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(ExpressPackingMonitoring.Themes.AppTheme.Auto);
            }
        }
        private void SaveConfig() { try { File.WriteAllText(_configFilePath, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true })); } catch { } }

        public void OpenSettings()
        {
            try
            {
                var clonedConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
                var settingsWin = new SettingsWindow(clonedConfig, DiskUsagePercent, DiskUsageText, IsRecording);
                if (Application.Current?.MainWindow != null) settingsWin.Owner = Application.Current.MainWindow;
                if (settingsWin.ShowDialog() == true) { 
                    // 判断摄像头相关配置是否变更
                    bool cameraChanged = Config.CameraIndex != clonedConfig.CameraIndex
                        || Config.FrameWidth != clonedConfig.FrameWidth
                        || Config.FrameHeight != clonedConfig.FrameHeight
                        || Config.Fps != clonedConfig.Fps;
                    bool themeChanged = Config.Theme != clonedConfig.Theme;

                    Config = clonedConfig; 
                    SaveConfig(); 

                    if (themeChanged)
                    {
                        if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(Config.Theme, out var themeEnum))
                        {
                            ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
                        }
                    }
                    ForceCheckDiskAndCleanup();

                    if (cameraChanged)
                    {
                        if (IsRecording)
                        {
                            ShowToast("⚙️ 配置已保存，摄像头配置将在录制结束后生效");
                            _pendingCameraRestart = true;
                        }
                        else
                        {
                            ShowToast("⚙️ 配置已保存，重启相机");
                            RestartCamera();
                        }
                    }
                    else
                    {
                        ShowToast("⚙️ 配置已保存");
                    }
                }
            }
            catch (Exception ex) { ShowToast($"设置错误: {ex.Message}"); }
        }

        private void OpenStatsWindow()
        {
            var statsWin = new StatisticsWindow(_db);
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

        private void StopRecordingManual() { if (!IsRecording) return; StopRecording(); CurrentOrderId = ""; ScanInputText = ""; ShowToast("已手动停止录制"); Speak("停止录制"); }

        private void OpenPlaybackWindow()
        {
            string folderPath = Path.IsPathRooted(Config.VideoStoragePath) ? Config.VideoStoragePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
            var playbackWin = new PlaybackWindow(folderPath, _db, Config.ShowDeletedVideos);
            if (Application.Current?.MainWindow != null) playbackWin.Owner = Application.Current.MainWindow;
            playbackWin.ShowDialog();
        }

        private void InitializeSystem()
        {
            _cts = new CancellationTokenSource();
            StartCamera();
            _videoTask = Task.Run(() => VideoProcessLoop(_cts.Token), _cts.Token);
            Task.Run(CheckDiskAndCleanup);
        }
        private void RestartCamera() { StopRecording(); StopCamera(); StartCamera(); }

        private void StartCamera()
        {
            try
            {
                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (videoDevices.Count == 0) { ShowToast("未检测到摄像头"); return; }
                if (Config.CameraIndex >= videoDevices.Count) Config.CameraIndex = 0;
                _videoSource = new VideoCaptureDevice(videoDevices[Config.CameraIndex].MonikerString);
                // 从摄像头能力中选择最匹配用户配置（分辨率+帧率）的模式
                if (_videoSource.VideoCapabilities.Length > 0)
                {
                    var caps = _videoSource.VideoCapabilities;
                    VideoCapabilities best = caps[0];
                    int bestScore = int.MaxValue;
                    foreach (var cap in caps)
                    {
                        // 分辨率差值权重高，帧率差值权重低
                        int resDiff = Math.Abs(cap.FrameSize.Width - Config.FrameWidth) + Math.Abs(cap.FrameSize.Height - Config.FrameHeight);
                        int fpsDiff = Math.Abs(cap.AverageFrameRate - Config.Fps);
                        int score = resDiff * 10 + fpsDiff;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = cap;
                        }
                    }
                    _videoSource.VideoResolution = best;
                    _actualCameraFps = best.AverageFrameRate > 0 ? best.AverageFrameRate : Config.Fps;
                }
                else
                {
                    _actualCameraFps = Config.Fps > 0 ? Config.Fps : 15;
                }
                _videoSource.NewFrame += VideoSource_NewFrame; _videoSource.Start();
            }
            catch { ShowToast("摄像头启动失败"); }
        }

        private void StopCamera()
        {
            if (_videoSource != null)
            {
                _videoSource.NewFrame -= VideoSource_NewFrame;
                if (_videoSource.IsRunning)
                {
                    _videoSource.SignalToStop();
                    // AForge WaitForStop can hang indefinitely — use a timeout
                    for (int i = 0; i < 50 && _videoSource.IsRunning; i++)
                        Thread.Sleep(100);
                }
                _videoSource = null;
            }
            lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = null; }
        }
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
            int frameTickCounter = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 使用硬件实际帧率控制循环节拍，与 FFmpeg 录制帧率保持一致
                    double frameDurationMs = 1000.0 / (_actualCameraFps > 0 ? _actualCameraFps : 15);
                    DateTime startTime = DateTime.Now; Mat currentFrame = null;
                    lock (_frameLock) { if (_latestFrame != null && !_latestFrame.IsDisposed) currentFrame = _latestFrame.Clone(); }

                    if (currentFrame != null && !currentFrame.Empty())
                    {
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

                        if (IsRecording && _videoWriteQueue != null && !_videoWriteQueue.IsAddingCompleted)
                        {
                            try { _videoWriteQueue.TryAdd(processedFrame.Clone(), 100); }
                            catch (ObjectDisposedException) { }
                            catch (InvalidOperationException) { }
                        }

                        var bitmap = processedFrame.ToWriteableBitmap();
                        bitmap.Freeze();
                        _ = Application.Current.Dispatcher.BeginInvoke(() => { VideoFrame = bitmap; });
                        if (frameTickCounter % 30 == 0) PerformMotionDetection(currentFrame);
                        if (processedFrame != currentFrame) processedFrame.Dispose(); currentFrame.Dispose();
                    }

                    if (IsRecording)
                    {
                        double elapsedSec = (DateTime.Now - _recordStartTime).TotalSeconds;
                        double motionIdleSec = (DateTime.Now - _lastMotionTime).TotalSeconds;
                        double warnSec = Config.TimeoutWarningSeconds;

                        // 录制前 5 秒为采集期，跳过超时与预警检测
                        bool inGracePeriod = elapsedSec < 5.0;

                        double autoStopTotalSec = Config.AutoStopMinutes * 60.0;
                        double maxDurTotalSec = Config.MaxDurationMinutes * 60.0;

                        if (!inGracePeriod)
                        {
                            // 有活跃运动时重置预警标记（滞后重置，防止反复播报）
                            if (_autoStopWarned && motionIdleSec < warnSec)
                            {
                                _autoStopWarned = false;
                                Speak("检测到画面运动，重置超时");
                            }

                            // 即将超时语音提示（确保预警阈值合理：超时总时长 + 5s）
                            if (!_autoStopWarned && Config.EnableAutoStop
                                && autoStopTotalSec > warnSec + 5
                                && motionIdleSec >= autoStopTotalSec - warnSec)
                            {
                                _autoStopWarned = true;
                                Speak("画面即将静止超时");
                            }
                            if (!_maxDurationWarned && Config.EnableMaxDuration
                                && maxDurTotalSec > warnSec * 2
                                && elapsedSec >= maxDurTotalSec - warnSec)
                            {
                                _maxDurationWarned = true;
                                Speak("录制即将达到最大时长");
                            }
                        }

                        if (frameTickCounter % 15 == 0 && _currentScanRecord != null)
                        {
                            _ = Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                if (_currentScanRecord != null)
                                {
                                    int maxSec = (int)(Config.MaxDurationMinutes * 60);
                                    _currentScanRecord.Duration = Config.EnableMaxDuration ? $"{(int)elapsedSec}s / {maxSec}s" : $"{(int)elapsedSec}s";
                                }
                            });
                        }

                        if (!inGracePeriod && Config.EnableAutoStop && (DateTime.Now - _lastMotionTime).TotalSeconds >= Config.AutoStopMinutes * 60.0)
                        { _stopReason = "静止超时"; _ = Application.Current.Dispatcher.InvokeAsync(() => { StopRecording(); ShowToast("画面静止超时，自动停录"); Speak("静止超时，停止录制"); CurrentOrderId = ""; ScanInputText = ""; }); }

                        if (!inGracePeriod && Config.EnableMaxDuration && elapsedSec >= Config.MaxDurationMinutes * 60.0)
                        { _stopReason = "时长超时"; _ = Application.Current.Dispatcher.InvokeAsync(() => { StopRecording(); ShowToast("⏳ 已达最大录像限制时长"); Speak("时长超时，停止录制"); CurrentOrderId = ""; ScanInputText = ""; }); }
                    }

                    frameTickCounter++;
                    int sleepMs = (int)Math.Max(0, frameDurationMs - (DateTime.Now - startTime).TotalMilliseconds);
                    if (sleepMs > 0) await Task.Delay(sleepMs, token);
                }
            }
            catch (OperationCanceledException) { }
        }

        private void PerformMotionDetection(Mat currentFrame)
        {
            if (_previousCheckFrame.Empty()) { currentFrame.CopyTo(_previousCheckFrame); _lastMotionTime = DateTime.Now; return; }
            using var cGray = currentFrame.Resize(new OpenCvSharp.Size(320, 240)).CvtColor(ColorConversionCodes.BGR2GRAY);
            using var pGray = _previousCheckFrame.Resize(new OpenCvSharp.Size(320, 240)).CvtColor(ColorConversionCodes.BGR2GRAY);
            using var diff = new Mat(); using var thresh = new Mat();
            Cv2.Absdiff(cGray, pGray, diff); Cv2.Threshold(diff, thresh, Config.MotionDetectThreshold, 255, ThresholdTypes.Binary);
            double changeRatio = (double)Cv2.CountNonZero(thresh) / (thresh.Width * thresh.Height);
            if (changeRatio > 0.01)
            {
                _lastMotionTime = DateTime.Now;
            }
            currentFrame.CopyTo(_previousCheckFrame);
        }

        private void HandleScan(string scanResult)
        {
            if (string.IsNullOrWhiteSpace(scanResult)) return;
            string upperResult = scanResult.ToUpper().Trim();

            if (upperResult.Contains("CMD_CLEAR") || upperResult.Contains("清除")) { ScanInputText = ""; ShowToast("🧹 扫码框已清除"); return; }
            if (upperResult.Contains("MODE_FAHUO") || upperResult.Contains("发货")) { CurrentMode = "发货"; ScanInputText = ""; ShowToast("切换为发货模式"); Speak("切换发货"); return; }
            if (upperResult.Contains("MODE_TUIHUO") || upperResult.Contains("退货")) { CurrentMode = "退货"; ScanInputText = ""; ShowToast("切换为退货模式"); Speak("切换退货"); return; }
            if (upperResult.Contains("CMD_START") || upperResult.Contains("开始录制")) { ScanInputText = ""; ToggleRecording(); return; }
            if (upperResult.Contains("CMD_STOP") || upperResult.Contains("停止录制")) { ScanInputText = ""; StopRecordingManual(); return; }

            try { if (!System.Text.RegularExpressions.Regex.IsMatch(upperResult, Config.OrderIdRegex)) { ScanInputText = ""; ShowToast("非法单号，已拦截");Speak("非法单号"); return; } } catch { }
                                                         
            CurrentOrderId = upperResult;
            if (IsRecording) _stopReason = "扫码切换";
            StartRecordingProcess();
        }

        private void StartRecordingProcess()
        {
            if (IsRecording) StopRecording();
            IsRecording = true; _lastMotionTime = DateTime.Now; _lastScanTime = DateTime.Now; _recordStartTime = DateTime.Now;
            _isScanning = true; _delayBeforeZooming = true; _isZooming = false;
            _autoStopWarned = false;
            _maxDurationWarned = false;
            // 重置参考帧，避免旧帧与新帧比较产生假运动
            _previousCheckFrame?.Dispose();
            _previousCheckFrame = new Mat();
            ScanInputText = "";

            string baseFolder = Path.IsPathRooted(Config.VideoStoragePath) ? Config.VideoStoragePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
            string dateFolder = Path.Combine(baseFolder, DateTime.Now.ToString("yyyy-MM-dd"));
            if (!Directory.Exists(dateFolder)) Directory.CreateDirectory(dateFolder);

            string fileName = $"{CurrentOrderId}_{DateTime.Now:yyyyMMdd_HHmmss}_{CurrentMode}.mp4";
            string filePath = Path.Combine(dateFolder, fileName);
            _currentVideoFilePath = filePath;
            _stopReason = "手动";

            // 录制开始时写入数据库
            try { _currentVideoRecordId = _db?.InsertVideoRecord(CurrentOrderId, CurrentMode, filePath, DateTime.Now) ?? 0; } catch { _currentVideoRecordId = 0; }

            string ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                IsRecording = false;
                ShowToast("⚠ 未找到 FFmpeg，无法录制");
                Speak("编码器不可用");
                return;
            }

            lock (_videoLock)
            {
                _writeCts = new CancellationTokenSource();
                _videoWriteQueue = new BlockingCollection<Mat>(boundedCapacity: 15);
                _writeTask = Task.Run(() => BackgroundFFmpegRecordingLoop(filePath, ffmpegPath, _writeCts.Token));
            }

            ShowToast("▶ 开始录像");
            Speak("开始录制");
            // 设置 IsActive 为 true，触发红色高亮
            _currentScanRecord = new ScanRecord(CurrentOrderId, "0s", DateTime.Now.ToString("HH:mm:ss"), CurrentMode, true);
            AddRecord(_currentScanRecord);
        }

        /// <summary>
        /// 主录制模式：通过管道将原始帧数据写入 FFmpeg，同时由 FFmpeg 录制麦克风音频，
        /// 一次性输出带声音的 mp4，无需后期合并。
        /// </summary>
        private void BackgroundFFmpegRecordingLoop(string filePath, string ffmpegPath, CancellationToken token)
        {
            Process ffmpeg = null;
            Stream stdin = null;
            bool recordingStarted = false;

            try
            {
                int w = Config.FrameWidth;
                int h = Config.FrameHeight;
                int fps = _actualCameraFps > 0 ? _actualCameraFps : Config.Fps;

                string args = BuildFFmpegArgs(w, h, fps, filePath);

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
                if (ffmpeg == null) return;

                // 后台排空 stderr 防止缓冲区死锁
                Task.Run(() => { try { ffmpeg.StandardError.ReadToEnd(); } catch { } });

                // 等待 FFmpeg 初始化
                Thread.Sleep(200);
                if (ffmpeg.HasExited)
                {
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        IsRecording = false;
                        ShowToast("⚠ FFmpeg 启动失败，请检查 ffmpeg.exe 是否完整");
                    });
                    return;
                }

                stdin = ffmpeg.StandardInput.BaseStream;
                recordingStarted = true;

                int expectedBytes = w * h * 3; // BGR24
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
            }
            catch (OperationCanceledException) { }
            catch (IOException) { } // 管道中断
            catch { }
            finally
            {
                // 关闭管道，FFmpeg 收到 EOF 后自动写入文件 trailer
                try { stdin?.Close(); } catch { }

                // 后台等待 FFmpeg 结束，不阻塞调用方
                var ffmpegRef = ffmpeg;
                var path = filePath;
                var started = recordingStarted;
                Task.Run(() =>
                {
                    try
                    {
                        if (ffmpegRef != null && !ffmpegRef.HasExited)
                        {
                            if (!ffmpegRef.WaitForExit(8000))
                            {
                                try { ffmpegRef.Kill(); } catch { }
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        try { ffmpegRef?.Dispose(); } catch { }
                        if (!started)
                        {
                            try { if (File.Exists(path)) File.Delete(path); } catch { }
                        }
                    }
                });
            }
        }

        /// <summary>
        /// 构建 FFmpeg 命令行参数：视频来自 stdin 管道（rawvideo BGR24），音频来自 DirectShow 麦克风。
        /// </summary>
        private string BuildFFmpegArgs(int w, int h, int fps, string filePath)
        {
            string args = $"-y -f rawvideo -video_size {w}x{h} -pixel_format bgr24 -framerate {fps} -i pipe:0";

            bool hasAudio = Config.EnableAudioRecording && !string.IsNullOrEmpty(Config.AudioDeviceName);
            if (hasAudio)
            {
                args += $" -f dshow -i audio=\"{Config.AudioDeviceName}\"";
            }

            args += " -c:v libx264 -pix_fmt yuv420p -preset ultrafast -crf 23";

            if (hasAudio)
            {
                args += " -c:a aac -b:a 128k -shortest";
            }

            args += $" \"{filePath}\"";
            return args;
        }

        private static string FindFFmpeg()
        {
            string appDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            return File.Exists(appDir) ? appDir : null;
        }

        private void StopRecording()
        {
            if (!IsRecording) return;
            IsRecording = false; _isScanning = false; _delayBeforeZooming = false; _isZooming = false;
            _autoStopWarned = false; _maxDurationWarned = false;

            lock (_videoLock)
            {
                // 先取消，让写入线程立即跳出循环
                _writeCts?.Cancel();
                // 再标记队列结束
                try { _videoWriteQueue?.CompleteAdding(); } catch { }
                // 短等待写入线程退出（关闭管道），FFmpeg 结束在后台异步完成
                try { _writeTask?.Wait(2000); } catch { }
                _writeCts?.Dispose(); _writeCts = null;

                // 清理队列残留帧
                if (_videoWriteQueue != null)
                {
                    while (_videoWriteQueue.TryTake(out var leftover))
                        leftover?.Dispose();
                    _videoWriteQueue.Dispose();
                    _videoWriteQueue = null;
                }
                _writeTask = null;
            }

            var duration = DateTime.Now - _recordStartTime;
            if (duration.TotalSeconds > 0 && _recordStartTime.Year > 2000)
            {
                if (_currentScanRecord != null)
                {
                    _currentScanRecord.Duration = $"{(int)duration.TotalSeconds}s";
                    _currentScanRecord.IsActive = false;
                }

                // 更新数据库记录：结束时间、时长、文件大小、停止原因
                try
                {
                    long fileSize = 0;
                    if (!string.IsNullOrEmpty(_currentVideoFilePath) && File.Exists(_currentVideoFilePath))
                        fileSize = new FileInfo(_currentVideoFilePath).Length;
                    _db?.UpdateVideoRecordOnStop(_currentVideoRecordId, DateTime.Now, duration.TotalSeconds, fileSize, _stopReason);
                }
                catch { }

                // 从数据库刷新今日统计（保证与统计窗口一致）
                RefreshTodayStats();
            }

            _recordStartTime = DateTime.MinValue;
            _currentScanRecord = null;
            _currentVideoRecordId = 0;
            _currentVideoFilePath = null;

            // 录制中修改过摄像头配置，延迟到现在生效
            if (_pendingCameraRestart)
            {
                _pendingCameraRestart = false;
                RestartCamera();
                ShowToast("摄像头配置已生效");
            }
        }

        private void ForceCheckDiskAndCleanup()
        {
            Task.Run(() =>
            {
                try
                {
                    // 优先从数据库获取总大小（快速），回退到文件系统扫描
                    long currentSizeBytes = _db?.GetTotalFileSizeBytes() ?? 0;
                    if (currentSizeBytes == 0)
                    {
                        string folderPath = Path.IsPathRooted(Config.VideoStoragePath) ? Config.VideoStoragePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
                        if (Directory.Exists(folderPath))
                        {
                            foreach (var fi in new DirectoryInfo(folderPath).EnumerateFiles("*.mp4", SearchOption.AllDirectories))
                                currentSizeBytes += fi.Length;
                        }
                    }

                    // 加上当前正在录制的文件实际大小（数据库中尚未更新）
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

                    // 检查视频存储所在磁盘的实际剩余空间，取配额和磁盘剩余中较小者
                    try
                    {
                        string storagePath = Path.IsPathRooted(Config.VideoStoragePath) ? Config.VideoStoragePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
                        string rootDrive = Path.GetPathRoot(Path.GetFullPath(storagePath)) ?? "";
                        if (!string.IsNullOrEmpty(rootDrive))
                        {
                            var driveInfo = new DriveInfo(rootDrive);
                            if (driveInfo.IsReady)
                            {
                                // 磁盘可用于视频的总容量 = 当前已用 + 磁盘剩余可用空间
                                long diskAvailableForVideos = currentSizeBytes + driveInfo.AvailableFreeSpace;
                                // 保留空间 = min(2GB, 磁盘总容量的5%)，最少500MB
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

                        // 从数据库查询最旧的视频，按时间升序删除
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
                                        len = new FileInfo(video.FilePath).Length; // 用实际大小
                                        File.Delete(video.FilePath);
                                    }
                                    // 数据库记录删除日志
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
                            // 数据库为空时回退到文件系统删除
                            string folderPath = Path.IsPathRooted(Config.VideoStoragePath) ? Config.VideoStoragePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
                            if (Directory.Exists(folderPath))
                            {
                                foreach (var file in new DirectoryInfo(folderPath).EnumerateFiles("*.mp4", SearchOption.AllDirectories).OrderBy(fi => fi.LastWriteTime))
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
                                ShowToast($"🗑 磁盘清理: 删除{deletedCount}个视频，释放{deletedMB:F0}MB");
                                RefreshTodayStats(); // 清理后刷新统计（删除的视频不再计入）
                            });
                        }
                    }

                    double currentGB = currentSizeBytes / (1024.0 * 1024.0 * 1024.0);
                    double effectiveMaxGB = maxSizeBytes / (1024.0 * 1024.0 * 1024.0);
                    if (effectiveMaxGB < 0.1) effectiveMaxGB = 0.1;
                    double configMaxGB = Config.MaxDiskSpaceGB > 0 ? Config.MaxDiskSpaceGB : 1.0;
                    string limitNote = effectiveMaxGB < configMaxGB - 0.1 ? $" (磁盘仅剩{effectiveMaxGB:F1}GB可用)" : "";
                    _ = Application.Current.Dispatcher.InvokeAsync(() => { DiskUsagePercent = Math.Min(100.0, (currentGB / effectiveMaxGB) * 100.0); DiskUsageText = $"{currentGB:F1} / {effectiveMaxGB:F1} GB{limitNote}"; });
                }
                catch { }
            });
        }
        private async Task CheckDiskAndCleanup() { while (!_cts.Token.IsCancellationRequested) { ForceCheckDiskAndCleanup(); int interval = IsRecording ? 10000 : 60000; await Task.Delay(interval, _cts.Token); } }
        public void Dispose()
        {
            _cts?.Cancel();
            _stopReason = "程序退出";
            StopRecording();
            StopCamera();
            try { _videoTask?.Wait(3000); } catch { }
            _cts?.Dispose();
            lock (_videoLock) { _previousCheckFrame?.Dispose(); }
            lock (_speechLock) { _speechSynth?.Dispose(); _speechSynth = null; }
            try { _db?.Dispose(); } catch { }
        }

        private void InitSpeechSynthesizer()
        {
            try
            {
                _speechSynth = new SpeechSynthesizer();
                _speechSynth.SetOutputToDefaultAudioDevice();
                // 尝试使用中文语音
                foreach (var voice in _speechSynth.GetInstalledVoices())
                {
                    if (voice.VoiceInfo.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                    {
                        _speechSynth.SelectVoice(voice.VoiceInfo.Name);
                        break;
                    }
                }
                _speechSynth.Rate = 2; // 语速稍快
                _speechSynth.Volume = 100;
            }
            catch { _speechSynth = null; }
        }

        private void Speak(string text)
        {
            if (!Config.EnableSoundPrompt) return;
            Task.Run(() =>
            {
                lock (_speechLock)
                {
                    try
                    {
                        _speechSynth?.SpeakAsyncCancelAll();
                        _speechSynth?.SpeakAsync(text);
                    }
                    catch { }
                }
            });
        }
    }
}