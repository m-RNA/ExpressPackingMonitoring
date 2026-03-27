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
using Windows.Media.SpeechSynthesis;
using System.Media;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.ViewModels
{

    public partial class MainViewModel : ObservableObject, IDisposable
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

        // 摄像头空闲休眠
        private bool _isCameraSleeping = false;
        private DateTime _lastActivityTime = DateTime.Now;
        public bool IsCameraSleeping { get => _isCameraSleeping; private set => SetProperty(ref _isCameraSleeping, value); }
        private Task _videoTask;
        private object _videoLock = new object();

        private readonly SemaphoreSlim _recorderLock = new SemaphoreSlim(1, 1);
        private bool _isInputOnCooldown = false;
        private Process _currentFfmpegProcess;
        private bool _isDisposed = false; // 新增：防止销毁后操作 UI
        private WebServer _webServer;

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

        private string _busyText = "";
        public string BusyText { get => _busyText; set => SetProperty(ref _busyText, value); }
        // ====================================

        private Windows.Media.SpeechSynthesis.SpeechSynthesizer _ttsNormal;
        private Windows.Media.SpeechSynthesis.SpeechSynthesizer _ttsWarning;
        private SoundPlayer _soundPlayer;
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
        private enum ZoomPhase { None, ZoomingIn, Holding, ZoomingOut }
        private ZoomPhase _zoomPhase = ZoomPhase.None;
        private DateTime _zoomPhaseStartTime;
        private bool _delayBeforeZooming = false;

        private ScanRecord _currentScanRecord;
        private long _currentRecordId; 
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

        private System.Windows.Rect _lastZoomRect;
        public System.Windows.Rect LastZoomRect { get => _lastZoomRect; private set => SetProperty(ref _lastZoomRect, value); }

        private System.Windows.Size _cameraFrameSize;
        public System.Windows.Size CameraFrameSize { get => _cameraFrameSize; private set => SetProperty(ref _cameraFrameSize, value); }

        private double? _previewZoomScale;
        public double? PreviewZoomScale { get => _previewZoomScale; set => SetProperty(ref _previewZoomScale, value); }

        private bool _isZoomingActive;
        public bool IsZoomingActive { get => _isZoomingActive; private set => SetProperty(ref _isZoomingActive, value); }

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
        public ICommand OpenStatsCommand { get; } // 打开统计面板
        public ICommand ResetEncoderDetectCommand { get; } // 重置编码器检测

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
            catch (Exception ex)
            {
                MessageBox.Show($"数据库初始化失败，部分功能将不可用：{ex.Message}", "启动警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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

        // ========================== 核心逻辑：恢复 MAN_ 前缀 ==========================
        private async void ToggleRecording() 
        {
            NotifyUserActivity();
            if (IsBusy || _isDisposed) return;
            if (!await _recorderLock.WaitAsync(0)) return; 

            try 
            {
                if (IsRecording) 
                {
                    await InternalStopRecordingAsync();
                    CurrentOrderId = "";
                    ScanInputText = "";
                    ShowToast("已手动停止录制");
                    Speak("停止录制");
                    return;
                }
                else 
                {
                    // 恢复逻辑：如果扫码框为空，使用 MAN_ 前缀
                    string input = ScanInputText?.Trim().ToUpper() ?? "";
                    if (string.IsNullOrWhiteSpace(input))
                    {
                        CurrentOrderId = $"MAN_{DateTime.Now:HHmmss}";
                    }
                    else
                    {
                        CurrentOrderId = input;
                    }
                    
                    // 手动触发录制时也开启缩放逻辑
                    _lastScanTime = DateTime.Now;
                    _isScanning = true;
                    _delayBeforeZooming = Config.ZoomDelaySeconds > 0;
                    if (!_delayBeforeZooming)
                    {
                        _zoomPhase = ZoomPhase.ZoomingIn;
                        _zoomPhaseStartTime = DateTime.Now;
                        LastZoomRect = System.Windows.Rect.Empty;
                        IsZoomingActive = true;
                    }

                    // 没有输入单号时语音提示
                    if (CurrentOrderId.StartsWith("MAN_"))
                    {
                        SpeakWarning("没有单号", 5);
                    }

                    Debug.WriteLine($"[Zoom] 手动开启录制触发缩放: ID={CurrentOrderId}, Delay={Config.ZoomDelaySeconds}");

                    await InternalStartRecordingAsync();
                    ScanInputText = ""; // 启动录制后清空
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ToggleRecording] 严重异常: {ex.Message}");
            }
            finally 
            { 
                _recorderLock.Release(); 
            }
        }

        private async void HandleScan(string scanResult)
        {
            NotifyUserActivity();
            if (IsBusy || _isDisposed) { ScanInputText = ""; return; }
            if (_isInputOnCooldown) { ScanInputText = ""; return; }

            if (string.IsNullOrWhiteSpace(scanResult)) return;
            string upperResult = scanResult.ToUpper().Trim();
            
            // 立即清空扫码框，防止重复触发
            ScanInputText = ""; 

            // 指令处理
            if (upperResult.Contains("CLEAR") || upperResult.Contains("清除")) { ShowToast("🧹 扫码框已清除"); return; }
            if (upperResult.Contains("SHIP") || upperResult.Contains("发货")) { CurrentMode = "发货"; StartInputCooldown(); ShowToast("切换为发货模式"); Speak("切换发货"); return; }
            if (upperResult.Contains("BACK") || upperResult.Contains("退货")) { CurrentMode = "退货"; StartInputCooldown(); ShowToast("切换为退货模式"); Speak("切换退货"); return; }
            if (upperResult.Contains("START") || upperResult.Contains("开始录制")) { ToggleRecording(); return; }
            if (upperResult.Contains("STOP") || upperResult.Contains("停止录制")) { _ = SafeStopRecordingAsync(true); return; }

            // 正则验证
            try { if (!System.Text.RegularExpressions.Regex.IsMatch(upperResult, Config.OrderIdRegex)) { ShowToast("非法单号，已拦截"); SpeakWarning("非法单号"); return; } } catch { }

            // 重复单号检测（查数据库今日记录）
            if (_db != null && _db.OrderIdExistsToday(upperResult))
            {
                ShowToast("⚠ 重复单号，请确认");
                SpeakWarning("重复单号");
            }

            Debug.WriteLine($"[Zoom] 扫码事件触发: ID={upperResult}, ZoomEnabled={Config.EnableSmartZoom}, Delay={Config.ZoomDelaySeconds}");
            StartInputCooldown();
            CurrentOrderId = upperResult;
            if (IsRecording) _stopReason = "扫码切换";

            if (!await _recorderLock.WaitAsync(0)) return;
            try
            {
                if (IsRecording) await InternalStopRecordingAsync();
                await InternalStartRecordingAsync();

                // 在录制停止/启动之后设置缩放状态（InternalStopRecordingAsync 会重置缩放状态）
                _lastScanTime = DateTime.Now;
                _isScanning = true;
                _delayBeforeZooming = Config.ZoomDelaySeconds > 0;
                if (!_delayBeforeZooming)
                {
                    _zoomPhase = ZoomPhase.ZoomingIn;
                    _zoomPhaseStartTime = DateTime.Now;
                    LastZoomRect = System.Windows.Rect.Empty;
                    IsZoomingActive = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HandleScan] 严重异常: {ex.Message}");
            }
            finally
            {
                _recorderLock.Release();
            }
        }

        private async void StartInputCooldown()
        {
            if (_isInputOnCooldown) return;
            _isInputOnCooldown = true;
            double cooldownMs = Config.BarcodeCooldownSeconds * 1000;
            HideBarcode1Temporarily();
            HideBarcode2Temporarily();
            await Task.Delay((int)cooldownMs);
            _isInputOnCooldown = false;
        }

        private async Task SafeStopRecordingAsync(bool isManual = false)
        {
            if (IsBusy || !IsRecording || _isDisposed) return;
            if (!await _recorderLock.WaitAsync(0)) return;
            try
            {
                await InternalStopRecordingAsync();
                if (isManual)
                {
                    CurrentOrderId = "";
                    ScanInputText = "";
                    ShowToast("已手动停止录制");
                    Speak("停止录制");
                }
            }
            finally
            {
                _recorderLock.Release();
            }
        }
        // =======================================================================

        // 录制、编码、磁盘清理逻辑已移动到 MainViewModel.Recording.cs / MainViewModel.Encoder.cs / MainViewModel.Cleanup.cs

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

        private void OpenPlaybackWindow()
        {
            string folderPath;
            try
            {
                folderPath = ResolveBestStoragePath();
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法访问存储路径：{ex.Message}", "存储错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

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

        /// <summary>
        /// 批量将数据库中的旧 MKV 文件转换为 MP4（无损容器转换）
        /// </summary>
        public async Task<(int success, int fail, int skip)> BatchConvertMkvToMp4Async(IProgress<string> progress, CancellationToken token)
        {
            if (_db == null) return (0, 0, 0);

            string ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                progress?.Report("未找到 FFmpeg，无法执行转换");
                return (0, 0, 0);
            }

            var mkvPaths = _db.QueryMkvFilePaths();
            int success = 0, fail = 0, skip = 0;
            int total = mkvPaths.Count;

            for (int i = 0; i < total; i++)
            {
                if (token.IsCancellationRequested) break;

                string mkvPath = mkvPaths[i];
                string mp4Path = Path.ChangeExtension(mkvPath, ".mp4");
                string fileName = Path.GetFileName(mkvPath);

                // 如果 MKV 已不存在但 MP4 存在，只更新数据库
                if (!File.Exists(mkvPath))
                {
                    if (File.Exists(mp4Path))
                    {
                        _db.UpdateVideoFilePath(mkvPath, mp4Path);
                        success++;
                        progress?.Report($"[{i + 1}/{total}] 已更新数据库: {fileName}");
                    }
                    else
                    {
                        skip++;
                        progress?.Report($"[{i + 1}/{total}] 文件不存在，跳过: {fileName}");
                    }
                    continue;
                }

                // 如果 MP4 已存在，直接删 MKV 并更新数据库
                if (File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0)
                {
                    try { File.Delete(mkvPath); } catch { }
                    _db.UpdateVideoFilePath(mkvPath, mp4Path);
                    success++;
                    progress?.Report($"[{i + 1}/{total}] MP4 已存在，已清理 MKV: {fileName}");
                    continue;
                }

                progress?.Report($"[{i + 1}/{total}] 正在转换: {fileName}");

                bool ok = await Task.Run(() =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = ffmpegPath,
                            Arguments = $"-y -i \"{mkvPath}\" -vcodec copy -acodec copy \"{mp4Path}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true,
                            RedirectStandardOutput = true
                        };

                        using var process = Process.Start(psi);
                        if (process == null) return false;
                        process.StandardError.ReadToEnd();
                        process.WaitForExit();
                        return process.ExitCode == 0 && File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0;
                    }
                    catch { return false; }
                }, token);

                if (ok)
                {
                    try { File.Delete(mkvPath); } catch { }
                    _db.UpdateVideoFilePath(mkvPath, mp4Path);
                    success++;
                    progress?.Report($"[{i + 1}/{total}] 转换成功: {fileName}");
                }
                else
                {
                    try { if (File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }
                    fail++;
                    progress?.Report($"[{i + 1}/{total}] 转换失败: {fileName}");
                }
            }

            return (success, fail, skip);
        }

        private void InitializeSystem()
        {
            _cts = new CancellationTokenSource();
            _lastActivityTime = DateTime.Now;
            StartCamera();
            _videoTask = Task.Run(() => VideoProcessLoop(_cts.Token), _cts.Token);
            Task.Run(CheckDiskAndCleanup);
            Task.Run(CameraIdleWatchdog);
            StartWebServer();
        }

        private void StartWebServer()
        {
            if (!Config.EnableWebServer || _db == null) return;
            try
            {
                _webServer = new WebServer(_db, Config.WebServerPort, Config.TranscodeCacheMaxMB);
                _webServer.Start();
                Debug.WriteLine($"[Web] 局域网服务已启动 http://0.0.0.0:{Config.WebServerPort}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Web] 启动失败: {ex.Message}");
                ShowToast($"⚠ 局域网服务启动失败: {ex.Message}");
            }
        }

        private void RestartCamera() { _ = SafeStopRecordingAsync(); StopCamera(); StartCamera(); }

        /// <summary>
        /// 注册用户活跃信号（扫码/鼠标/键盘/按钮等），如果摄像头休眠中则唤醒
        /// </summary>
        public void NotifyUserActivity()
        {
            _lastActivityTime = DateTime.Now;
            if (_isCameraSleeping)
            {
                IsCameraSleeping = false; // SetProperty 会同时更新字段并触发 PropertyChanged
                StartCamera();
                ShowToast("摄像头已唤醒");
                Speak("摄像头已唤醒");
                Debug.WriteLine("[Idle] 用户活跃，摄像头唤醒");
            }
        }

        private async void CameraIdleWatchdog()
        {
            while (!_isDisposed)
            {
                await Task.Delay(10_000); // 每10秒检查一次
                if (_isDisposed) break;
                if (!Config.EnableCameraIdle || Config.CameraIdleMinutes <= 0) continue;
                if (IsRecording || _isCameraSleeping) continue;

                double idleMinutes = (DateTime.Now - _lastActivityTime).TotalMinutes;
                if (idleMinutes >= Config.CameraIdleMinutes)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => {
                        if (_isCameraSleeping || IsRecording) return; // 再次检查防止竞态
                        IsCameraSleeping = true; // SetProperty 会同时更新字段并触发 PropertyChanged
                        StopCamera();
                        VideoFrame = null;
                        ShowToast($"💤 摄像头已休眠（空闲{Config.CameraIdleMinutes}分钟）");
                        Speak("摄像头已休眠");
                        Debug.WriteLine($"[Idle] 摄像头休眠: 空闲{idleMinutes:F1}分钟");
                    });
                }
            }
        }

        private DateTime _lastFrameTime = DateTime.MinValue;

        private void StartCamera()
        {
            try
            {
                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (videoDevices.Count == 0) 
                { 
                    ShowToast("⚠ 未检测到任何摄像头"); 
                    SpeakWarning("未检测到摄像头");
                    return; 
                }
                
                string targetMoniker = Config.CameraMonikerString;
                int targetIndex = -1;

                // 1. 优先通过 MonikerString 查找
                if (!string.IsNullOrEmpty(targetMoniker))
                {
                    for (int i = 0; i < videoDevices.Count; i++)
                    {
                        if (videoDevices[i].MonikerString == targetMoniker)
                        {
                            targetIndex = i;
                            break;
                        }
                    }
                }

                // 2. 如果没找到，尝试通过索引 (回退逻辑)
                if (targetIndex == -1)
                {
                    if (Config.CameraIndex >= 0 && Config.CameraIndex < videoDevices.Count)
                    {
                        targetIndex = Config.CameraIndex;
                        Config.CameraMonikerString = videoDevices[targetIndex].MonikerString;
                    }
                    else
                    {
                        targetIndex = 0;
                        Config.CameraMonikerString = videoDevices[0].MonikerString;
                    }
                }

                _videoSource = new VideoCaptureDevice(videoDevices[targetIndex].MonikerString);
                
                // 加载该摄像头的独立配置
                if (Config.CameraConfigs.TryGetValue(videoDevices[targetIndex].MonikerString, out var settings))
                {
                    Config.FrameWidth = settings.FrameWidth;
                    Config.FrameHeight = settings.FrameHeight;
                    Config.Fps = settings.Fps;
                    Config.AudioDeviceName = settings.AudioDeviceName ?? "";
                    Config.AudioSyncOffsetMs = settings.AudioSyncOffsetMs;
                }

                // 设置错误处理器
                _videoSource.VideoSourceError += (s, e) => {
                    Debug.WriteLine($"[Camera] 视频源错误: {e.Description}");
                    _ = Application.Current.Dispatcher.InvokeAsync(() => {
                        ShowToast("⚠ 摄像头连接发生错误");
                    });
                };

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
            int cameraErrorCounter = 0;
            int cameraMissingCounter = 0;

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
                        cameraErrorCounter = 0; // 重置错误计数
                        _lastFrameTime = DateTime.Now;

                        Mat processedFrame = currentFrame;
                        CameraFrameSize = new System.Windows.Size(currentFrame.Width, currentFrame.Height);

                        if (Config.EnableSmartZoom || PreviewZoomScale.HasValue)
                        {
                            double effectiveScale = PreviewZoomScale ?? Config.ZoomScale;
                            int zoomW = (int)(currentFrame.Width / effectiveScale);
                            int zoomH = (int)(currentFrame.Height / effectiveScale);
                            if (zoomW <= 0 || zoomW > currentFrame.Width) zoomW = currentFrame.Width;
                            if (zoomH <= 0 || zoomH > currentFrame.Height) zoomH = currentFrame.Height;

                            var currentZoomRect = new OpenCvSharp.Rect((currentFrame.Width - zoomW) / 2, (currentFrame.Height - zoomH) / 2, zoomW, zoomH)
                                .Intersect(new OpenCvSharp.Rect(0, 0, currentFrame.Width, currentFrame.Height));

                            if (currentZoomRect.Width > 0 && currentZoomRect.Height > 0 && _zoomPhase == ZoomPhase.None)
                            {
                                LastZoomRect = new System.Windows.Rect(currentZoomRect.X, currentZoomRect.Y, currentZoomRect.Width, currentZoomRect.Height);
                            }

                            if (_isScanning)
                            {
                                if (_delayBeforeZooming && (DateTime.Now - _lastScanTime).TotalMilliseconds >= Config.ZoomDelaySeconds * 1000.0)
                                {
                                    _delayBeforeZooming = false;
                                    _zoomPhase = ZoomPhase.ZoomingIn;
                                    _zoomPhaseStartTime = DateTime.Now;
                                    LastZoomRect = System.Windows.Rect.Empty;
                                    IsZoomingActive = true;
                                    Debug.WriteLine($"[Zoom] 缩放触发: Delay={Config.ZoomDelaySeconds}s, Scale={Config.ZoomScale}");
                                }

                                // 根据缩放阶段计算动画倍率
                                double animDuration = Config.EnableZoomAnimation ? Config.ZoomAnimationDurationMs : 0;
                                double animatedScale = 1.0;
                                bool applyZoom = false;

                                if (_zoomPhase == ZoomPhase.ZoomingIn)
                                {
                                    double elapsed = (DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds;
                                    double t = animDuration > 0 ? Math.Min(elapsed / animDuration, 1.0) : 1.0;
                                    animatedScale = 1.0 + (effectiveScale - 1.0) * SmoothStep(t);
                                    applyZoom = true;
                                    if (t >= 1.0)
                                    {
                                        _zoomPhase = ZoomPhase.Holding;
                                        _zoomPhaseStartTime = DateTime.Now;
                                    }
                                }
                                else if (_zoomPhase == ZoomPhase.Holding)
                                {
                                    animatedScale = effectiveScale;
                                    applyZoom = true;
                                    if ((DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds >= Config.ZoomDurationSeconds * 1000.0)
                                    {
                                        _zoomPhase = ZoomPhase.ZoomingOut;
                                        _zoomPhaseStartTime = DateTime.Now;
                                    }
                                }
                                else if (_zoomPhase == ZoomPhase.ZoomingOut)
                                {
                                    double elapsed = (DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds;
                                    double t = animDuration > 0 ? Math.Min(elapsed / animDuration, 1.0) : 1.0;
                                    animatedScale = effectiveScale - (effectiveScale - 1.0) * SmoothStep(t);
                                    applyZoom = true;
                                    if (t >= 1.0)
                                    {
                                        _zoomPhase = ZoomPhase.None;
                                        _isScanning = false;
                                        IsZoomingActive = false;
                                        Debug.WriteLine("[Zoom] 缩放动画结束，恢复原样");
                                    }
                                }

                                if (applyZoom && animatedScale > 1.001)
                                {
                                    int animW = (int)(currentFrame.Width / animatedScale);
                                    int animH = (int)(currentFrame.Height / animatedScale);
                                    if (animW > 0 && animH > 0 && animW <= currentFrame.Width && animH <= currentFrame.Height)
                                    {
                                        var animRect = new OpenCvSharp.Rect(
                                            (currentFrame.Width - animW) / 2, (currentFrame.Height - animH) / 2, animW, animH)
                                            .Intersect(new OpenCvSharp.Rect(0, 0, currentFrame.Width, currentFrame.Height));
                                        if (animRect.Width > 0 && animRect.Height > 0)
                                        {
                                            var zoomed = currentFrame.Clone(animRect);
                                            processedFrame = new Mat();
                                            Cv2.Resize(zoomed, processedFrame, new OpenCvSharp.Size(Config.FrameWidth, Config.FrameHeight));
                                            zoomed.Dispose();
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (LastZoomRect != System.Windows.Rect.Empty) LastZoomRect = System.Windows.Rect.Empty;
                            if (_isScanning)
                            {
                                _isScanning = false;
                                Debug.WriteLine($"[Zoom] 扫码已触发但未执行缩放: EnableSmartZoom={Config.EnableSmartZoom}");
                            }
                        }

                        // 水印叠加：始终在预览画面显示时间，录制时额外显示单号
                        if (Config.EnableWatermark)
                        {
                            try
                            {
                                if (processedFrame == currentFrame)
                                {
                                    processedFrame = currentFrame.Clone();
                                }
                                var now = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(8));
                                string line1 = $"UTC+8: {now:yyyy/MM/dd HH:mm:ss}";
                                double fontScale = Math.Max(0.5, processedFrame.Height / 720.0) * 0.6;
                                int thickness = fontScale >= 0.8 ? 2 : 1;
                                int lineHeight = (int)(30 * fontScale / 0.6);
                                // 第一行：时间
                                Cv2.PutText(processedFrame, line1, new OpenCvSharp.Point(10, lineHeight),
                                    HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 0, 0), thickness + 2, LineTypes.AntiAlias);
                                Cv2.PutText(processedFrame, line1, new OpenCvSharp.Point(10, lineHeight),
                                    HersheyFonts.HersheySimplex, fontScale, new Scalar(255, 255, 255), thickness, LineTypes.AntiAlias);
                                // 第二行：单号（录制中或有当前单号时显示）
                                string orderId = IsRecording ? _recordingOrderId : CurrentOrderId;
                                if (!string.IsNullOrEmpty(orderId))
                                {
                                    string line2 = $"Order:{orderId}";
                                    int y2 = lineHeight + (int)(lineHeight * 1.1);
                                    Cv2.PutText(processedFrame, line2, new OpenCvSharp.Point(10, y2),
                                        HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 0, 0), thickness + 2, LineTypes.AntiAlias);
                                    Cv2.PutText(processedFrame, line2, new OpenCvSharp.Point(10, y2),
                                        HersheyFonts.HersheySimplex, fontScale, new Scalar(255, 255, 255), thickness, LineTypes.AntiAlias);
                                }
                            }
                            catch { }
                        }

                        if (IsRecording && _videoWriteQueue != null && !_videoWriteQueue.IsAddingCompleted)
                        {
                            // === 防熔断检查机制 ===
                            // 如果后台任务意外报错结束，绝对不要继续往队列塞数据
                            if (_writeTask != null && _writeTask.IsCompleted)
                            {
                                // 这种情况下不应该继续填充队列
                                continue;
                            }

                            try
                            {
                                var clone = processedFrame.Clone();
                                // === 防卡死核心机制 ===
                                // 给队列的塞入设定 5ms 极限超时。如果 5ms 放不进去，证明后台卡住了。
                                // 此时宁可抛弃这一帧，也绝对不能让当前的 UI/预览线程被挂起！
                                var queue = _videoWriteQueue;
                                if (queue != null && !queue.IsAddingCompleted && !queue.TryAdd(clone, 5))
                                {
                                    clone.Dispose();
                                }
                            }
                            catch { } // 忽略对象被清理时的异常
                        }

                        var bitmap = processedFrame.ToWriteableBitmap();
                        bitmap.Freeze();
                        _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => { 
                            if (_isDisposed) return;
                            VideoFrame = bitmap; 
                        }));
                    if (frameTickCounter % 30 == 0) PerformMotionDetection(currentFrame);
                        if (processedFrame != currentFrame) processedFrame.Dispose(); currentFrame.Dispose();
                    }
                    else
                    {
                        // 休眠期间不做任何自动重连操作
                        if (_isCameraSleeping)
                        {
                            cameraErrorCounter = 0;
                            cameraMissingCounter = 0;
                        }
                        // 摄像头掉线检测：如果 3秒没帧，且 _videoSource 理论上在运行
                        else if (_videoSource != null && _videoSource.IsRunning)
                        {
                            cameraErrorCounter++;
                            // 约 3秒 (假设 15fps，45帧)
                            if (cameraErrorCounter > (_actualCameraFps * 3))
                            {
                                cameraErrorCounter = 0;
                                _ = Application.Current.Dispatcher.InvokeAsync(() => {
                                    ShowToast("⚠ 摄像头信号丢失，尝试重连...");
                                    RestartCamera();
                                });
                            }
                        }
                        else
                        {
                            // 摄像头完全没启动或已停止：每 5秒尝试发现并启动一次
                            cameraMissingCounter++;
                            if (cameraMissingCounter > (_actualCameraFps * 5))
                            {
                                cameraMissingCounter = 0;
                                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                                if (videoDevices.Count > 0)
                                {
                                    _ = Application.Current.Dispatcher.InvokeAsync(() => {
                                        ShowToast("📷 检测到摄像头，尝试启动...");
                                        RestartCamera();
                                    });
                                }
                            }
                        }
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
                                SpeakWarning("画面即将静止超时");
                            }
                            if (!_maxDurationWarned && Config.EnableMaxDuration
                                && maxDurTotalSec > warnSec * 2
                                && elapsedSec >= maxDurTotalSec - warnSec)
                            {
                                _maxDurationWarned = true;
                                SpeakWarning("录制即将达到最大时长");
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
                        { 
                            _stopReason = "静止超时"; 
                            _ = Application.Current.Dispatcher.InvokeAsync(async () => { 
                                if (_isDisposed) return;
                                await SafeStopRecordingAsync(); 
                                ShowToast("画面静止超时，自动停录"); 
                                SpeakWarning("静止超时，停止录制"); 
                                CurrentOrderId = ""; 
                                ScanInputText = ""; 
                            }); 
                        }

                        if (!inGracePeriod && Config.EnableMaxDuration && elapsedSec >= Config.MaxDurationMinutes * 60.0)
                        { 
                            _stopReason = "时长超时"; 
                            _ = Application.Current.Dispatcher.InvokeAsync(async () => { 
                                if (_isDisposed) return;
                                await SafeStopRecordingAsync(); 
                                ShowToast("⏳ 已达最大录像限制时长"); 
                                SpeakWarning("时长超时，停止录制"); 
                                CurrentOrderId = ""; 
                                ScanInputText = ""; 
                            }); 
                        }
                    }

                    frameTickCounter++;
                    int sleepMs = (int)Math.Max(0, frameDurationMs - (DateTime.Now - startTime).TotalMilliseconds);
                    if (sleepMs > 0) await Task.Delay(sleepMs, token);
                }
            }
            catch (OperationCanceledException) { }
        }

        private static double SmoothStep(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return t * t * (3 - 2 * t);
        }

        private void PerformMotionDetection(Mat currentFrame)
        {
            if (_previousCheckFrame.Empty()) { currentFrame.CopyTo(_previousCheckFrame); _lastMotionTime = DateTime.Now; return; }
            using var cGray = currentFrame.Resize(new OpenCvSharp.Size(320, 240)).CvtColor(ColorConversionCodes.BGR2GRAY);
            using var pGray = _previousCheckFrame.Resize(new OpenCvSharp.Size(320, 240)).CvtColor(ColorConversionCodes.BGR2GRAY);
            using var diff = new Mat(); using var thresh = new Mat();
            Cv2.Absdiff(cGray, pGray, diff); Cv2.Threshold(diff, thresh, Config.MotionDetectThreshold, 255, ThresholdTypes.Binary);
            double changeRatio = (double)Cv2.CountNonZero(thresh) / (thresh.Width * thresh.Height);
            if (changeRatio > 0.01) { _lastMotionTime = DateTime.Now; }
            currentFrame.CopyTo(_previousCheckFrame);
        }



                /// <summary>
        /// 根据用户设置解析最终要使用的编码器。
        /// GpuEncoder 存储 GPU 偏好（"auto"/"nvidia"/"amd"/"intel"/"cpu"），
        /// 结合 VideoCodec（"h264"/"h265"）动态映射为具体编码器。
        /// </summary>
        private void InitSpeechSynthesizer()
        {
            try
            {
                var voices = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices;
                var femaleZh = voices.FirstOrDefault(v => v.Gender == VoiceGender.Female && v.Language == "zh-CN");
                var maleZh = voices.FirstOrDefault(v => v.Gender == VoiceGender.Male && v.Language == "zh-CN");
                var anyZh = femaleZh ?? maleZh ?? voices.FirstOrDefault(v => v.Language.StartsWith("zh"));

                _ttsNormal = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
                if (femaleZh != null) _ttsNormal.Voice = femaleZh;
                else if (anyZh != null) _ttsNormal.Voice = anyZh;

                _ttsWarning = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
                if (maleZh != null) _ttsWarning.Voice = maleZh;
                else if (anyZh != null) _ttsWarning.Voice = anyZh;

                _soundPlayer = new SoundPlayer();
            }
            catch { _ttsNormal = null; _ttsWarning = null; }
        }

        private MemoryStream SynthesizeToMemoryStream(Windows.Media.SpeechSynthesis.SpeechSynthesizer synth, string text)
        {
            var result = synth.SynthesizeTextToStreamAsync(text).AsTask().GetAwaiter().GetResult();
            var ms = new MemoryStream();
            result.AsStreamForRead().CopyTo(ms);
            ms.Position = 0;
            return ms;
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
                        _soundPlayer?.Stop();
                        if (_ttsNormal == null) return;
                        var ms = SynthesizeToMemoryStream(_ttsNormal, text);
                        _soundPlayer.Stream = ms;
                        _soundPlayer.Play();
                    }
                    catch { }
                }
            });
        }

        /// <summary>
        /// 警告/错误语音：使用男声 + 同步阻塞播放，确保不会被后续普通语音打断。
        /// </summary>
        private void SpeakWarning(string text, int repeatCount = 1)
        {
            if (!Config.EnableSoundPrompt) return;
            Task.Run(() =>
            {
                lock (_speechLock)
                {
                    try
                    {
                        _soundPlayer?.Stop();
                        if (_ttsWarning == null) return;
                        string fullText = repeatCount > 1
                            ? string.Join("，", Enumerable.Repeat(text, repeatCount))
                            : text;
                        var ms = SynthesizeToMemoryStream(_ttsWarning, fullText);
                        _soundPlayer.Stream = ms;
                        _soundPlayer.PlaySync(); // 同步阻塞直到播完
                    }
                    catch { }
                }
            });
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _cts?.Cancel();
            _stopReason = "程序退出";

            try
            {
                if (IsRecording)
                {
                    _videoWriteQueue?.CompleteAdding();
                    _writeCts?.Cancel();
                    _writeTask?.Wait(2000);
                    if (_currentFfmpegProcess != null && !_currentFfmpegProcess.HasExited)
                    {
                        try { _currentFfmpegProcess.Kill(); } catch { }
                    }
                }
            }
            catch { }

            StopCamera();
            try { _videoTask?.Wait(1000); } catch { }
            _cts?.Dispose();
            lock (_videoLock) { _previousCheckFrame?.Dispose(); }
            lock (_speechLock)
            {
                _soundPlayer?.Stop();
                _soundPlayer?.Dispose(); _soundPlayer = null;
                _ttsNormal?.Dispose(); _ttsNormal = null;
                _ttsWarning?.Dispose(); _ttsWarning = null;
            }
            try { _webServer?.Dispose(); } catch { }
            try { _db?.Dispose(); } catch { }
        }
    }
}