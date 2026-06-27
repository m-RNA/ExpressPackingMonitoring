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
using ExpressPackingMonitoring.Services;
using System.Drawing;
using System.Drawing.Imaging;
using Windows.Media.SpeechSynthesis;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.ViewModels
{

    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private AppConfig _config;
        private readonly string _configFilePath = AppPaths.ConfigPath;
        private readonly string _dbFilePath = AppPaths.VideoDatabasePath;
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
        private readonly object _audioLock = new object();
        private NAudio.CoreAudioApi.WasapiCapture _audioCapture;
        private NAudio.Wave.WaveFileWriter _audioWriter;
        private BlockingCollection<byte[]> _audioWriteQueue;
        private Task _audioFileWriteTask;
        private bool _audioWriteFailed;
        private bool _audioWriteQueueFullLogged;
        private bool _audioWriteQueueFullReported;
        private bool _audioFailedForCurrentRecording;
        private string _currentAudioFilePath;
        private string _currentAudioLogPath;
        private CancellationTokenSource _audioMonitorCts;
        private Task _audioMonitorTask;
        private volatile bool _audioStopRequested;
        private bool _audioRestarting;
        private DateTime _lastAudioDataAt = DateTime.MinValue;
        private DateTime _lastAudioPacketAt = DateTime.MinValue;
        private DateTime _audioSuppressUntil = DateTime.MinValue;
        private long _audioBytesWritten;
        private short _audioPeakSinceLastCheck;
        private long _audioBytesSinceLastCheck;
        private int _silentAudioCheckCount;
        private int _audioMonitorLogTick;
        private int _audioConvertFailureCount;
        private int _audioSelectedSourceChannel = -1;
        private double _audioResamplePosition;
        private short _audioPreviousSourceSample;
        private bool _audioHasPreviousSourceSample;
        private bool _audioCaptureUnstable;
        private int _audioGapCount;
        private double _audioMaxGapMs;
        private long _audioGapPaddingBytes;

        private Mat _previousCheckFrame = new Mat();
        private readonly Mat _motionCurrentSmall = new Mat();
        private readonly Mat _motionPreviousSmall = new Mat();
        private readonly Mat _motionCurrentGray = new Mat();
        private readonly Mat _motionPreviousGray = new Mat();
        private readonly Mat _motionDiff = new Mat();
        private readonly Mat _motionThreshold = new Mat();
        private BitmapSource _videoFrame;
        private static readonly TimeSpan PreviewFrameInterval = TimeSpan.FromMilliseconds(1000.0 / 12.0);
        private static readonly TimeSpan PreviewFreezeWarnThreshold = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan PreviewFreezeRestartThreshold = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PreviewFreezeRestartCooldown = TimeSpan.FromSeconds(30);
        private DateTime _lastPreviewFrameAt = DateTime.MinValue;
        private DateTime _lastPreviewPublishedAt = DateTime.MinValue;
        private DateTime _lastPreviewFreezeLogAt = DateTime.MinValue;
        private DateTime _lastPreviewWatchdogRestartAt = DateTime.MinValue;
        private DateTime _lastRecordingQueueWarnAt = DateTime.MinValue;
        private int _previewUpdatePending;
        private CancellationTokenSource _cts;

        // 摄像头空闲休眠
        private bool _isCameraSleeping = false;
        private DateTime _lastActivityTime = DateTime.Now;
        public bool IsCameraSleeping { get => _isCameraSleeping; private set => SetProperty(ref _isCameraSleeping, value); }
        private Task _videoTask;
        private object _videoLock = new object();

        // 摄像头重连控制
        private volatile bool _isRestartingCamera = false;
        private volatile bool _cameraEverConnected = false; // 摄像头是否曾经成功连接过（区分启动vs断连）
        private DateTime _lastRestartAttempt = DateTime.MinValue;
        private int _consecutiveRestartFailures = 0;
        private const int MaxConsecutiveRestartFailures = 5;
        private const double MinRestartIntervalSeconds = 3.0;

        private readonly SemaphoreSlim _recorderLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _mkvConvertLock = new SemaphoreSlim(1, 1);
        private bool _isInputOnCooldown = false;
        private string _pendingScanDuringCooldown = "";
        private Process _currentFfmpegProcess;
        private TaskCompletionSource<long> _firstRecordingFrameWritten;
        private long _recordingStartTimestamp;
        private bool _isDisposed = false; // 新增：防止销毁后操作 UI
        private WebServer _webServer;
        private GlobalKeyboardHook _globalKeyHook;

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

        private string _busyText = "";
        public string BusyText { get => _busyText; set => SetProperty(ref _busyText, value); }
        // ====================================

        private SpeechService _speechService;

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
        public string TotalPackTimeStr => $"{(int)_totalPackTime.TotalHours:D2}:{_totalPackTime.Minutes:D2}";
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

        private volatile bool _suppressVideoPreviewUpdates;
        public bool SuppressVideoPreviewUpdates { get => _suppressVideoPreviewUpdates; set => _suppressVideoPreviewUpdates = value; }
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
            _speechService = new SpeechService
            {
                EnableSoundPrompt = Config.EnableSoundPrompt,
                EnableAiTts = Config.EnableAiTts,
                AiTtsEngine = Config.AiTtsEngine,
                AiTtsSpeakerId = Config.AiTtsSpeakerId,
                AiTtsWarningSpeakerId = Config.AiTtsWarningSpeakerId,
                AiTtsSpeed = Config.AiTtsSpeed,
                EdgeTtsVoice = Config.EdgeTtsVoice,
                EdgeTtsWarningVoice = Config.EdgeTtsWarningVoice
            };
            _speechService.UpdateBreakWords(Config.TtsBreakWords);
            if (Config.EnableAiTts)
                _speechService.InitAiTts();
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
            InitGlobalKeyboardHook();
        }

        private void InitGlobalKeyboardHook()
        {
            _globalKeyHook = new GlobalKeyboardHook();
            _globalKeyHook.BarcodeScanned += OnGlobalBarcodeScanned;
            if (Config.EnableGlobalKeyboard)
                _globalKeyHook.Start();
        }

        private void OnGlobalBarcodeScanned(string barcode)
        {
            if (_isDisposed) return;
            HandleScan(barcode);
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
                var todayList = _db?.GetAggregatedStats(DateTime.Today, DateTime.Today, "day");
                if (todayList != null && todayList.Count > 0)
                {
                    var today = todayList[0];
                    TotalPieces = today.TotalPieces;
                    _totalPackTime = TimeSpan.FromSeconds(today.TotalDurationSec);
                }
                else
                {
                    TotalPieces = 0;
                    _totalPackTime = TimeSpan.Zero;
                }
                OnPropertyChanged(nameof(TotalPackTimeStr)); OnPropertyChanged(nameof(AveragePackTime));
            }
            catch { }
        }

        private void ToggleMode() { CurrentMode = CurrentMode == "发货" ? "退货" : "发货"; ShowToast($"已切换为: {CurrentMode}"); Speak(CurrentMode == "发货" ? "切换发货" : "切换退货"); }

        private void PauseSpeechForRecording() => _speechService?.PauseForRecording();

        private void ResumeSpeechWhenCameraIdle()
        {
            if (_speechService == null || _isDisposed) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(800);
                    if (_isDisposed || IsRecording || IsBusy) return;
                    _speechService.ResumeAfterRecording();
                }
                catch { }
            });
        }

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
                    PauseSpeechForRecording();
                    await InternalStopRecordingAsync();
                    CurrentOrderId = "";
                    ScanInputText = "";
                    ShowToast("已手动停止录制");
                    Speak("停止录制", cancelPrevious: false);
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

                    Debug.WriteLine($"[Zoom] 手动开启录制触发缩放: ID={CurrentOrderId}, Delay={Config.ZoomDelaySeconds}");

                    await InternalStartRecordingAsync();
                    ScanInputText = ""; // 启动录制后清空

                    // 没有输入单号时语音提示（不打断"开始录制"，排队等播完后再警告）
                    if (CurrentOrderId.StartsWith("MAN_"))
                    {
                        SpeakWarning("没有单号", 3, cancelPrevious: false);
                    }

                    // 语音播报完成后再暂停，避免"开始录制"被延迟
                    if (IsRecording)
                        PauseSpeechForRecording();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ToggleRecording] 严重异常: {ex.Message}");
            }
            finally 
            { 
                if (!IsRecording)
                    ResumeSpeechWhenCameraIdle();
                _recorderLock.Release(); 
            }
        }

        private async void HandleScan(string scanResult)
        {
            NotifyUserActivity();
            if (IsBusy || _isDisposed) { ScanInputText = ""; return; }
            if (string.IsNullOrWhiteSpace(scanResult)) return;
            string upperResult = scanResult.ToUpper().Trim();
            
            // 立即清空扫码框，防止重复触发
            ScanInputText = ""; 

            if (_isInputOnCooldown)
            {
                if (IsOrderScan(upperResult))
                {
                    _pendingScanDuringCooldown = upperResult;
                    RuntimeLog.Info("Scan", $"Scan queued during cooldown: {upperResult}");
                    ShowToast("扫码过快，已保留最后一个单号");
                }
                return;
            }

            // 指令处理
            if (upperResult.Contains("CLEAR") || upperResult.Contains("清除")) { ShowToast("提示：扫码框已清除"); return; }
            if (upperResult.Contains("SHIP") || upperResult.Contains("发货") || upperResult.Contains("FAHUO")) { CurrentMode = "发货"; StartInputCooldown(); ShowToast("切换为发货模式"); Speak("切换发货"); return; }
            if (upperResult.Contains("BACK") || upperResult.Contains("退货") || upperResult.Contains("TUIHUO")) { CurrentMode = "退货"; StartInputCooldown(); ShowToast("切换为退货模式"); Speak("切换退货"); return; }
            if (upperResult.Contains("START") || upperResult.Contains("开始录制")) { ToggleRecording(); return; }
            if (upperResult.Contains("STOP") || upperResult.Contains("停止录制")) { _ = SafeStopRecordingAsync(true); return; }

            // 正则验证
            try { if (!System.Text.RegularExpressions.Regex.IsMatch(upperResult, Config.OrderIdRegex)) { ShowToast("非法单号，已拦截"); SpeakWarning("非法单号"); return; } } catch { }

            Debug.WriteLine($"[Zoom] 扫码事件触发: ID={upperResult}, ZoomEnabled={Config.EnableSmartZoom}, Delay={Config.ZoomDelaySeconds}");
            StartInputCooldown();
            CurrentOrderId = upperResult;
            if (IsRecording) _stopReason = "扫码切换";

            if (!await _recorderLock.WaitAsync(0)) return;
            try
            {
                // 扫码切换：立即打断上一轮可能还在播放的语音（如"重复单号"×3）
                _speechService?.Stop();
                if (IsRecording)
                {
                    PauseSpeechForRecording();
                    await InternalStopRecordingAsync();
                }
                await InternalStartRecordingAsync();

                // 录制已启动、数据库记录已写入，此时检查重复单号（排除刚刚插入的当前记录）
                bool isDuplicate = _db != null && _db.OrderIdExistsRecent(upperResult, excludeRecordId: _currentRecordId);
                if (isDuplicate)
                {
                    ShowToast("警告：重复单号，请确认");
                    SpeakWarning("重复单号", 3, cancelPrevious: false);
                }

                // 查询快递助手推送的订单信息，播报留言/备注/商品
                if (Config.EnableOrderInfoLog)
                    System.Diagnostics.Debug.WriteLine($"[OrderInfo] 扫码查询: {upperResult}, EnableAnnounce={Config.EnableOrderInfoAnnounce}, WebServer={(_webServer != null ? "已启动" : "未启动")}");
                if (Config.EnableOrderInfoAnnounce && _webServer != null)
                {
                    var orderInfo = _webServer.GetOrderInfo(upperResult);
                    if (Config.EnableOrderInfoLog)
                        System.Diagnostics.Debug.WriteLine($"[OrderInfo] 查询结果: {(orderInfo != null ? $"命中 买家=[{orderInfo.BuyerMessage}] 卖家=[{orderInfo.SellerMemo}] 商品=[{orderInfo.ProductInfo}]" : "未命中")}");
                    if (orderInfo != null)
                    {
                        if (Config.AnnounceBuyerMessage && !string.IsNullOrWhiteSpace(orderInfo.BuyerMessage))
                        {
                            Speak($"买家留言，{orderInfo.BuyerMessage}", cancelPrevious: false);
                        }
                        if (Config.AnnounceSellerMemo && !string.IsNullOrWhiteSpace(orderInfo.SellerMemo))
                        {
                            Speak($"卖家备注，{orderInfo.SellerMemo}", cancelPrevious: false);
                        }
                        if (Config.AnnounceProductInfo && !string.IsNullOrWhiteSpace(orderInfo.ProductInfo))
                        {
                            Speak($"商品，{orderInfo.ProductInfo}", cancelPrevious: false);
                        }
                    }
                }

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

                // 语音播报完成后再暂停，避免"开始录制"和订单信息被延迟
                if (IsRecording)
                    PauseSpeechForRecording();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HandleScan] 严重异常: {ex.Message}");
            }
            finally
            {
                if (!IsRecording)
                    ResumeSpeechWhenCameraIdle();
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
            string pending = _pendingScanDuringCooldown;
            _pendingScanDuringCooldown = "";
            if (!string.IsNullOrWhiteSpace(pending) && !_isDisposed)
            {
                RuntimeLog.Info("Scan", $"Processing queued scan after cooldown: {pending}");
                HandleScan(pending);
            }
        }

        private bool IsOrderScan(string upperResult)
        {
            if (string.IsNullOrWhiteSpace(upperResult)) return false;
            if (upperResult.Contains("CLEAR") || upperResult.Contains("清除")) return false;
            if (upperResult.Contains("SHIP") || upperResult.Contains("发货") || upperResult.Contains("FAHUO")) return false;
            if (upperResult.Contains("BACK") || upperResult.Contains("退货") || upperResult.Contains("TUIHUO")) return false;
            if (upperResult.Contains("START") || upperResult.Contains("开始录制")) return false;
            if (upperResult.Contains("STOP") || upperResult.Contains("停止录制")) return false;
            try { return System.Text.RegularExpressions.Regex.IsMatch(upperResult, Config.OrderIdRegex); }
            catch { return false; }
        }

        private async Task SafeStopRecordingAsync(bool isManual = false)
        {
            if (IsBusy || !IsRecording || _isDisposed) return;
            if (!await _recorderLock.WaitAsync(0)) return;
            try
            {
                PauseSpeechForRecording();
                await InternalStopRecordingAsync();
                if (isManual)
                {
                    CurrentOrderId = "";
                    ScanInputText = "";
                    ShowToast("已手动停止录制");
                    Speak("停止录制", cancelPrevious: false);
                }
            }
            finally
            {
                if (!IsRecording)
                    ResumeSpeechWhenCameraIdle();
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
            if (Config.AudioSyncOffsetMs == 400)
                Config.AudioSyncOffsetMs = 0;
            Config.AudioSyncOffsetMs = Math.Clamp(Config.AudioSyncOffsetMs, -5000, 5000);
            if (string.IsNullOrWhiteSpace(Config.AiTtsEngine))
                Config.AiTtsEngine = "Edge";
            if (string.IsNullOrWhiteSpace(Config.EdgeTtsVoice))
                Config.EdgeTtsVoice = "zh-CN-XiaoxiaoNeural";
            if (string.IsNullOrWhiteSpace(Config.EdgeTtsWarningVoice))
                Config.EdgeTtsWarningVoice = "zh-CN-YunxiNeural";
            
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
        private void SaveConfig() { try { File.WriteAllText(_configFilePath, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })); } catch { } }

        public void OpenSettings()
        {
            if (_isEncoderDetectRunning)
            {
                ShowToast("处理中：编码器环境检测中，请稍后打开设置...");
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
                    bool globalKeyChanged = Config.EnableGlobalKeyboard != clonedConfig.EnableGlobalKeyboard;
                    bool aiTtsChanged = Config.EnableAiTts != clonedConfig.EnableAiTts
                        || Config.AiTtsEngine != clonedConfig.AiTtsEngine;

                    Config = clonedConfig; 
                    SaveConfig(); 

                    // 同步语音服务配置
                    if (_speechService != null)
                    {
                        _speechService.EnableSoundPrompt = Config.EnableSoundPrompt;
                        _speechService.EnableAiTts = Config.EnableAiTts;
                        _speechService.AiTtsEngine = Config.AiTtsEngine;
                        _speechService.AiTtsSpeakerId = Config.AiTtsSpeakerId;
                        _speechService.AiTtsWarningSpeakerId = Config.AiTtsWarningSpeakerId;
                        _speechService.AiTtsSpeed = Config.AiTtsSpeed;
                        _speechService.EdgeTtsVoice = Config.EdgeTtsVoice;
                        _speechService.EdgeTtsWarningVoice = Config.EdgeTtsWarningVoice;
                        _speechService.UpdateBreakWords(Config.TtsBreakWords);
                        if (aiTtsChanged && Config.EnableAiTts && !_speechService.IsAiTtsAvailable)
                            _speechService.InitAiTts();
                    }

                    if (themeChanged)
                    {
                        if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(Config.Theme, out var themeEnum))
                        {
                            ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
                        }
                    }
                    ForceCheckDiskAndCleanup();

                    if (globalKeyChanged && _globalKeyHook != null)
                    {
                        if (Config.EnableGlobalKeyboard)
                            _globalKeyHook.Start();
                        else
                            _globalKeyHook.Stop();
                    }

                    if (cameraChanged)
                    {
                        if (IsRecording)
                        {
                            ShowToast("提示：配置已保存，摄像头配置将在录制结束后生效");
                            _pendingCameraRestart = true;
                        }
                        else
                        {
                            ShowToast("提示：配置已保存，重启相机");
                            _consecutiveRestartFailures = 0;
                            RestartCamera();
                        }
                    }
                    else
                    {
                        ShowToast("提示：配置已保存");
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
                        DeleteAudioTempFile(Path.ChangeExtension(mkvPath, ".wav"));
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
                    DeleteAudioTempFile(Path.ChangeExtension(mkvPath, ".wav"));
                    _db.UpdateVideoFilePath(mkvPath, mp4Path);
                    success++;
                    progress?.Report($"[{i + 1}/{total}] MP4 已存在，已清理 MKV: {fileName}");
                    continue;
                }

                progress?.Report($"[{i + 1}/{total}] 正在转换: {fileName}");

                bool ok = await Task.Run(() =>
                {
                    var result = ConvertMkvToMp4ForPlayback(mkvPath);
                    if (!result.Success)
                        RuntimeLog.Warn("MkvRecover", $"Convert failed file={fileName}, error={result.ErrorMessage}");
                    return result.Success;
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
            RuntimeLog.Info("System", "InitializeSystem");
            StartCamera();
            _videoTask = Task.Run(() => VideoProcessLoop(_cts.Token), _cts.Token);
            Task.Run(CheckDiskAndCleanup);
            Task.Run(CameraIdleWatchdog);
            StartWebServer();

            // 启动时自动将上次断电残留的 MKV 转换为 MP4
            Task.Run(RecoverOrphanedMkvAsync);
        }

        private async Task RecoverOrphanedMkvAsync()
        {
            try
            {
                var result = await BatchConvertMkvToMp4Async(
                    new Progress<string>(msg => Debug.WriteLine($"[MkvRecover] {msg}")),
                    CancellationToken.None);

                if (result.success > 0 || result.fail > 0)
                {
                    Debug.WriteLine($"[MkvRecover] 启动恢复完成: 成功={result.success}, 失败={result.fail}, 跳过={result.skip}");
                    if (result.success > 0)
                    {
                        _ = Application.Current.Dispatcher.BeginInvoke(() =>
                            ShowToast($"已恢复 {result.success} 个断电残留视频"));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MkvRecover] 异常: {ex.Message}");
            }
        }

        private void StartWebServer()
        {
            if (!Config.EnableWebServer || _db == null) return;
            try
            {
                _webServer = new WebServer(_db, Config.WebServerPort, Config.TranscodeCacheMaxMB, () => IsRecording, ConvertRecordMkvToMp4);
                _webServer.EnableOrderInfoLog = Config.EnableOrderInfoLog;
                _webServer.OrderInfoReceived += OnOrderInfoReceived;
                _webServer.Start();
                Debug.WriteLine($"[Web] 局域网服务已启动 http://0.0.0.0:{Config.WebServerPort}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Web] 启动失败: {ex.Message}");
                ShowToast($"警告：局域网服务启动失败: {ex.Message}");
            }
        }

        /// <summary>收到油猴脚本推送的订单信息时，提前生成 TTS 缓存</summary>
        private void OnOrderInfoReceived(List<OrderInfo> orders)
        {
            if (_speechService == null || !Config.EnableOrderInfoAnnounce) return;
            foreach (var info in orders)
            {
                if (Config.AnnounceBuyerMessage && !string.IsNullOrWhiteSpace(info.BuyerMessage))
                    _speechService.PreGenerateCache($"买家留言，{info.BuyerMessage}");
                if (Config.AnnounceSellerMemo && !string.IsNullOrWhiteSpace(info.SellerMemo))
                    _speechService.PreGenerateCache($"卖家备注，{info.SellerMemo}");
                if (Config.AnnounceProductInfo && !string.IsNullOrWhiteSpace(info.ProductInfo))
                    _speechService.PreGenerateCache($"商品，{info.ProductInfo}");
            }
        }

        private void RestartCamera()
        {
            // 阻止并发重启
            if (_isRestartingCamera) return;
            _isRestartingCamera = true;
            try
            {
                RuntimeLog.Warn("Camera", $"RestartCamera start recording={IsRecording}, failures={_consecutiveRestartFailures}");
                StopCamera();
                StartCamera();
                _lastRestartAttempt = DateTime.Now;
                RuntimeLog.Info("Camera", $"RestartCamera done running={_videoSource?.IsRunning == true}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Camera", "RestartCamera failed", ex);
                throw;
            }
            finally
            {
                _isRestartingCamera = false;
            }
        }

        private async void RestartCameraWithRecordingStop()
        {
            if (_isRestartingCamera) return;
            _isRestartingCamera = true;
            try
            {
                RuntimeLog.Warn("Camera", $"RestartCameraWithRecordingStop start recording={IsRecording}, failures={_consecutiveRestartFailures}");
                if (IsRecording)
                {
                    // 录制中：先尝试不停止录制的重连
                    StopCamera();
                    StartCamera();
                    _lastRestartAttempt = DateTime.Now;

                    if (_videoSource != null && _videoSource.IsRunning)
                    {
                        _consecutiveRestartFailures = 0;
                        RuntimeLog.Info("Camera", "Camera reconnected while recording, recording continues");
                        ShowToast("成功：摄像头已重连，录制继续");
                        Speak("摄像头已连接");
                    }
                    else
                    {
                        _consecutiveRestartFailures++;
                        if (_consecutiveRestartFailures >= MaxConsecutiveRestartFailures)
                        {
                            // 多次重连失败，停止录制
                            _stopReason = "摄像头断连";
                            RuntimeLog.Warn("Camera", $"Camera reconnect failed {_consecutiveRestartFailures} times while recording, stopping recording");
                            await SafeStopRecordingAsync();
                            ShowToast($"警告：摄像头连续 {MaxConsecutiveRestartFailures} 次重连失败，录制已停止。请重新插拔后在设置中手动重启。");
                            SpeakWarning("请重新连接摄像头", 3);
                            Debug.WriteLine($"[Camera] 录制中连续 {_consecutiveRestartFailures} 次重连失败，停止录制和自动重连");
                        }
                        else
                        {
                            SpeakWarning("摄像头断开，正在尝试连接");
                        }
                    }
                }
                else
                {
                    // 非录制状态：原有逻辑
                    StopCamera();
                    StartCamera();
                    _lastRestartAttempt = DateTime.Now;

                    if (_videoSource != null && _videoSource.IsRunning)
                    {
                        _consecutiveRestartFailures = 0;
                        RuntimeLog.Info("Camera", "Camera reconnected while idle");
                    }
                    else
                    {
                        _consecutiveRestartFailures++;
                        RuntimeLog.Warn("Camera", $"Camera reconnect failed while idle, failures={_consecutiveRestartFailures}");
                        if (_consecutiveRestartFailures >= MaxConsecutiveRestartFailures)
                        {
                            ShowToast($"警告：摄像头连续 {MaxConsecutiveRestartFailures} 次重连失败，已停止自动重连。请重新插拔后在设置中手动重启。");
                            SpeakWarning("请重新连接摄像头", 3);
                            Debug.WriteLine($"[Camera] 连续 {_consecutiveRestartFailures} 次重连失败，停止自动重连");
                        }
                    }
                }
            }
            finally
            {
                _isRestartingCamera = false;
            }
        }

        /// <summary>用户手动触发摄像头重置（在设置或 UI 按钮调用）</summary>
        public void ManualRestartCamera()
        {
            _consecutiveRestartFailures = 0;
            RestartCamera();
        }

        /// <summary>
        /// 注册用户活跃信号（扫码/鼠标/键盘/按钮等），如果摄像头休眠中则唤醒
        /// </summary>
        public void NotifyUserActivity()
        {
            _lastActivityTime = DateTime.Now;
            if (_isCameraSleeping)
            {
                IsCameraSleeping = false;
                _consecutiveRestartFailures = 0;
                StartCamera();
                ShowToast("摄像头已唤醒");
                Speak("摄像头已唤醒");
                Debug.WriteLine("[Idle] 用户活跃，摄像头唤醒");
            }
            else if (_consecutiveRestartFailures >= MaxConsecutiveRestartFailures)
            {
                // 用户活动时如果摄像头已停止自动重连，重置并再试一次
                _consecutiveRestartFailures = 0;
                Debug.WriteLine("[Camera] 用户活动，重置重连计数器并重试");
                RestartCamera();
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
                        ShowToast($"提示：摄像头已休眠（空闲{Config.CameraIdleMinutes}分钟）");
                        Speak("摄像头已休眠");
                        Debug.WriteLine($"[Idle] 摄像头休眠: 空闲{idleMinutes:F1}分钟");
                        RuntimeLog.Info("MkvRecover", "Camera idle, start pending MKV conversion");
                        Task.Run(RecoverOrphanedMkvAsync);
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
                    RuntimeLog.Warn("Camera", "StartCamera found no video devices");
                    ShowToast("警告：未检测到任何摄像头");
                    SpeakWarning("未检测到摄像头");
                    return; 
                }
                
                string targetMoniker = Config.CameraMonikerString;
                int targetIndex = -1;

                // 1. 优先通过 MonikerString 查找（精确匹配目标设备）
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

                    // 目标摄像头已配置但未找到：不切换到其他设备
                    if (targetIndex == -1)
                    {
                        Debug.WriteLine($"[Camera] 目标摄像头未找到: {targetMoniker}，不切换到其他设备");
                        RuntimeLog.Warn("Camera", $"Configured camera missing, moniker={targetMoniker}");
                        ShowToast("警告：目标摄像头未连接，等待重新插入");
                        return;
                    }
                }

                // 2. 首次使用（未配置 MonikerString）：使用索引选择并记录 MonikerString
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
                RuntimeLog.Info("Camera", $"StartCamera selected index={targetIndex}, name={videoDevices[targetIndex].Name}");
                
                // 加载该摄像头的独立配置
                if (Config.CameraConfigs.TryGetValue(videoDevices[targetIndex].MonikerString, out var settings))
                {
                    Config.FrameWidth = settings.FrameWidth;
                    Config.FrameHeight = settings.FrameHeight;
                    Config.Fps = settings.Fps;
                    Config.AudioDeviceName = settings.AudioDeviceName ?? "";
                    Config.AudioSyncOffsetMs = settings.AudioSyncOffsetMs;
                }

                // 设置错误处理器（摄像头拔掉时 AForge 会触发此事件）
                _videoSource.VideoSourceError += (s, e) => {
                    Debug.WriteLine($"[Camera] 视频源错误: {e.Description}");
                    RuntimeLog.Error("Camera", $"VideoSourceError: {e.Description}");
                    _ = Application.Current.Dispatcher.InvokeAsync(() => {
                        ShowToast("警告：摄像头连接发生错误，尝试重连...");
                        RestartCameraWithRecordingStop();
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
                _lastFrameTime = DateTime.Now; // 防止 VideoProcessLoop 启动时误判无帧
                _lastPreviewPublishedAt = DateTime.Now;
                _cameraEverConnected = true;
                RuntimeLog.Info("Camera", $"StartCamera success {Config.FrameWidth}x{Config.FrameHeight}@{_actualCameraFps}, running={_videoSource.IsRunning}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Camera", "StartCamera failed", ex);
                ShowToast("摄像头启动失败");
            }
        }

        private void StopCamera()
        {
            if (_videoSource != null)
            {
                RuntimeLog.Info("Camera", $"StopCamera running={_videoSource.IsRunning}");
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
            RuntimeLog.Info("Camera", "StopCamera completed");
        }
        
        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs) { try { Mat newMat = BitmapToMat(eventArgs.Frame); lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = newMat; } _lastFrameTime = DateTime.Now; } catch (Exception ex) { RuntimeLog.Error("Camera", "NewFrame conversion failed", ex); } }

        private Mat BitmapToMat(Bitmap bitmap)
        {
            if (bitmap.PixelFormat == PixelFormat.Format24bppRgb)
            {
                var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
                var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, bitmap.PixelFormat);
                try
                {
                    return Mat.FromPixelData(bitmap.Height, bitmap.Width, MatType.CV_8UC3, bmpData.Scan0, bmpData.Stride).Clone();
                }
                finally
                {
                    bitmap.UnlockBits(bmpData);
                }
            }

            using var solidBitmap = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
            using (Graphics gr = Graphics.FromImage(solidBitmap))
                gr.DrawImage(bitmap, new System.Drawing.Rectangle(0, 0, solidBitmap.Width, solidBitmap.Height));

            var solidRect = new System.Drawing.Rectangle(0, 0, solidBitmap.Width, solidBitmap.Height);
            var solidData = solidBitmap.LockBits(solidRect, ImageLockMode.ReadOnly, solidBitmap.PixelFormat);
            try
            {
                return Mat.FromPixelData(solidBitmap.Height, solidBitmap.Width, MatType.CV_8UC3, solidData.Scan0, solidData.Stride).Clone();
            }
            finally
            {
                solidBitmap.UnlockBits(solidData);
            }
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

                    // 检测摄像头是否已断开：_latestFrame 是旧帧不会自动清除，必须用 _lastFrameTime 判断
                    if (currentFrame != null && _cameraEverConnected && !_isCameraSleeping)
                    {
                        double sinceLastNewFrame = (DateTime.Now - _lastFrameTime).TotalSeconds;
                        if (sinceLastNewFrame > 1.5)
                        {
                            currentFrame.Dispose();
                            currentFrame = null;
                            lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = null; }
                        }
                    }

                    if (currentFrame != null && !currentFrame.Empty())
                    {

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
                                var size1 = Cv2.GetTextSize(line1, HersheyFonts.HersheySimplex, fontScale, thickness, out _);
                                int x1 = processedFrame.Width - size1.Width - 15;
                                int y1 = lineHeight;
                                
                                Cv2.PutText(processedFrame, line1, new OpenCvSharp.Point(x1, y1),
                                    HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 0, 0), thickness + 2, LineTypes.AntiAlias);
                                Cv2.PutText(processedFrame, line1, new OpenCvSharp.Point(x1, y1),
                                    HersheyFonts.HersheySimplex, fontScale, new Scalar(255, 255, 255), thickness, LineTypes.AntiAlias);
                                
                                // 第二行：单号（录制中或有当前单号时显示）
                                string orderId = IsRecording ? _recordingOrderId : CurrentOrderId;
                                if (!string.IsNullOrEmpty(orderId))
                                {
                                    string line2 = $"Order:{orderId}";
                                    var size2 = Cv2.GetTextSize(line2, HersheyFonts.HersheySimplex, fontScale, thickness, out _);
                                    int x2 = processedFrame.Width - size2.Width - 15;
                                    int y2 = y1 + (int)(lineHeight * 1.1);
                                    
                                    Cv2.PutText(processedFrame, line2, new OpenCvSharp.Point(x2, y2),
                                        HersheyFonts.HersheySimplex, fontScale, new Scalar(0, 0, 0), thickness + 2, LineTypes.AntiAlias);
                                    Cv2.PutText(processedFrame, line2, new OpenCvSharp.Point(x2, y2),
                                        HersheyFonts.HersheySimplex, fontScale, new Scalar(255, 255, 255), thickness, LineTypes.AntiAlias);
                                }
                            }
                            catch { }
                        }

                        PublishPreviewFrameIfDue(processedFrame);
                        if (frameTickCounter % 30 == 0) PerformMotionDetection(currentFrame);

                        bool handedToRecorder = IsRecording && TryEnqueueFrameForRecording(processedFrame);
                        if (processedFrame != currentFrame)
                        {
                            if (!handedToRecorder) processedFrame.Dispose();
                            currentFrame.Dispose();
                        }
                        else if (!handedToRecorder)
                        {
                            currentFrame.Dispose();
                        }

                        CheckPreviewWatchdog();
                    }
                    else
                    {
                        // 休眠期间不做任何自动重连操作
                        if (_isCameraSleeping)
                        {
                        }
                        // 如果已达重连上限或正在重启中，不再尝试
                        else if (_isRestartingCamera || _consecutiveRestartFailures >= MaxConsecutiveRestartFailures)
                        {
                        }
                        // 冷却期间不尝试重连（退避机制）
                        else if ((DateTime.Now - _lastRestartAttempt).TotalSeconds < MinRestartIntervalSeconds * Math.Max(1, _consecutiveRestartFailures))
                        {
                        }
                        // 摄像头掉线检测：使用时间差（避免 200ms 循环间隔导致帧计数不准）
                        else if (_videoSource != null && _videoSource.IsRunning)
                        {
                            double noFrameSeconds = (DateTime.Now - _lastFrameTime).TotalSeconds;
                            if (noFrameSeconds > 1.5)
                            {
                                Debug.WriteLine($"[Camera] 信号丢失 {noFrameSeconds:F1}s，尝试重连 (失败次数={_consecutiveRestartFailures})");
                                _ = Application.Current.Dispatcher.InvokeAsync(() => {
                                    ShowToast("警告：摄像头信号丢失，尝试重连...");
                                    SpeakWarning("摄像头重新连接中");
                                    RestartCameraWithRecordingStop();
                                });
                            }
                        }
                        else if (_cameraEverConnected)
                        {
                            // 摄像头曾连接过但现在不可用（断连/拔掉）：持续尝试重连
                            double missingSeconds = (DateTime.Now - _lastFrameTime).TotalSeconds;
                            if (missingSeconds > 2.0)
                            {
                                Debug.WriteLine($"[Camera] 摄像头断开，尝试重连 (失败次数={_consecutiveRestartFailures})");
                                _ = Application.Current.Dispatcher.InvokeAsync(() => {
                                    ShowToast("警告：摄像头已断开，等待重新连接...");
                                    SpeakWarning("摄像头重新连接中");
                                    RestartCameraWithRecordingStop();
                                });
                            }
                        }

                        // 无帧时降低循环频率，避免空转 CPU
                        await Task.Delay(200, token);
                        frameTickCounter++;
                        continue;
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
                                ShowToast("提示：已达最大录像限制时长");
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
            catch (Exception ex)
            {
                RuntimeLog.Error("VideoProcess", "VideoProcessLoop crashed", ex);
                throw;
            }
        }

        private void CheckPreviewWatchdog()
        {
            if (_isDisposed || _isCameraSleeping || SuppressVideoPreviewUpdates) return;
            if (_videoSource == null || !_videoSource.IsRunning || !_cameraEverConnected) return;
            if (_lastFrameTime == DateTime.MinValue || _lastPreviewPublishedAt == DateTime.MinValue) return;

            DateTime now = DateTime.Now;
            TimeSpan sinceLastFrame = now - _lastFrameTime;
            TimeSpan sinceLastPreview = now - _lastPreviewPublishedAt;

            if (sinceLastFrame > PreviewFreezeWarnThreshold)
            {
                if (now - _lastPreviewFreezeLogAt > PreviewFreezeWarnThreshold)
                {
                    _lastPreviewFreezeLogAt = now;
                    RuntimeLog.Warn("Preview", $"No new camera frame for {sinceLastFrame.TotalSeconds:F1}s, preview age={sinceLastPreview.TotalSeconds:F1}s, recording={IsRecording}");
                }
                return;
            }

            if (sinceLastPreview < PreviewFreezeWarnThreshold) return;

            int queueCount = -1;
            try { queueCount = _videoWriteQueue?.Count ?? -1; } catch { }
            string writeTaskStatus = _writeTask == null ? "null" : _writeTask.Status.ToString();

            if (now - _lastPreviewFreezeLogAt > PreviewFreezeWarnThreshold)
            {
                _lastPreviewFreezeLogAt = now;
                RuntimeLog.Warn("Preview", $"Preview stale for {sinceLastPreview.TotalSeconds:F1}s while frames are fresh ({sinceLastFrame.TotalSeconds:F1}s), pending={_previewUpdatePending}, recording={IsRecording}, queue={queueCount}, writeTask={writeTaskStatus}");
            }

            if (sinceLastPreview < PreviewFreezeRestartThreshold) return;
            if (_isRestartingCamera) return;
            if (now - _lastPreviewWatchdogRestartAt < PreviewFreezeRestartCooldown) return;

            _lastPreviewWatchdogRestartAt = now;
            RuntimeLog.Warn("Preview", $"Preview frozen for {sinceLastPreview.TotalSeconds:F1}s, restarting camera. recording={IsRecording}, queue={queueCount}, writeTask={writeTaskStatus}");
            Interlocked.Exchange(ref _previewUpdatePending, 0);
            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_isDisposed || _isCameraSleeping || SuppressVideoPreviewUpdates) return;
                ShowToast("警告：预览画面卡住，正在重连摄像头...");
                RestartCameraWithRecordingStop();
            });
        }

        private static double SmoothStep(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return t * t * (3 - 2 * t);
        }

        private void PublishPreviewFrameIfDue(Mat frame)
        {
            if (SuppressVideoPreviewUpdates || _isDisposed) return;

            DateTime now = DateTime.UtcNow;
            if (now - _lastPreviewFrameAt < PreviewFrameInterval) return;

            if (Interlocked.CompareExchange(ref _previewUpdatePending, 1, 0) != 0) return;
            _lastPreviewFrameAt = now;

            try
            {
                var bitmap = frame.ToWriteableBitmap();
                bitmap.Freeze();

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    Interlocked.Exchange(ref _previewUpdatePending, 0);
                    return;
                }

                _ = dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!_isDisposed && !SuppressVideoPreviewUpdates)
                        {
                            VideoFrame = bitmap;
                            _lastPreviewPublishedAt = DateTime.Now;
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _previewUpdatePending, 0);
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch
            {
                Interlocked.Exchange(ref _previewUpdatePending, 0);
            }
        }

        private void PerformMotionDetection(Mat currentFrame)
        {
            if (_previousCheckFrame.Empty()) { currentFrame.CopyTo(_previousCheckFrame); _lastMotionTime = DateTime.Now; return; }
            var motionSize = new OpenCvSharp.Size(320, 240);
            Cv2.Resize(currentFrame, _motionCurrentSmall, motionSize);
            Cv2.Resize(_previousCheckFrame, _motionPreviousSmall, motionSize);
            Cv2.CvtColor(_motionCurrentSmall, _motionCurrentGray, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor(_motionPreviousSmall, _motionPreviousGray, ColorConversionCodes.BGR2GRAY);
            Cv2.Absdiff(_motionCurrentGray, _motionPreviousGray, _motionDiff);
            Cv2.Threshold(_motionDiff, _motionThreshold, Config.MotionDetectThreshold, 255, ThresholdTypes.Binary);
            double changeRatio = (double)Cv2.CountNonZero(_motionThreshold) / (_motionThreshold.Width * _motionThreshold.Height);
            if (changeRatio > 0.01) { _lastMotionTime = DateTime.Now; }
            currentFrame.CopyTo(_previousCheckFrame);
        }

        private bool TryEnqueueFrameForRecording(Mat frame)
        {
            try
            {
                if (frame == null || frame.IsDisposed) return false;
                if (_writeTask != null && _writeTask.IsCompleted) return false;

                var queue = _videoWriteQueue;
                bool added = queue != null && !queue.IsAddingCompleted && queue.TryAdd(frame, 5);
                if (!added && DateTime.Now - _lastRecordingQueueWarnAt > TimeSpan.FromSeconds(5))
                {
                    _lastRecordingQueueWarnAt = DateTime.Now;
                    RuntimeLog.Warn("Recording", $"Video frame enqueue failed, queueNull={queue == null}, addingCompleted={queue?.IsAddingCompleted}, queueCount={queue?.Count}, writeTask={_writeTask?.Status}");
                }
                return added;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Recording", "Video frame enqueue exception", ex);
                return false;
            }
        }



                private void Speak(string text, bool cancelPrevious = true) => _speechService?.Speak(text, cancelPrevious);
        private void SpeakWarning(string text, int repeatCount = 1, bool cancelPrevious = true) => _speechService?.SpeakWarning(text, repeatCount, cancelPrevious);

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _cts?.Cancel();
            _stopReason = "程序退出";

            string videoFileToConvert = null;
            string audioFileToConvert = null;
            string audioLogFileToUse = null;
            bool audioFailedForRecording = false;
            long audioBytesWrittenForRecording = 0;
            long recordId = 0;
            DateTime recordStart = DateTime.MinValue;

            try
            {
                if (IsRecording)
                {
                    videoFileToConvert = _currentVideoFilePath;
                    audioLogFileToUse = _currentAudioLogPath;
                    recordId = _currentRecordId;
                    recordStart = _recordStartTime;

                    _videoWriteQueue?.CompleteAdding();
                    _writeCts?.Cancel();
                    audioFileToConvert = StopAudioRecording();
                    audioFailedForRecording = _audioFailedForCurrentRecording;
                    audioBytesWrittenForRecording = _audioBytesWritten;
                    _writeTask?.Wait(5000); // 等待写入线程关闭 stdin，让 FFmpeg 正常结束

                    // 如果 FFmpeg 还没退出，再等一会儿让它写完尾部
                    if (_currentFfmpegProcess != null && !_currentFfmpegProcess.HasExited)
                    {
                        if (!_currentFfmpegProcess.WaitForExit(3000))
                        {
                            try { _currentFfmpegProcess.Kill(); } catch { }
                        }
                    }

                    IsRecording = false;
                }
            }
            catch { }

            // 等待上一次的 finalize 任务完成
            try { _lastFinalizeTask?.Wait(5000); } catch { }

            // 录制中退出：更新数据库并转换 MP4
            if (!string.IsNullOrEmpty(videoFileToConvert) && File.Exists(videoFileToConvert))
            {
                try
                {
                    long fileSize = new FileInfo(videoFileToConvert).Length;
                    if (fileSize >= 1024 * 50 && recordId > 0)
                    {
                        int durSec = Math.Max(1, (int)(DateTime.Now - recordStart).TotalSeconds);
                        _db?.UpdateVideoRecordOnStop(recordId, DateTime.Now, durSec, fileSize, _stopReason, _currentVideoCodec, _currentVideoEncoder);
                        RuntimeLog.Info("Recording", $"Exit finalized MKV, queued for startup/web conversion: {Path.GetFileName(videoFileToConvert)}");
                    }
                    else
                    {
                        DeleteAudioTempFile(audioFileToConvert);
                    }
                }
                catch { }
            }

            StopCamera();
            try { _videoTask?.Wait(1000); } catch { }
            _cts?.Dispose();
            lock (_videoLock)
            {
                _previousCheckFrame?.Dispose();
                _motionCurrentSmall?.Dispose();
                _motionPreviousSmall?.Dispose();
                _motionCurrentGray?.Dispose();
                _motionPreviousGray?.Dispose();
                _motionDiff?.Dispose();
                _motionThreshold?.Dispose();
            }
            _speechService?.Dispose();
            _speechService = null;
            try { _globalKeyHook?.Dispose(); } catch { }
            try { _webServer?.Dispose(); } catch { }
            try { _db?.Dispose(); } catch { }
        }
    }
}
