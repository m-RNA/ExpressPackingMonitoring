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
using System.ComponentModel;
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

    public class GpuEncoderOption
    {
        public string Value { get; set; }
        public string DisplayName { get; set; }
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
        public int AudioSyncOffsetMs { get; set; } = 400;
        public double BarcodeCooldownSeconds { get; set; } = 2.0;
        public string GpuEncoder { get; set; } = "nvidia";
        public string VideoCodec { get; set; } = "h265"; // "h264" or "h265"
        public int VideoCqp { get; set; } = 30;

        // 缓存的检测结果
        public List<GpuEncoderOption> EncoderOptionsCache { get; set; }
        public List<string> ValidatedEncodersCache { get; set; }
        public bool IsEncoderDetected { get; set; } = false;
    }

    public class MainViewModel : ObservableObject, IDisposable
    {
        private AppConfig _config;
        private readonly string _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private readonly string _dbFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "videos.db");
        private VideoDatabase _db;

        /// <summary>启动时缓存的可用 GPU 编码器列表</summary>
        public static List<GpuEncoderOption> CachedEncoderOptions { get; private set; }

        /// <summary>启动时通过试编码验证的所有编码器名称（包括 H.264 和 H.265）</summary>
        public static HashSet<string> ValidatedEncoders { get; private set; } = new();

        private VideoCaptureDevice _videoSource;
        private Mat _latestFrame;
        private readonly object _frameLock = new object();

        private BlockingCollection<Mat> _videoWriteQueue;
        private Task _writeTask;
        private Task _lastFinalizeTask;
        private CancellationTokenSource _writeCts;
        private int _actualCameraFps = 15; // 摄像头硬件实际帧率

        private Mat _previousCheckFrame = new Mat();
        private BitmapSource _videoFrame;
        private CancellationTokenSource _cts;
        private Task _videoTask;
        private object _videoLock = new object();

        private bool _isInputOnCooldown = false; // 全局输入冷却锁

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
        private string _currentVideoFilePath;  // 当前录制文件路径
        private string _currentVideoCodec;
        private string _currentVideoEncoder;
        private string _stopReason = "手动";     // 停止录制的原因
        private string _recordingOrderId;       // 录制开始时的单号
        private string _recordingMode;          // 录制开始时的模式
        private bool _autoStopWarned = false;
        private bool _maxDurationWarned = false;
        private bool _pendingCameraRestart = false; // 录制中修改了摄像头配置，录制结束后重启
        private bool _isEncoderDetectRunning = true; // 是否正在进行 GPU 编码器检测

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
        private double _barcode1CooldownProgress;
        private double _barcode2CooldownProgress;
        public string Barcode1Label { get => _barcode1Label; set => SetProperty(ref _barcode1Label, value); }
        public string Barcode2Label { get => _barcode2Label; set => SetProperty(ref _barcode2Label, value); }
        public BitmapSource Barcode1Image { get => _barcode1Image; set => SetProperty(ref _barcode1Image, value); }
        public BitmapSource Barcode2Image { get => _barcode2Image; set => SetProperty(ref _barcode2Image, value); }
        public double Barcode1CooldownProgress { get => _barcode1CooldownProgress; set => SetProperty(ref _barcode1CooldownProgress, value); }
        public double Barcode2CooldownProgress { get => _barcode2CooldownProgress; set => SetProperty(ref _barcode2CooldownProgress, value); }
        public ICommand ClearScanInputCommand { get; }
        public ICommand ClearSearchCommand { get; }
        private CancellationTokenSource _barcode1CooldownCts;
        private CancellationTokenSource _barcode2CooldownCts;
        private bool _barcode1OnCooldown;
        private bool _barcode2OnCooldown;
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
                { cmd1 = "CLEAR"; label1 = "清\n除"; }
                else if (_currentMode == "发货")
                { cmd1 = "BACK"; label1 = "退\n货"; }
                else
                { cmd1 = "SHIP"; label1 = "发\n货"; }
                // 行2: 未录制→开录；录制中→停录
                string cmd2 = _isRecording ? "STOP" : "START";
                string label2 = _isRecording ? "停\n录" : "开\n录";
                if (!_barcode1OnCooldown)
                {
                    Barcode1Label = label1;
                    Barcode1Image = BarcodeHelper.Generate(cmd1, 70, 3);
                }
                if (!_barcode2OnCooldown)
                {
                    Barcode2Label = label2;
                    Barcode2Image = BarcodeHelper.Generate(cmd2, 70, 3);
                }
            }
            catch { }
        }

        private async void HideBarcode1Temporarily()
        {
            _barcode1CooldownCts?.Cancel();
            var cts = _barcode1CooldownCts = new CancellationTokenSource();
            _barcode1OnCooldown = true;
            Barcode1Image = null; Barcode1Label = "";
            Barcode1CooldownProgress = 0;
            double totalMs = Config.BarcodeCooldownSeconds * 1000;
            const int step = 50;
            double elapsed = 0;
            try
            {
                while (elapsed < totalMs)
                {
                    await Task.Delay(step, cts.Token);
                    elapsed += step;
                    Barcode1CooldownProgress = Math.Min(100, elapsed / totalMs * 100);
                }
            }
            catch { return; }
            _barcode1OnCooldown = false;
            Barcode1CooldownProgress = 0;
            if (!cts.IsCancellationRequested) RefreshBarcodes();
        }

        private async void HideBarcode2Temporarily()
        {
            _barcode2CooldownCts?.Cancel();
            var cts = _barcode2CooldownCts = new CancellationTokenSource();
            _barcode2OnCooldown = true;
            Barcode2Image = null; Barcode2Label = "";
            Barcode2CooldownProgress = 0;
            double totalMs = Config.BarcodeCooldownSeconds * 1000;
            const int step = 50;
            double elapsed = 0;
            try
            {
                while (elapsed < totalMs)
                {
                    await Task.Delay(step, cts.Token);
                    elapsed += step;
                    Barcode2CooldownProgress = Math.Min(100, elapsed / totalMs * 100);
                }
            }
            catch { return; }
            _barcode2OnCooldown = false;
            Barcode2CooldownProgress = 0;
            if (!cts.IsCancellationRequested) RefreshBarcodes();
        }

        public ICommand ScanCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand OpenPlaybackCommand { get; }
        public ICommand ToggleModeCommand { get; }
        public ICommand ToggleRecordingCommand { get; }
        public ICommand OpenStatsCommand { get; } // 【新增】：打开统计面板
        public ICommand ResetEncoderDetectCommand { get; } // 【新增】：重置编码器检测

        public MainViewModel()
        {
            // 跳过 XAML 设计器环境，避免 XDG0003 等设计时错误
            if (DesignerProperties.GetIsInDesignMode(new System.Windows.DependencyObject()))
            {
                ScanCommand = new RelayCommand<string>(_ => { });
                OpenSettingsCommand = new RelayCommand(() => { });
                OpenPlaybackCommand = new RelayCommand(() => { });
                ToggleModeCommand = new RelayCommand(() => { });
                ToggleRecordingCommand = new RelayCommand(() => { });
                OpenStatsCommand = new RelayCommand(() => { });
                ClearScanInputCommand = new RelayCommand(() => { });
                ClearSearchCommand = new RelayCommand(() => { });
                return;
            }

            LoadConfig();
            // 在起动时后台探测可用 GPU 编码器并缓存
            Task.Run(() => {
                _isEncoderDetectRunning = true;
                if (Config.IsEncoderDetected && Config.EncoderOptionsCache != null && Config.ValidatedEncodersCache != null)
                {
                    CachedEncoderOptions = Config.EncoderOptionsCache;
                    ValidatedEncoders = new HashSet<string>(Config.ValidatedEncodersCache);
                }
                else
                {
                    var (options, validated) = DetectAvailableEncodersSync();
                    CachedEncoderOptions = options;
                    ValidatedEncoders = validated;
                    
                    // 保存到配置中
                    Config.EncoderOptionsCache = options;
                    Config.ValidatedEncodersCache = validated.ToList();
                    Config.IsEncoderDetected = true;
                    SaveConfig();
                }
                _isEncoderDetectRunning = false;
            });
            InitDatabase();
            RefreshTodayStats();
            InitSpeechSynthesizer();
            ScanCommand = new RelayCommand<string>(HandleScan);
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            OpenPlaybackCommand = new RelayCommand(OpenPlaybackWindow);
            ToggleModeCommand = new RelayCommand(ToggleMode);
            ToggleRecordingCommand = new RelayCommand(ToggleRecording);
            OpenStatsCommand = new RelayCommand(OpenStatsWindow);
            ResetEncoderDetectCommand = new RelayCommand(ResetEncoderDetect);
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

            if (Config.VideoCqp <= 0)
                Config.VideoCqp = 25;
            Config.AudioSyncOffsetMs = Math.Clamp(Config.AudioSyncOffsetMs, -5000, 5000);
            
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
            if (_isEncoderDetectRunning)
            {
                ShowToast("⏳ 编码器环境检测中，请稍后打开设置...");
                return;
            }

            try
            {
                var clonedConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
                var settingsWin = new SettingsWindow(this, clonedConfig, DiskUsagePercent, DiskUsageText, IsRecording);
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
            try
            {
                var playbackWin = new PlaybackWindow(folderPath, _db, Config.ShowDeletedVideos);
                if (Application.Current?.MainWindow != null) playbackWin.Owner = Application.Current.MainWindow;
                playbackWin.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开回放窗口失败：{ex.Message}", "回放错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
                try { _videoSource.NewFrame -= VideoSource_NewFrame; } catch { }
                try
                {
                    if (_videoSource.IsRunning)
                    {
                        _videoSource.SignalToStop();
                        for (int i = 0; i < 50 && _videoSource.IsRunning; i++)
                            Thread.Sleep(100);
                    }
                }
                catch (SEHException) { /* AForge COM cleanup on some laptops */ }
                catch { }
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
                            // 如果后台录制任务已经报错停止（Task 已完成但不是因为取消请求）
                            if (_writeTask != null && _writeTask.IsCompleted)
                            {
                                // 这种情况下不应该继续填充队列
                                continue;
                            }

                            try
                            {
                                var clone = processedFrame.Clone();
                                // 修改：使用 TryAdd 并设置极短的超时（例如 100ms）
                                // 如果 5ms 进不去队列，说明磁盘或编码器卡了，直接丢弃这一帧，保住预览不卡死
                                if (!_videoWriteQueue.TryAdd(clone, 5))
                                {
                                    clone.Dispose();
                                }
                            }
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
            // 1. 如果处于冷却期，直接清空输入并拦截
            if (_isInputOnCooldown)
            {
                ScanInputText = "";
                return;
            }

            if (string.IsNullOrWhiteSpace(scanResult)) return;
            string upperResult = scanResult.ToUpper().Trim();

            // 指令处理
            if (upperResult.Contains("CLEAR") || upperResult.Contains("清除")) { ScanInputText = ""; ShowToast("🧹 扫码框已清除"); return; }
            if (upperResult.Contains("SHIP") || upperResult.Contains("发货")) { CurrentMode = "发货"; ScanInputText = ""; StartInputCooldown(); ShowToast("切换为发货模式"); Speak("切换发货"); return; }
            if (upperResult.Contains("BACK") || upperResult.Contains("退货")) { CurrentMode = "退货"; ScanInputText = ""; StartInputCooldown(); ShowToast("切换为退货模式"); Speak("切换退货"); return; }
            if (upperResult.Contains("START") || upperResult.Contains("开始录制")) { ScanInputText = ""; ToggleRecording(); return; } // ToggleRecording 内部会触发冷却
            if (upperResult.Contains("STOP") || upperResult.Contains("停止录制")) { ScanInputText = ""; StopRecordingManual(); return; }

            // 正则验证
            try { if (!System.Text.RegularExpressions.Regex.IsMatch(upperResult, Config.OrderIdRegex)) { ScanInputText = ""; ShowToast("非法单号，已拦截"); Speak("非法单号"); return; } } catch { }

            // 2. 触发冷却并执行录制转换
            StartInputCooldown();
            CurrentOrderId = upperResult;
            if (IsRecording) _stopReason = "扫码切换";
            StartRecordingProcess();
        }

        // 新增：启动输入冷却控制
        private async void StartInputCooldown()
        {
            if (_isInputOnCooldown) return;
            _isInputOnCooldown = true;

            // 复用配置中的冷却秒数
            double cooldownMs = Config.BarcodeCooldownSeconds * 1000;

            // 启动定时任务清除冷却状态
            _ = Task.Run(async () =>
            {
                await Task.Delay((int)cooldownMs);
                _isInputOnCooldown = false;
            });

            // 同时触发原有条码图标的隐藏逻辑
            HideBarcode1Temporarily();
            HideBarcode2Temporarily();
        }

        private async void StartRecordingProcess()
        {
            if (IsRecording)
            {
                StopRecording();
                // 强制等待 300ms，给系统和 GPU 释放资源的时间
                await Task.Delay(300);
            }

            // 重置状态
            IsRecording = true;
            _lastMotionTime = DateTime.Now;
            _lastScanTime = DateTime.Now;
            _recordStartTime = DateTime.Now;
            _isScanning = true;
            _delayBeforeZooming = true;
            _isZooming = false;
            _autoStopWarned = false;
            _maxDurationWarned = false;
            // 重置参考帧，避免旧帧与新帧比较产生假运动
            _previousCheckFrame?.Dispose();
            _previousCheckFrame = new Mat();
            ScanInputText = "";

            string baseFolder = Path.IsPathRooted(Config.VideoStoragePath) ? Config.VideoStoragePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.VideoStoragePath);
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
                IsRecording = false;
                ShowToast("⚠ 未找到 FFmpeg，无法录制");
                Speak("编码器不可用");
                return;
            }

            // 关键点：在启动前，先尝试清理可能残留的 ffmpeg 僵尸进程
            // 特别是针对上一个视频是长视频的情况
            await Task.Run(() =>
            {
                try
                {
                    var processes = Process.GetProcessesByName("ffmpeg");
                    foreach (var p in processes)
                    {
                        // 如果进程已经运行很久了（可能是残留），杀掉它
                        if ((DateTime.Now - p.StartTime).TotalMinutes > 5) p.Kill();
                    }
                }
                catch { }
            });

            // 启动新的 FFmpeg 线程
            lock (_videoLock)
            {
                _writeCts = new CancellationTokenSource();
                _videoWriteQueue = new BlockingCollection<Mat>(boundedCapacity: 60);
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
            int w = Config.FrameWidth;
            int h = Config.FrameHeight;
            int fps = _actualCameraFps > 0 ? _actualCameraFps : Config.Fps;
            string encoder = ResolveEncoder();
            bool hasAudio = Config.EnableAudioRecording && !string.IsNullOrEmpty(Config.AudioDeviceName);
            string requestedEncoder = encoder;

            // 仅使用用户选定的编码器进行录制，不进行自动回退
            var (ok, err) = RunFFmpegPipeline(filePath, ffmpegPath, token, w, h, fps, encoder, hasAudio);
            
            if (ok)
            {
                _currentVideoEncoder = encoder;
                _currentVideoCodec = EncodingHelper.GetCodecFromEncoder(encoder);
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    ShowToast($"编码器 {EncodingHelper.GetEncoderLabel(encoder)}"));
                return;
            }

            // 全部失败或用户取消
            if (!token.IsCancellationRequested)
            {
                try { if (File.Exists(filePath) && new FileInfo(filePath).Length == 0) File.Delete(filePath); } catch { }
                string errMsg = string.IsNullOrEmpty(err) ? "" : $"\n{err}";

                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    // --- 核心修复：立即重置 UI 状态 ---
                    IsRecording = false;
                    CurrentOrderId = "";
                    ScanInputText = "";

                    // 彻底清理队列，防止内存堆积
                    lock (_videoLock)
                    {
                        if (_videoWriteQueue != null)
                        {
                            _videoWriteQueue.CompleteAdding();
                            while (_videoWriteQueue.TryTake(out var m)) m?.Dispose();
                        }
                    }

                    ShowToast($"⚠ 录制失败，请检查配置{errMsg}");
                    Speak("录制失败");
                    MessageBox.Show(
                        $"当前设置的编码器无法完成录制，视频未保存。\n\n请求编码器: {EncodingHelper.GetEncoderLabel(requestedEncoder)}\n错误详情: {err}\n\n建议在设置中更换编码器或尝试 CPU 软编码。",
                        "录制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            }
        }

        /// <summary>根据当前 VideoCodec 设置获取 CPU 编码器名称</summary>
        private string GetCpuEncoder()
        {
            return (Config.VideoCodec?.ToLowerInvariant() ?? "h264") switch
            {
                "h265" => "libx265",
                "av1" => "libsvtav1",
                _ => "libx264"
            };
        }

        /// <summary>将 GPU 名称和编解码器映射为具体的 FFmpeg 编码器名称</summary>

        /// <summary>
        /// 启动 FFmpeg 进程并通过管道写入帧数据。返回 (true, "") 表示正常录制，(false, stderr) 表示失败。
        /// </summary>
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

                // 异步捕获 stderr
                var stderrTask = Task.Run(() => { try { return ffmpeg.StandardError.ReadToEnd(); } catch { return ""; } });

                // 短暂等待检测 FFmpeg 是否立即崩溃（编码器不支持等）
                for (int wait = 0; wait < 150 && !ffmpeg.HasExited; wait += 30)
                    Thread.Sleep(30);
                if (ffmpeg.HasExited)
                {
                    stderrText = stderrTask.GetAwaiter().GetResult();
                    Debug.WriteLine($"[FFmpeg] early exit ({encoder}): {stderrText}");
                    // 提取最后一行有意义的错误信息
                    string shortErr = ExtractFFmpegError(stderrText);
                    return (false, shortErr);
                }

                stdin = ffmpeg.StandardInput.BaseStream;

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

                try
                {
                    stdin?.Close();
                    stdinClosed = true;
                }
                catch { }

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
                // 关闭管道，FFmpeg 收到 EOF 后自动写入文件 trailer
                if (!stdinClosed)
                {
                    try { stdin?.Close(); } catch { }
                }

                // 同步等待 FFmpeg 完成（必须同步，否则回退重试时文件被锁）
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

        /// <summary>从 FFmpeg stderr 中提取简短错误信息</summary>
        private static string ExtractFFmpegError(string stderr)
        {
            if (string.IsNullOrEmpty(stderr)) return "";
            // 取最后几行中包含关键词的行
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

        /// <summary>
        /// 构建 FFmpeg 命令行参数：视频来自 stdin 管道（rawvideo BGR24），音频可选来自 DirectShow 麦克风。
        /// </summary>
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

            // H.264 编码器
            if (encoder == "h264_nvenc")
                args += $" -c:v h264_nvenc -pix_fmt yuv420p -preset p4 -rc vbr -cq {cqp} -b:v 0 -g {gop}";
            else if (encoder == "h264_amf")
                args += $" -c:v h264_amf -pix_fmt yuv420p -quality balanced -rc cqp -qp_i {cqp} -qp_p {cqp} -g {gop}";
            else if (encoder == "h264_qsv")
                args += $" -c:v h264_qsv -pix_fmt nv12 -preset medium -global_quality {cqp} -g {gop}";
            else if (encoder == "libx264")
                args += $" -c:v libx264 -pix_fmt yuv420p -preset fast -crf {cqp} -g {gop}";
            // H.265 (HEVC) 编码器
            else if (encoder == "hevc_nvenc")
                args += $" -c:v hevc_nvenc -pix_fmt yuv420p -preset p4 -rc vbr -cq {cqp} -b:v 0 -g {gop}";
            else if (encoder == "hevc_amf")
                args += $" -c:v hevc_amf -pix_fmt yuv420p -quality balanced -rc cqp -qp_i {cqp} -qp_p {cqp} -g {gop}";
            else if (encoder == "hevc_qsv")
                args += $" -c:v hevc_qsv -pix_fmt nv12 -preset medium -global_quality {cqp} -g {gop}";
            else if (encoder == "libx265")
                args += $" -c:v libx265 -pix_fmt yuv420p -preset fast -crf {cqp} -g {gop}";
            // AV1 编码器 — 使用分辨率自适应目标码率
            else if (encoder == "av1_nvenc")
                args += $" -c:v av1_nvenc -pix_fmt yuv420p -preset p4 -rc vbr -cq {cqp} -b:v 0 -g {gop}";
            else if (encoder == "av1_amf")
                args += $" -c:v av1_amf -pix_fmt yuv420p -quality balanced -rc cqp -qp_i {cqp} -qp_p {cqp} -g {gop}";
            else if (encoder == "av1_qsv")
                args += $" -c:v av1_qsv -pix_fmt nv12 -preset medium -global_quality {cqp} -g {gop}";
            else if (encoder == "libsvtav1")
                args += $" -c:v libsvtav1 -pix_fmt yuv420p -preset {GetCpuAv1Preset(w, h, fps)} -crf {cqp} -svtav1-params tune=0 -g {gop}";
            else
                args += $" -c:v {encoder} -pix_fmt yuv420p -g {gop}";

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
            if (offsetMs > 0)
                return $"adelay={offsetMs}:all=1,aresample=async=1:first_pts=0";
            if (offsetMs < 0)
            {
                double trimStartSec = Math.Abs(offsetMs) / 1000.0;
                return $"atrim=start={trimStartSec:0.###},asetpts=PTS-STARTPTS,aresample=async=1:first_pts=0";
            }

            return "aresample=async=1:first_pts=0";
        }

        private int GetVideoCqp() => Config.VideoCqp > 0 ? Config.VideoCqp : 25;

        /// <summary>
        /// 根据分辨率和帧率计算 AV1 目标码率 (kb/s)。
        /// 基准 (30fps): 720P=387, 1080P=906, 2K=1600, 4K=2050；按帧率线性缩放。
        /// </summary>
        private static int CalcAv1Bitrate(int w, int h, int fps)
        {
            long pixels = (long)w * h;
            // 30fps 下的基准码率 (kb/s)，按像素数线性插值
            (long px, int kbps)[] table = {
                (1280L * 720,   387),
                (1920L * 1080,  906),
                (2560L * 1440, 1600),
                (3840L * 2160, 2050)
            };
            int baseKbps;
            if (pixels <= table[0].px)
                baseKbps = table[0].kbps;
            else if (pixels >= table[^1].px)
                baseKbps = table[^1].kbps;
            else
            {
                baseKbps = table[^1].kbps;
                for (int i = 0; i < table.Length - 1; i++)
                {
                    if (pixels <= table[i + 1].px)
                    {
                        double t = (double)(pixels - table[i].px) / (table[i + 1].px - table[i].px);
                        baseKbps = (int)(table[i].kbps + t * (table[i + 1].kbps - table[i].kbps));
                        break;
                    }
                }
            }
            // 按帧率线性缩放（基准 30fps）
            double fpsScale = Math.Max(fps, 1) / 30.0;
            return Math.Max(100, (int)(baseKbps * fpsScale));
        }

        private static string FindFFmpeg()
        {
            string appDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            return File.Exists(appDir) ? appDir : null;
        }

        /// <summary>
        /// 根据用户设置解析最终要使用的编码器。
        /// GpuEncoder 存储 GPU 偏好（"auto"/"nvidia"/"amd"/"intel"/"cpu"），
        /// 结合 VideoCodec（"h264"/"h265"）动态映射为具体编码器。
        /// </summary>
        private string ResolveEncoder()
        {
            string codec = Config.VideoCodec?.Trim().ToLowerInvariant() ?? "h264";
            if (codec != "h264" && codec != "h265" && codec != "av1") codec = "h264";
            string cpuEncoder = codec switch { "h265" => "libx265", "av1" => "libsvtav1", _ => "libx264" };

            string gpu = EncodingHelper.NormalizeGpuSetting(Config.GpuEncoder?.Trim().ToLowerInvariant() ?? "auto");

            if (gpu != "auto")
            {
                // 用户指定了具体 GPU
                string encoder = EncodingHelper.ResolveRequestedEncoder(gpu, codec);
                if (encoder == cpuEncoder || (ValidatedEncoders != null && ValidatedEncoders.Contains(encoder)))
                    return encoder;
                return cpuEncoder;
            }

            // 自动模式：按 NVIDIA > AMD > Intel > CPU 优先级
            foreach (var g in new[] { "nvidia", "amd", "intel" })
            {
                string encoder = EncodingHelper.ResolveRequestedEncoder(g, codec);
                if (ValidatedEncoders != null && ValidatedEncoders.Contains(encoder))
                    return encoder;
            }
            return cpuEncoder;
        }

        /// <summary>
        /// 一次性调用 ffmpeg -encoders 并返回完整输出，避免多次启动进程。
        /// </summary>
        private static string QueryFFmpegEncoders(string ffmpegPath)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                return output ?? "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 重新执行 GPU 编码器检测并更新缓存。
        /// </summary>
        public void ResetEncoderDetect()
        {
            if (_isEncoderDetectRunning)
            {
                ShowToast("⏳ 检测已在运行中...");
                return;
            }

            Task.Run(() =>
            {
                _isEncoderDetectRunning = true;
                ShowToast("🔄 正在重新检测 GPU 编码器，请稍候...");

                var (options, validated) = DetectAvailableEncodersSync();
                CachedEncoderOptions = options;
                ValidatedEncoders = validated;

                // 更新并保存配置
                Config.EncoderOptionsCache = options;
                Config.ValidatedEncodersCache = validated.ToList();
                Config.IsEncoderDetected = true;
                SaveConfig();

                _isEncoderDetectRunning = false;
                ShowToast("✅ 编码器重新检测完成");
            });
        }

        /// <summary>
        /// 用实际试编码验证某个 GPU 编码器在当前硬件上是否真正可用。
        /// </summary>
        private static (bool ok, string stderr) TestEncoder(string ffmpegPath, string encoder)
        {
            try
            {
                // NVENC 要求最小分辨率 ≥ 146x50 左右，用 256x256 保证所有 GPU 编码器兼容
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-f lavfi -i color=black:s=256x256:d=0.1 -frames:v 2 -an -pix_fmt yuv420p -c:v {encoder} -f null -",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (false, "Process.Start returned null");
                string stderr = proc.StandardError.ReadToEnd();
                bool exited = proc.WaitForExit(15000);
                int exitCode = exited ? proc.ExitCode : -999;
                return (exited && exitCode == 0, $"exit={exitCode} stderr={stderr}");
            }
            catch (Exception ex) { return (false, $"exception: {ex.Message}"); }
        }

        /// <summary>
        /// 同步枚举系统上 FFmpeg 可用的 GPU 编码器列表（H.264 + H.265），并通过试编码验证硬件是否真正支持。
        /// 检测结果写入 encoder_detect.log 便于排查。
        /// 返回 (GPU 选项列表, 通过验证的编码器名称集合)。
        /// </summary>
        public static (List<GpuEncoderOption> options, HashSet<string> validated) DetectAvailableEncodersSync()
        {
            var log = new System.Text.StringBuilder();
            log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === GPU 编码器检测开始 ===");

            var list = new List<GpuEncoderOption>
            {
                new GpuEncoderOption { Value = "auto", DisplayName = "自动检测（优先独显）" },
                new GpuEncoderOption { Value = "cpu", DisplayName = "CPU 软编码" }
            };
            var validated = new HashSet<string> { "libx264", "libx265" }; // H.264/H.265 CPU 编码器始终可用

            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            log.AppendLine($"FFmpeg 路径: {ffmpegPath}");
            log.AppendLine($"FFmpeg 存在: {File.Exists(ffmpegPath)}");

            if (!File.Exists(ffmpegPath))
            {
                log.AppendLine("⚠ FFmpeg 不存在，跳过检测");
                WriteEncoderLog(log);
                return (list, validated);
            }

            string output = QueryFFmpegEncoders(ffmpegPath);
            log.AppendLine($"ffmpeg -encoders 输出长度: {output.Length}");

            // 按 GPU 厂商分组，每个厂商检测 H.264、H.265、AV1 三种编码器
            var gpuGroups = new[]
            {
                (gpu: "nvidia", label: "NVIDIA GPU (NVENC)",  encs: new[] { "h264_nvenc", "hevc_nvenc", "av1_nvenc" }),
                (gpu: "amd",    label: "AMD GPU (AMF)",       encs: new[] { "h264_amf",   "hevc_amf",   "av1_amf" }),
                (gpu: "intel",  label: "Intel GPU (QSV)",     encs: new[] { "h264_qsv",   "hevc_qsv",   "av1_qsv" })
            };

            foreach (var (gpu, label, encs) in gpuGroups)
            {
                log.AppendLine($"\n=== {label} ===");
                bool anyPassed = false;

                foreach (var enc in encs)
                {
                    bool inList = output.Contains(enc);
                    log.AppendLine($"  --- {enc} ---");
                    log.AppendLine($"    ffmpeg -encoders 包含: {inList}");

                    if (!inList)
                    {
                        log.AppendLine($"    跳过试编码（不在编码器列表中）");
                        continue;
                    }

                    var (testOk, testDetail) = TestEncoder(ffmpegPath, enc);
                    log.AppendLine($"    试编码结果: {(testOk ? "✓ 通过" : "✗ 失败")}");
                    log.AppendLine($"    详情: {testDetail}");

                    if (testOk)
                    {
                        validated.Add(enc);
                        anyPassed = true;
                    }
                }

                if (anyPassed)
                    list.Insert(list.Count - 1, new GpuEncoderOption { Value = gpu, DisplayName = label });
            }

            // 检测 CPU AV1 编码器（libsvtav1 可能不包含在 FFmpeg essentials 构建中）
            {
                log.AppendLine($"\n=== CPU AV1 (libsvtav1) ===");
                bool svtInList = output.Contains("libsvtav1");
                log.AppendLine($"  ffmpeg -encoders 包含: {svtInList}");
                if (svtInList)
                {
                    var (testOk, testDetail) = TestEncoder(ffmpegPath, "libsvtav1");
                    log.AppendLine($"  试编码结果: {(testOk ? "✓ 通过" : "✗ 失败")}");
                    log.AppendLine($"  详情: {testDetail}");
                    if (testOk) validated.Add("libsvtav1");
                }
                else
                {
                    log.AppendLine($"  跳过试编码（不在编码器列表中）");
                }
            }

            log.AppendLine($"\nGPU 选项: {string.Join(", ", list.Select(e => e.Value))}");
            log.AppendLine($"已验证编码器: {string.Join(", ", validated)}");
            log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === 检测结束 ===");
            WriteEncoderLog(log);
            return (list, validated);
        }

        private static void WriteEncoderLog(System.Text.StringBuilder log)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "encoder_detect.log");
                File.WriteAllText(logPath, log.ToString(), System.Text.Encoding.UTF8);
            }
            catch { }
        }

        private void StopRecording()
        {
            if (!IsRecording) return;

            // 1. 立即设置状态，停止数据流入
            IsRecording = false;
            _isScanning = false;
            _delayBeforeZooming = false;
            _isZooming = false;
            _autoStopWarned = false;
            _maxDurationWarned = false;

            // 2. 将当前的录制资源抓取到局部变量，然后立即清空全局引用
            // 这一步是关键：防止新录制启动时还在用旧的 CTS 或 Queue
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

            // 3. 通知旧队列停止接收数据
            try { oldQueue?.CompleteAdding(); } catch { }
            oldCts?.Cancel();

            // 4. 获取必要的元数据用于保存数据库
            var filePath = _currentVideoFilePath;
            var videoCodec = _currentVideoCodec;
            var videoEncoder = _currentVideoEncoder;
            var recordStart = _recordStartTime;
            var orderId = _recordingOrderId;
            var mode = _recordingMode;
            var stopReason = _stopReason;
            var scanRecord = _currentScanRecord;

            _recordStartTime = DateTime.MinValue;
            _currentScanRecord = null;
            _currentVideoFilePath = null;
            _currentVideoCodec = null;
            _currentVideoEncoder = null;

            // 5. 后台执行收尾，但不再阻塞主线程
            _lastFinalizeTask = Task.Run(() =>
            {
                try
                {
                    // 给旧进程最多 5 秒收尾时间，如果收不掉就强制杀掉
                    oldWriteTask?.Wait(5000);
                }
                catch { }

                FinalizeRecording(oldWriteTask, oldCts, oldQueue,
                    filePath, videoCodec, videoEncoder, recordStart, orderId, mode, stopReason, scanRecord);
            });

            // 录制中修改过摄像头配置，延迟到现在生效
            if (_pendingCameraRestart)
            {
                _pendingCameraRestart = false;
                RestartCamera();
                ShowToast("摄像头配置已生效");
            }
        }

        /// <summary>后台完成录制收尾：等待 FFmpeg 退出、清理资源、写入数据库</summary>
        private void FinalizeRecording(Task writeTask, CancellationTokenSource cts, BlockingCollection<Mat> queue,
            string filePath, string videoCodec, string videoEncoder, DateTime recordStart, string orderId, string mode, string stopReason, ScanRecord scanRecord)
        {
            try { writeTask?.Wait(15000); } catch { }
            cts?.Dispose();
            if (queue != null)
            {
                while (queue.TryTake(out var leftover)) leftover?.Dispose();
                queue.Dispose();
            }

            var duration = recordStart.Year > 2000 ? (DateTime.Now - recordStart) : TimeSpan.Zero;
            if (duration.TotalSeconds > 0)
            {
                if (scanRecord != null)
                {
                    _ = Application.Current?.Dispatcher?.InvokeAsync(() =>
                    {
                        scanRecord.Duration = $"{(int)duration.TotalSeconds}s";
                        scanRecord.IsActive = false;
                    });
                }

                try
                {
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        long fileSize = new FileInfo(filePath).Length;
                        long id = _db?.InsertVideoRecord(orderId, mode, videoCodec, videoEncoder, filePath, recordStart) ?? 0;
                        _db?.UpdateVideoRecordOnStop(id, DateTime.Now, duration.TotalSeconds, fileSize, stopReason, videoCodec, videoEncoder);
                    }
                }
                catch { }

                _ = Application.Current?.Dispatcher?.InvokeAsync(() => RefreshTodayStats());
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
                            foreach (var fi in EnumerateVideoFiles(folderPath))
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
            try { _lastFinalizeTask?.Wait(15000); } catch { }
            _cts?.Dispose();
            lock (_videoLock) { _previousCheckFrame?.Dispose(); }
            lock (_speechLock) { _speechSynth?.Dispose(); _speechSynth = null; }
            try { _db?.Dispose(); } catch { }
        }

        private static IEnumerable<FileInfo> EnumerateVideoFiles(string folderPath)
        {
            var dir = new DirectoryInfo(folderPath);
            foreach (var file in dir.EnumerateFiles("*.mkv", SearchOption.AllDirectories))
                yield return file;
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