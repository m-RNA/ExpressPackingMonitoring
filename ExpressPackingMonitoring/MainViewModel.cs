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
        public string OrderIdRegex { get; set; } = "^[a-zA-Z0-9]{6,30}$";
        public bool EnableSoundPrompt { get; set; } = true;
        public double TimeoutWarningSeconds { get; set; } = 10.0;
        public string Theme { get; set; } = "Auto";
        public bool ShowDeletedVideos { get; set; } = true;
        public bool AutoStartOnBoot { get; set; } = false;
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
        private int _cachedFourCC = 0; // 启动时探测并缓存可用编码器

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
        private long _currentVideoRecordId;    // 数据库中当前录制记录 ID
        private string _currentVideoFilePath;  // 当前录制文件路径
        private string _stopReason = "手动";     // 停止录制的原因
        private bool _autoStopWarned = false;
        private bool _maxDurationWarned = false;

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
            InitDatabase();
            RefreshTodayStats();
            InitSpeechSynthesizer();
            ScanCommand = new RelayCommand<string>(HandleScan);
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            OpenPlaybackCommand = new RelayCommand(OpenPlaybackWindow);
            ToggleModeCommand = new RelayCommand(ToggleMode);
            ToggleRecordingCommand = new RelayCommand(ToggleRecording);
            OpenStatsCommand = new RelayCommand(OpenStatsWindow);
            InitializeSystem();
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
            if (IsRecording) { ShowToast("录制中，请先停止录像再修改设置"); Speak("请先停止录像"); return; }
            try
            {
                var clonedConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
                var settingsWin = new SettingsWindow(clonedConfig, DiskUsagePercent, DiskUsageText);
                if (Application.Current?.MainWindow != null) settingsWin.Owner = Application.Current.MainWindow;
                if (settingsWin.ShowDialog() == true) { 
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
                    ForceCheckDiskAndCleanup(); ShowToast("⚙️ 配置已保存，重启相机"); RestartCamera(); 
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
            ProbeVideoCodec();  // 启动时一次性探测可用编码器
            _cts = new CancellationTokenSource();
            StartCamera();
            _videoTask = Task.Run(() => VideoProcessLoop(_cts.Token), _cts.Token);
            Task.Run(CheckDiskAndCleanup);
        }

        /// <summary>
        /// 参照 OBS 做法：启动时用独立临时文件探测可用编码器，避免录制时反复创建/销毁 VideoWriter 泄漏 FFmpeg 句柄。
        /// </summary>
        private void ProbeVideoCodec()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "ExpressPackingMonitoring_probe");
            try { Directory.CreateDirectory(testDir); } catch { return; }

            // 优先 mp4v（兼容性最好），再试 avc1、hevc
            int[] codecs = {
                FourCC.MP4V,
                VideoWriter.FourCC('a', 'v', 'c', '1'),
                VideoWriter.FourCC('h', 'e', 'v', 'c'),
            };

            foreach (var codec in codecs)
            {
                // 使用配置的分辨率进行真实环境下的编码器能力探测
                int probeW = Math.Max(640, Config.FrameWidth);
                int probeH = Math.Max(480, Config.FrameHeight);

                // 每个编码器用单独的临时文件，互不干扰
                string testFile = Path.Combine(testDir, $"probe_{codec}.mp4");
                VideoWriter testWriter = null;
                try
                {
                    testWriter = new VideoWriter(testFile, codec, 15, new OpenCvSharp.Size(probeW, probeH));
                    if (testWriter.IsOpened())
                    {
                        // 写入 2 帧验证编码器真正可用
                        using var black = new Mat(probeH, probeW, MatType.CV_8UC3, Scalar.All(0));
                        testWriter.Write(black);
                        testWriter.Write(black);
                        _cachedFourCC = codec;
                    }
                }
                catch { }
                finally
                {
                    // 同一线程创建和释放，保证 FFmpeg 状态干净
                    try { testWriter?.Release(); } catch { }
                    try { testWriter?.Dispose(); } catch { }
                }
                if (_cachedFourCC != 0) break;
            }

            // 清理临时目录
            try { Directory.Delete(testDir, true); } catch { }
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
                if (_videoSource.VideoCapabilities.Length > 0) _videoSource.VideoResolution = _videoSource.VideoCapabilities[0];
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
            double frameDurationMs = 1000.0 / (Config.Fps > 0 ? Config.Fps : 15);
            int frameTickCounter = 0;

            try
            {
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

            try { if (!System.Text.RegularExpressions.Regex.IsMatch(upperResult, Config.OrderIdRegex)) { ScanInputText = ""; ShowToast("非法单号，已拦截"); return; } } catch { }

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

            if (_cachedFourCC == 0)
            {
                IsRecording = false;
                ShowToast("⚠ 无可用视频编码器，无法录制");
                Speak("编码器不可用");
                return;
            }

            lock (_videoLock)
            {
                _writeCts = new CancellationTokenSource();
                // 优化内存占用：缓冲区调小至 60 帧（15FPS 约 4 秒缓冲），节省 600MB 内存
                _videoWriteQueue = new BlockingCollection<Mat>(boundedCapacity: 60);
                // 参照 OBS：VideoWriter 在写入线程内创建/使用/销毁，保证同一线程生命周期
                _writeTask = Task.Run(() => BackgroundVideoWriterLoop(filePath, _writeCts.Token));
            }

            ShowToast("▶ 开始录像");
            Speak("开始录制");
            // 设置 IsActive 为 true，触发红色高亮
            _currentScanRecord = new ScanRecord(CurrentOrderId, "0s", DateTime.Now.ToString("HH:mm:ss"), CurrentMode, true);
            AddRecord(_currentScanRecord);
        }

        /// <summary>
        /// 参照 OBS 做法：VideoWriter 的创建、写入、Release/Dispose 全部在同一线程完成，
        /// 避免跨线程操作导致 FFmpeg 原生句柄泄漏。
        /// </summary>
        private void BackgroundVideoWriterLoop(string filePath, CancellationToken token)
        {
            VideoWriter writer = null;
            bool writerOk = false;
            try
            {
                var size = new OpenCvSharp.Size(Config.FrameWidth, Config.FrameHeight);
                writer = new VideoWriter(filePath, _cachedFourCC, Config.Fps, size);

                if (!writer.IsOpened())
                {
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        IsRecording = false;
                        ShowToast("⚠ 视频编码器打开失败");
                    });
                    return;
                }
                writerOk = true;

                // 使用无 token 的枚举，让 CompleteAdding 自然排空队列中所有帧
                foreach (var frame in _videoWriteQueue.GetConsumingEnumerable())
                {
                    // 仅在排空后用 token 作为强制中断的兜底
                    if (token.IsCancellationRequested) { frame?.Dispose(); break; }

                    if (frame != null && !frame.IsDisposed)
                    {
                        if (frame.Width != size.Width || frame.Height != size.Height)
                        {
                            using var resized = new Mat();
                            Cv2.Resize(frame, resized, size);
                            writer.Write(resized);
                        }
                        else { writer.Write(frame); }
                        frame.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                // 同一线程释放，保证 FFmpeg muxer 正确写入 trailer
                try { writer?.Release(); } catch { }
                try { writer?.Dispose(); } catch { }

                // 如果 writer 创建失败，删除 0KB 空文件
                if (!writerOk)
                {
                    try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                }
            }
        }

        private void StopRecording()
        {
            if (!IsRecording) return;
            IsRecording = false; _isScanning = false; _delayBeforeZooming = false; _isZooming = false;
            _autoStopWarned = false; _maxDurationWarned = false;

            lock (_videoLock)
            {
                // CompleteAdding → 写入线程自然排空队列所有帧 → Release/Dispose writer（同一线程）
                _videoWriteQueue?.CompleteAdding();
                try { _writeTask?.Wait(8000); } catch { }
                // 兜底：如果排空超时，强制中断
                _writeCts?.Cancel();
                try { _writeTask?.Wait(2000); } catch { }
                _writeCts?.Dispose(); _writeCts = null;

                // 清理队列中因强制中断而未消费的残留帧
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