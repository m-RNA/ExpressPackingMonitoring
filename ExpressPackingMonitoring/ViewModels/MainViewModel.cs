#nullable disable
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Localization;
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
        private Task _cameraForceStopTask;
        private Mat _latestFrame;
        private readonly object _frameLock = new object();

        private BlockingCollection<Mat> _videoWriteQueue;
        private Task _writeTask;
        private Task _lastFinalizeTask;
        private Task _mkvRecoveryTask;
        private Task _postStopMuxTask;
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
        private WriteableBitmap _previewWriteableBitmap;
        private static readonly TimeSpan PreviewFrameInterval = TimeSpan.FromMilliseconds(1000.0 / 12.0);
        private static readonly TimeSpan PreviewFreezeWarnThreshold = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan PreviewFreezeRestartThreshold = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PreviewFreezeRestartCooldown = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ResourceHealthLogInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan UiHeartbeatStaleThreshold = TimeSpan.FromSeconds(2);
        private DateTime _lastPreviewFrameAt = DateTime.MinValue;
        private DateTime _lastPreviewPublishedAt = DateTime.MinValue;
        private DateTime _lastPreviewFreezeLogAt = DateTime.MinValue;
        private DateTime _lastPreviewWatchdogRestartAt = DateTime.MinValue;
        private DateTime _lastRecordingQueueWarnAt = DateTime.MinValue;
        private DateTime _lastResourceHealthLogAt = DateTime.MinValue;
        private DateTime _lastPreviewConvertErrorLogAt = DateTime.MinValue;
        private DateTime _lastVideoFrameErrorLogAt = DateTime.MinValue;
        private DateTime _lastCameraStateErrorLogAt = DateTime.MinValue;
        private DateTime _lastUiHeartbeatAt = DateTime.Now;
        private System.Windows.Threading.DispatcherTimer _uiHeartbeatTimer;
        private readonly PreviewSessionGate _previewSessionGate = new();
        private readonly CameraFrameReadySignal _cameraFrameReady = new();
        private CancellationTokenSource _cts;

        // 摄像头空闲休眠
        private bool _isCameraSleeping = false;
        private DateTime _lastActivityTime = DateTime.Now;
        public bool IsCameraSleeping { get => _isCameraSleeping; private set => SetProperty(ref _isCameraSleeping, value); }
        private Task _videoTask;
        private object _videoLock = new object();

        // 摄像头重连控制
        private volatile bool _isRestartingCamera = false;
        private volatile bool _isSetupWizardActive = false;
        private volatile bool _cameraEverConnected = false; // 摄像头是否曾经成功连接过（区分启动vs断连）
        private DateTime _lastRestartAttempt = DateTime.MinValue;
        private int _consecutiveRestartFailures = 0;
        private const int MaxConsecutiveRestartFailures = 5;
        private const double MinRestartIntervalSeconds = 3.0;

        private readonly SemaphoreSlim _recorderLock = new SemaphoreSlim(1, 1);
        private sealed class PrintedRefundScanCheck
        {
            public Guid AlertId { get; } = Guid.NewGuid();
            public string TrackingNumber { get; init; } = "";
            public string Mode { get; init; } = "";
            private int _alerted;

            public bool TryMarkAlerted() => Interlocked.Exchange(ref _alerted, 1) == 0;
        }

        private static readonly TimeSpan PrintedRefundLookupInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PrintedRefundLookupTimeout = TimeSpan.FromSeconds(15);
        private readonly object _printedRefundLookupLock = new();
        private readonly List<PrintedRefundScanCheck> _pendingPrintedRefundChecks = new();
        private Task _printedRefundLookupTask;
        private DateTime _lastPrintedRefundLookupUtc = DateTime.MinValue;
        private readonly SemaphoreSlim _mkvConvertLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _shutdownLock = new SemaphoreSlim(1, 1);
        private volatile bool _shutdownRequested;
        private bool _isShutdownInProgress;
        private volatile bool _shutdownPrepared;
        private bool _isInputOnCooldown = false;
        private string _pendingScanDuringCooldown = "";
        private Process _currentFfmpegProcess;
        private TaskCompletionSource<long> _firstRecordingFrameWritten;
        private long _recordingStartTimestamp;
        private bool _isDisposed = false; // 新增：防止销毁后操作 UI
        private WebServer _webServer;
        private readonly SemaphoreSlim _webServerLifecycleLock = new(1, 1);
        private GlobalKeyboardHook _globalKeyHook;
        private CameraBarcodeRecognitionService _cameraBarcodeRecognition;
        private CancellationTokenSource _cameraBarcodeFeedbackCts;

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value) && value)
                    ResetCameraBarcodeRecognition(preserveConfirmedCodes: true);
            }
        }

        private string _busyText = "";
        public string BusyText { get => _busyText; set => SetProperty(ref _busyText, value); }
        // ====================================

        private SpeechService _speechService;
        private AlertService _alertService;

        private string _currentMode = "发货";
        private string _currentOrderId = "";
        private bool _isRecording;
        private string _scanInputText = "";
        public string ScanInputText { get => _scanInputText; set { if (SetProperty(ref _scanInputText, value)) ScheduleRefreshBarcodes(); } }

        private string _cameraBarcodeStatusText = "将面单条形码放入框内";
        private bool _isCameraBarcodeCandidate;
        private bool _isCameraBarcodeConfirmed;
        public string CameraBarcodeStatusText { get => _cameraBarcodeStatusText; private set => SetProperty(ref _cameraBarcodeStatusText, value); }
        public bool IsCameraBarcodeCandidate { get => _isCameraBarcodeCandidate; private set => SetProperty(ref _isCameraBarcodeCandidate, value); }
        public bool IsCameraBarcodeConfirmed { get => _isCameraBarcodeConfirmed; private set => SetProperty(ref _isCameraBarcodeConfirmed, value); }
        public bool IsCameraBarcodeRecognitionEnabled => Config?.EnableCameraBarcodeRecognition == true;

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
        private volatile bool _isEncoderDetectRunning = true; // 是否正在进行 GPU 编码器检测
        private readonly string _activeWorkstationRole = WorkstationRoles.CameraMonitor;
        private string _workstationAccessText = AppLanguage.Get("Main.OtherComputersUnavailable");
        private string _workstationPrintStatusText = "快递单打印工位：未连接";
        private string _workstationStatusToolTip = "";
        private string _monitorAccessAddress = "";
        private int _workstationAddressRefreshVersion;

        private int _totalPieces;
        private TimeSpan _totalPackTime;
        public int TotalPieces { get => _totalPieces; set { SetProperty(ref _totalPieces, value); OnPropertyChanged(nameof(AveragePackTimeDisplay)); } }
        public DurationDisplayText TotalPackTimeDisplay => FormatDurationDisplay(_totalPackTime);
        public DurationDisplayText AveragePackTimeDisplay => TotalPieces == 0 ? DurationDisplayText.Zero : FormatDurationDisplay(TimeSpan.FromSeconds(_totalPackTime.TotalSeconds / TotalPieces));

        public sealed class DurationDisplayText
        {
            public static DurationDisplayText Zero { get; } = new("", "", "", "", "0", "秒");

            public DurationDisplayText(string hourValue, string hourUnit, string minuteValue, string minuteUnit, string secondValue, string secondUnit)
            {
                HourValue = hourValue;
                HourUnit = hourUnit;
                MinuteValue = minuteValue;
                MinuteUnit = minuteUnit;
                SecondValue = secondValue;
                SecondUnit = secondUnit;
            }

            public string HourValue { get; }
            public string HourUnit { get; }
            public string MinuteValue { get; }
            public string MinuteUnit { get; }
            public string SecondValue { get; }
            public string SecondUnit { get; }
        }

        private static DurationDisplayText FormatDurationDisplay(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                duration = TimeSpan.Zero;

            int totalSeconds = (int)Math.Round(duration.TotalSeconds);
            int hours = totalSeconds / 3600;
            int minutes = totalSeconds % 3600 / 60;
            int seconds = totalSeconds % 60;

            return new DurationDisplayText(
                hours > 0 ? hours.ToString() : "",
                hours > 0 ? "时" : "",
                minutes > 0 || hours > 0 ? minutes.ToString() : "",
                minutes > 0 || hours > 0 ? "分" : "",
                seconds.ToString(),
                "秒");
        }

        internal static string FormatWatermarkTimestamp(DateTimeOffset timestamp)
        {
            TimeSpan offset = timestamp.Offset;
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            offset = offset.Duration();
            string offsetText = offset.Minutes == 0
                ? $"{sign}{offset.Hours:00}"
                : $"{sign}{offset.Hours:00}:{offset.Minutes:00}";
            return $"UTC{offsetText}: {timestamp:yyyy/MM/dd HH:mm:ss}";
        }

        private string _toastMessage;
        private bool _isToastVisible;
        private CancellationTokenSource _toastCts;
        public string ToastMessage { get => _toastMessage; set => SetProperty(ref _toastMessage, value); }
        public bool IsToastVisible { get => _isToastVisible; set => SetProperty(ref _isToastVisible, value); }

        private string _previewOrderRemarkText = "";
        private string _previewOrderDetailText = "";
        private string _previewAlertText = "";
        private bool _isPreviewOrderNoticeVisible;
        private bool _isPreviewAlertVisible;
        private bool _isPreviewAlertCritical;
        private CancellationTokenSource _previewAlertCts;
        public string PreviewOrderRemarkText { get => _previewOrderRemarkText; private set => SetProperty(ref _previewOrderRemarkText, value); }
        public string PreviewOrderDetailText { get => _previewOrderDetailText; private set => SetProperty(ref _previewOrderDetailText, value); }
        public string PreviewAlertText { get => _previewAlertText; private set => SetProperty(ref _previewAlertText, value); }
        public bool IsPreviewOrderNoticeVisible { get => _isPreviewOrderNoticeVisible; private set => SetProperty(ref _isPreviewOrderNoticeVisible, value); }
        public bool IsPreviewAlertVisible { get => _isPreviewAlertVisible; private set => SetProperty(ref _isPreviewAlertVisible, value); }
        public bool IsPreviewAlertCritical { get => _isPreviewAlertCritical; private set => SetProperty(ref _isPreviewAlertCritical, value); }

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
        public bool IsRecording
        {
            get => _isRecording;
            set
            {
                if (!SetProperty(ref _isRecording, value)) return;
                ScheduleRefreshBarcodes();
                if (!value)
                    ClearPreviewOrderNotice();
            }
        }
        public bool IsShutdownInProgress { get => _isShutdownInProgress; private set => SetProperty(ref _isShutdownInProgress, value); }
        public double DiskUsagePercent { get => _diskUsagePercent; set => SetProperty(ref _diskUsagePercent, value); }
        public string DiskUsageText { get => _diskUsageText; set => SetProperty(ref _diskUsageText, value); }
        public AppConfig Config
        {
            get => _config;
            set
            {
                if (SetProperty(ref _config, value))
                    OnPropertyChanged(nameof(IsCameraBarcodeRecognitionEnabled));
            }
        }
        public string WorkstationAccessText { get => _workstationAccessText; set => SetProperty(ref _workstationAccessText, value); }
        public string WorkstationPrintStatusText { get => _workstationPrintStatusText; set => SetProperty(ref _workstationPrintStatusText, value); }
        public string WorkstationStatusToolTip { get => _workstationStatusToolTip; set => SetProperty(ref _workstationStatusToolTip, value); }
        public string MonitorAccessAddress { get => _monitorAccessAddress; set => SetProperty(ref _monitorAccessAddress, value); }

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

        public void ResumeVideoPreviewUpdatesAfterWindowMove()
        {
            SuppressVideoPreviewUpdates = false;
            BeginPreviewSession(clearFrame: false);
        }
        private void RefreshBarcodes()
        {
            try
            {
                // 行1: 扫码框有内容→清除；空→切换模式
                string cmd1; string label1;
                if (!string.IsNullOrEmpty(_scanInputText))
                { cmd1 = "CLEAR"; label1 = AppLanguage.Get("Main.BarcodeClear").Replace("\\n", "\n"); }
                else if (_currentMode == "发货")
                { cmd1 = "BACK"; label1 = AppLanguage.Get("Main.BarcodeReturn").Replace("\\n", "\n"); }
                else
                { cmd1 = "SHIP"; label1 = AppLanguage.Get("Main.BarcodeShipping").Replace("\\n", "\n"); }
                // 行2: 未录制→开录；录制中→停录
                string cmd2 = _isRecording ? "STOP" : "START";
                string label2 = AppLanguage.Get(_isRecording ? "Main.BarcodeStop" : "Main.BarcodeStart").Replace("\\n", "\n");
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
        public ICommand CopyMonitorAddressCommand { get; }
        public ICommand SwitchWorkstationCommand { get; }

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
                CopyMonitorAddressCommand = new RelayCommand(() => { });
                SwitchWorkstationCommand = new RelayCommand(() => { });
                ClearScanInputCommand = new RelayCommand(() => { });
                ClearSearchCommand = new RelayCommand(() => { });
                return;
            }

            LoadConfig();
            InitializeCameraBarcodeRecognition();
            // 在起动时后台探测可用 GPU 编码器并缓存
            Task.Run(() => {
                _isEncoderDetectRunning = true;
                try
                {
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
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("EncoderDetect", "Startup encoder detection failed", ex);
                }
                finally
                {
                    _isEncoderDetectRunning = false;
                }
            });
            InitDatabase();
            RefreshTodayStats();
            RestoreRecentScanRecords();
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
            _alertService = new AlertService(
                PresentAlert,
                _speechService.PlayAlert,
                interruptAudio: _speechService.Stop,
                preGenerate: (text, style) => _speechService.PreGenerateCache(text, style == AlertVoiceStyle.Warning),
                pauseAudio: _speechService.PauseForRecording,
                resumeAudio: _speechService.ResumeAfterRecording);
            ScanCommand = new RelayCommand<string>(HandleScan);
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            OpenPlaybackCommand = new RelayCommand(OpenPlaybackWindow);
            ToggleModeCommand = new RelayCommand(ToggleMode);
            ToggleRecordingCommand = new RelayCommand(ToggleRecording);
            OpenStatsCommand = new RelayCommand(OpenStatsWindow);
            ResetEncoderDetectCommand = new RelayCommand(ResetEncoderDetect);
            CopyMonitorAddressCommand = new RelayCommand(CopyMonitorAddress);
            SwitchWorkstationCommand = new RelayCommand(SwitchWorkstation);
            ClearScanInputCommand = new RelayCommand(() => ScanInputText = "");
            ClearSearchCommand = new RelayCommand(() => LogSearchText = "");
            InitializeSystem();
            StartUiHeartbeat();
            RefreshBarcodes();
            InitGlobalKeyboardHook();
        }

        private void InitGlobalKeyboardHook()
        {
            _globalKeyHook = new GlobalKeyboardHook();
            ApplyGlobalKeyboardConfig();
            _globalKeyHook.BarcodeScanned += OnGlobalBarcodeScanned;
            if (Config.EnableGlobalKeyboard)
                _globalKeyHook.Start();
        }

        private void ApplyGlobalKeyboardConfig()
        {
            _globalKeyHook?.ConfigureAutoSubmit(
                Config.EnableScannerAutoSubmit,
                Config.ScannerAutoSubmitMinLength,
                Config.ScannerAutoSubmitQuietMs,
                Config.ScannerAutoSubmitMaxAverageIntervalMs,
                Config.ScannerAutoSubmitMaxKeyIntervalMs,
                IsAutoSubmitScanCandidate);
        }

        private void OnGlobalBarcodeScanned(string barcode)
        {
            if (_isDisposed || _shutdownRequested) return;
            HandleScan(barcode);
        }

        private void InitializeCameraBarcodeRecognition()
        {
            _cameraBarcodeRecognition = new CameraBarcodeRecognitionService(
                IsAutoSubmitScanCandidate,
                () => !IsRecording && CanSubmitCameraBarcode());
            _cameraBarcodeRecognition.StatusChanged += OnCameraBarcodeStatusChanged;
            _cameraBarcodeRecognition.BarcodeConfirmed += OnCameraBarcodeConfirmed;
        }

        private bool CanSubmitCameraBarcode()
        {
            return Config?.EnableCameraBarcodeRecognition == true
                && !_isDisposed
                && !_shutdownRequested
                && !_isSetupWizardActive
                && !_isCameraSleeping
                && !IsBusy;
        }

        private void TrySubmitCameraBarcodeFrame(Mat frame)
        {
            if (!CanSubmitCameraBarcode())
                return;

            _cameraBarcodeRecognition?.TrySubmitFrame(frame, allowFullFrame: !IsRecording);
        }

        private void OnCameraBarcodeConfirmed(string code)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (CameraBarcodeRuntimeOptions.ShadowMode)
                {
                    LogBarcodeRecordingComparison(code, fromCamera: true, dryRun: true);
                    return;
                }

                if (CanSubmitCameraBarcode())
                    HandleScan(code, fromCamera: true);
            }));
        }

        private void OnCameraBarcodeStatusChanged(CameraBarcodeRecognitionStatus status)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            _ = dispatcher.BeginInvoke(new Action(() => ApplyCameraBarcodeStatus(status)));
        }

        private void ApplyCameraBarcodeStatus(CameraBarcodeRecognitionStatus status)
        {
            if (_isDisposed || Config?.EnableCameraBarcodeRecognition != true)
                return;

            if (status.State == CameraBarcodeRecognitionState.Confirmed)
            {
                _cameraBarcodeFeedbackCts?.Cancel();
                var cts = _cameraBarcodeFeedbackCts = new CancellationTokenSource();
                IsCameraBarcodeCandidate = false;
                IsCameraBarcodeConfirmed = true;
                CameraBarcodeStatusText = $"已识别 {status.Code}";
                _ = ResetCameraBarcodeFeedbackAsync(cts);
                return;
            }

            if (IsCameraBarcodeConfirmed)
                return;

            IsCameraBarcodeCandidate = status.State == CameraBarcodeRecognitionState.Candidate;
            CameraBarcodeStatusText = IsCameraBarcodeCandidate ? "识别中，请保持稳定" : "将面单条形码放入框内";
        }

        private async Task ResetCameraBarcodeFeedbackAsync(CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(1200, cts.Token);
                if (!cts.IsCancellationRequested && !_isDisposed)
                {
                    IsCameraBarcodeConfirmed = false;
                    IsCameraBarcodeCandidate = false;
                    CameraBarcodeStatusText = "将面单条形码放入框内";
                }
            }
            catch (OperationCanceledException) { }
        }

        private void ResetCameraBarcodeRecognition(bool preserveConfirmedCodes = false)
        {
            _cameraBarcodeFeedbackCts?.Cancel();
            _cameraBarcodeRecognition?.Reset(preserveConfirmedCodes);
            IsCameraBarcodeCandidate = false;
            IsCameraBarcodeConfirmed = false;
            CameraBarcodeStatusText = "将面单条形码放入框内";
        }

        private void StartUiHeartbeat()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            _lastUiHeartbeatAt = DateTime.Now;
            _uiHeartbeatTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Normal,
                dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _uiHeartbeatTimer.Tick += (_, __) => _lastUiHeartbeatAt = DateTime.Now;
            _uiHeartbeatTimer.Start();
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
                OnPropertyChanged(nameof(TotalPackTimeDisplay)); OnPropertyChanged(nameof(AveragePackTimeDisplay));
            }
            catch { }
        }

        private void RestoreRecentScanRecords()
        {
            try
            {
                var records = _db?.GetRecentCompletedVideos(DateTime.Today, 20);
                if (records == null) return;

                _allLogs.Clear();
                foreach (var record in records)
                {
                    _allLogs.Add(new ScanRecord(
                        record.OrderId,
                        "已保存",
                        record.StartTime.ToString("HH:mm:ss"),
                        record.Mode));
                }
                FilterLogs();
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("ScanHistory", "Failed to restore recent scan records", ex);
            }
        }

        private void ToggleMode() { CurrentMode = CurrentMode == "发货" ? "退货" : "发货"; ShowToast($"已切换为: {CurrentMode}"); Speak(CurrentMode == "发货" ? DefaultSpeechCatalog.SwitchToShipping : DefaultSpeechCatalog.SwitchToReturn); }

        private void PauseSpeechForRecording() => _alertService?.PauseAudio();

        private void ResumeSpeechWhenCameraIdle()
        {
            if (_alertService == null || _isDisposed) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(800);
                    if (_isDisposed || IsRecording || IsBusy) return;
                    _alertService.ResumeAudio();
                }
                catch { }
            });
        }

        // ========================== 核心逻辑：恢复 MAN_ 前缀 ==========================
        private async void ToggleRecording() 
        {
            NotifyUserActivity();
            if (IsBusy || _isDisposed || _shutdownRequested) return;
            if (!await _recorderLock.WaitAsync(0)) return; 

            try 
            {
                if (IsRecording) 
                {
                    PauseSpeechForRecording();
                    await InternalStopRecordingAsync();
                    QueuePostStopMux("手动停止");
                    CurrentOrderId = "";
                    ScanInputText = "";
                    ShowToast("已手动停止录制");
                    Speak(DefaultSpeechCatalog.StopRecording, cancelPrevious: false);
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
                        PublishScannerAlert(
                            "missing-order-number",
                            "警告：没有单号",
                            DefaultSpeechCatalog.MissingOrderNumber,
                            repeatCount: 3);
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

        private void HandleScan(string scanResult)
        {
            HandleScan(scanResult, fromCamera: false);
        }

        private async void HandleScan(string scanResult, bool fromCamera)
        {
            NotifyUserActivity();
            BarcodeRecordingDecision decision = EvaluateBarcodeRecordingDecision(scanResult, fromCamera);
            if (CameraBarcodeRuntimeOptions.ShadowMode)
                LogBarcodeRecordingComparison(decision, fromCamera, dryRun: false);

            if (decision.Reason == BarcodeRecordingDecisionReason.CannotProcess)
            {
                ScanInputText = "";
                return;
            }
            if (decision.Reason == BarcodeRecordingDecisionReason.EmptyInput)
                return;

            string upperResult = decision.NormalizedValue;
            // 立即清空扫码框，防止重复触发
            ScanInputText = "";

            switch (decision.Reason)
            {
                case BarcodeRecordingDecisionReason.CameraCurrentCodeIgnored:
                    RuntimeLog.Info("CameraBarcode", $"Ignored current recording barcode while same-code stop is disabled: {upperResult}");
                    return;
                case BarcodeRecordingDecisionReason.CooldownOrderQueued:
                    _pendingScanDuringCooldown = upperResult;
                    RuntimeLog.Info("Scan", $"Scan queued during cooldown: {upperResult}");
                    ShowToast("扫码过快，已保留最后一个单号");
                    return;
                case BarcodeRecordingDecisionReason.CooldownIgnored:
                    return;
                case BarcodeRecordingDecisionReason.ClearCommand:
                    ShowToast("提示：扫码框已清除");
                    return;
                case BarcodeRecordingDecisionReason.ShippingCommand:
                    CurrentMode = "发货";
                    StartInputCooldown();
                    ShowToast("切换为发货模式");
                    Speak(DefaultSpeechCatalog.SwitchToShipping);
                    return;
                case BarcodeRecordingDecisionReason.ReturnCommand:
                    CurrentMode = "退货";
                    StartInputCooldown();
                    ShowToast("切换为退货模式");
                    Speak(DefaultSpeechCatalog.SwitchToReturn);
                    return;
                case BarcodeRecordingDecisionReason.StartCommand:
                    ToggleRecording();
                    return;
                case BarcodeRecordingDecisionReason.StopCommand:
                    _ = SafeStopRecordingAsync(true, mergeAfterStop: true);
                    return;
                case BarcodeRecordingDecisionReason.RecordingOrderMissing:
                    ShowToast("当前录像未绑定单号，无法同码停录");
                    SpeakWarning(DefaultSpeechCatalog.RecordingHasNoOrderNumber);
                    return;
                case BarcodeRecordingDecisionReason.RecordingOrderMismatch:
                    ShowToast($"警告：单号不一致：{upperResult}");
                    SpeakWarning(DefaultSpeechCatalog.OrderNumberMismatch);
                    return;
                case BarcodeRecordingDecisionReason.InvalidOrderNumber:
                    PublishScannerAlert(
                        $"invalid-order-number:{upperResult}",
                        "非法单号，已拦截",
                        DefaultSpeechCatalog.InvalidOrderNumber);
                    return;
            }

            // 同码停录由统一决策器确认，避免影子日志与真实流程分别维护一套规则。
            if (decision.Reason == BarcodeRecordingDecisionReason.SameCodeMatched)
            {
                if (!await _recorderLock.WaitAsync(0))
                {
                    ShowToast("录制状态正在切换，请稍后再试");
                    return;
                }

               try
                {
                    _stopReason = "同码停录";
                    PauseSpeechForRecording();
                    await InternalStopRecordingAsync();
                    QueuePostStopMux("同码停录");
                    CurrentOrderId = "";
                    ScanInputText = "";
                    ShowToast("单号匹配，已停止录制");
                    Speak(DefaultSpeechCatalog.StopRecording, cancelPrevious: false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HandleScan] 同码停录异常: {ex.Message}");
                    RuntimeLog.Error("Scan", "Matched barcode stop failed", ex);
                }
                finally
                {
                    if (!IsRecording)
                        ResumeSpeechWhenCameraIdle();
                    _recorderLock.Release();
                }
                return;
            }

            Debug.WriteLine($"[Zoom] 扫码事件触发: ID={upperResult}, ZoomEnabled={Config.EnableSmartZoom}, Delay={Config.ZoomDelaySeconds}");
            StartInputCooldown();

            CurrentOrderId = upperResult;
            if (IsRecording) _stopReason = "扫码切换";
            if (!await _recorderLock.WaitAsync(0)) return;
            try
            {
                // 扫码切换：立即打断上一轮可能还在播放的语音（如"重复单号"×3）
                _alertService?.InterruptAudio();
                if (IsRecording)
                {
                    PauseSpeechForRecording();
                    RuntimeLog.Info("Recording", "连续扫码切换，暂缓音视频合成，等待 stop 或手动停止");
                    await InternalStopRecordingAsync();
                }
                await InternalStartRecordingAsync();
                QueuePrintedRefundCheck(upperResult, CurrentMode);

                // 录制已启动、数据库记录已写入，此时检查重复单号（排除刚刚插入的当前记录）
                bool isDuplicate = _db != null && _db.OrderIdExistsRecent(upperResult, excludeRecordId: _currentRecordId);
                if (isDuplicate)
                {
                    PublishScannerAlert(
                        $"duplicate-order-number:{upperResult}",
                        "警告：重复单号，请确认",
                        DefaultSpeechCatalog.DuplicateOrderNumber,
                        repeatCount: 3);
                }

                // 查询快递助手推送的订单信息，在预览画面持续提示并按设置播报
                if (Config.EnableOrderInfoLog)
                    System.Diagnostics.Debug.WriteLine($"[OrderInfo] 扫码查询: {upperResult}, EnableAnnounce={Config.EnableOrderInfoAnnounce}, WebServer={(_webServer != null ? "已启动" : "未启动")}");
                var orderInfo = _webServer?.GetOrderInfo(upperResult);
                SetPreviewOrderNotice(IsRecording ? orderInfo : null);
                if (Config.EnableOrderInfoLog)
                    System.Diagnostics.Debug.WriteLine($"[OrderInfo] 查询结果: {(orderInfo != null ? $"命中 买家=[{orderInfo.BuyerMessage}] 卖家=[{orderInfo.SellerMemo}] 商品=[{orderInfo.ProductInfo}]" : "未命中")}");
                if (Config.EnableOrderInfoAnnounce && orderInfo != null)
                {
                    foreach (AlertSpeechFollowup announcement in BuildOrderInfoSpeechFollowups(
                                 orderInfo,
                                 Config.EnableOrderInfoAnnounce,
                                 Config.AnnounceBuyerMessage,
                                 Config.AnnounceSellerMemo,
                                 Config.AnnounceProductInfo))
                    {
                        PublishVoice(
                            announcement.Text,
                            announcement.VoiceStyle,
                            announcement.Sound,
                            repeatCount: 1,
                            interruptCurrent: false);
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

        private void LogBarcodeRecordingComparison(string scanResult, bool fromCamera, bool dryRun)
        {
            BarcodeRecordingDecision decision = EvaluateBarcodeRecordingDecision(scanResult, fromCamera);
            LogBarcodeRecordingComparison(decision, fromCamera, dryRun);
        }

        private void LogBarcodeRecordingComparison(
            BarcodeRecordingDecision decision,
            bool fromCamera,
            bool dryRun)
        {
            string source = fromCamera ? "摄像头" : "扫码枪";
            string execution = dryRun ? "仅判定不执行" : "进入真实流程";
            RuntimeLog.Info(
                "CameraBarcodeCompare",
                $"来源={source}, 单号={decision.NormalizedValue}, 判定={GetBarcodeDecisionText(decision.Action)}, 原因={BarcodeRecordingDecisionPolicy.GetReasonText(decision.Reason)}, 执行={execution}, 当前录制={IsRecording}, 当前单号={_recordingOrderId}, 同码停录={Config.EnableSameBarcodeStopRecording}, 冷却中={_isInputOnCooldown}");
        }

        private BarcodeRecordingDecision EvaluateBarcodeRecordingDecision(string scanResult, bool fromCamera) =>
            BarcodeRecordingDecisionPolicy.Evaluate(
                scanResult,
                fromCamera,
                canProcess: fromCamera
                    ? CanSubmitCameraBarcode()
                    : !IsBusy && !_isDisposed && !_shutdownRequested,
                IsRecording,
                _recordingOrderId,
                Config.EnableSameBarcodeStopRecording,
                _isInputOnCooldown,
                Config.OrderIdRegex);

        private static string GetBarcodeDecisionText(BarcodeRecordingDecisionAction action) => action switch
        {
            BarcodeRecordingDecisionAction.Queue => "等待处理",
            BarcodeRecordingDecisionAction.Start => "开始录制",
            BarcodeRecordingDecisionAction.Stop => "停止录制",
            BarcodeRecordingDecisionAction.Switch => "切换录制",
            BarcodeRecordingDecisionAction.ClearInput => "清除输入",
            BarcodeRecordingDecisionAction.SwitchToShipping => "切换发货模式",
            BarcodeRecordingDecisionAction.SwitchToReturn => "切换退货模式",
            BarcodeRecordingDecisionAction.ToggleRecording => "切换录制状态",
            _ => "忽略"
        };

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
            return CameraBarcodeCandidatePolicy.IsValid(upperResult, Config.OrderIdRegex);
        }

        internal static bool ShouldAlertPrintedRefund(string mode, bool alertEnabled, OrderInfo orderInfo)
        {
            if (!alertEnabled ||
                (mode != "发货" && mode != "退货") ||
                orderInfo?.IsPrintedRefund != true)
                return false;

            string[] statuses = ParseRefundStatuses(orderInfo.RefundStatus);
            return statuses.Length == 0 || statuses.Any(status => status != "NO_REFUND");
        }

        internal static string GetRefundStatusDisplayText(OrderInfo orderInfo)
        {
            string[] statuses = ParseRefundStatuses(orderInfo?.RefundStatus);
            if (statuses.Length == 0)
                return "存在打印后退款，请人工核对";

            var descriptions = statuses
                .Where(status => status != "NO_REFUND")
                .Select(status => status switch
                {
                    "WAIT_SELLER_AGREE" => DefaultSpeechCatalog.RefundWaitingSeller,
                    "WAIT_BUYER_RETURN_GOODS" => DefaultSpeechCatalog.RefundWaitingBuyerReturn,
                    "WAIT_SELLER_CONFIRM_GOODS" => DefaultSpeechCatalog.RefundWaitingSellerConfirm,
                    "SUCCESS" => DefaultSpeechCatalog.RefundCompleted,
                    "CLOSED" => DefaultSpeechCatalog.RefundClosed,
                    _ => $"退款状态未知（{status}），请人工核对"
                })
                .Distinct()
                .ToList();

            return descriptions.Count == 0 ? "无退款" : string.Join("，", descriptions);
        }

        private static string[] ParseRefundStatuses(string refundStatus)
        {
            return (refundStatus ?? "")
                .Split(new[] { ',', '，', ';', '；', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(status => status.ToUpperInvariant())
                .Distinct()
                .ToArray();
        }

        internal static TimeSpan GetPrintedRefundLookupDelay(DateTime lastRequestUtc, DateTime nowUtc)
        {
            if (lastRequestUtc == DateTime.MinValue)
                return TimeSpan.Zero;

            TimeSpan remaining = PrintedRefundLookupInterval - (nowUtc - lastRequestUtc);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        private void QueuePrintedRefundCheck(string trackingNumber, string mode)
        {
            if (!Config.EnablePrintedRefundAlert || string.IsNullOrWhiteSpace(trackingNumber))
                return;

            var check = new PrintedRefundScanCheck
            {
                TrackingNumber = trackingNumber.Trim().ToUpperInvariant(),
                Mode = mode
            };

            CheckPrintedRefundAndAlert(check, _webServer?.GetOrderInfo(check.TrackingNumber), "最近缓存");

            lock (_printedRefundLookupLock)
            {
                _pendingPrintedRefundChecks.Add(check);
                if (_printedRefundLookupTask == null || _printedRefundLookupTask.IsCompleted)
                    _printedRefundLookupTask = Task.Run(RunPrintedRefundLookupLoopAsync);
            }
        }

        private async Task RunPrintedRefundLookupLoopAsync()
        {
            while (true)
            {
                TimeSpan delay;
                lock (_printedRefundLookupLock)
                {
                    if (_isDisposed || _pendingPrintedRefundChecks.Count == 0)
                    {
                        _printedRefundLookupTask = null;
                        return;
                    }
                    delay = GetPrintedRefundLookupDelay(_lastPrintedRefundLookupUtc, DateTime.UtcNow);
                }

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay);

                WebServer server = _webServer;
                OrderLookupResult result = new() { Responded = false };
                lock (_printedRefundLookupLock)
                    _lastPrintedRefundLookupUtc = DateTime.UtcNow;

                try
                {
                    if (server != null)
                    {
                        string[] trackingNumbers;
                        lock (_printedRefundLookupLock)
                        {
                            trackingNumbers = _pendingPrintedRefundChecks
                                .Select(x => x.TrackingNumber)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                        }
                        result = await server.RequestFreshOrderSnapshotAsync(PrintedRefundLookupTimeout, trackingNumbers);
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("Scan", "Printed-refund snapshot request failed", ex);
                }

                List<PrintedRefundScanCheck> checks;
                lock (_printedRefundLookupLock)
                {
                    checks = _pendingPrintedRefundChecks.ToList();
                    _pendingPrintedRefundChecks.Clear();
                }

                foreach (PrintedRefundScanCheck check in checks)
                    CheckPrintedRefundAndAlert(check, server?.GetOrderInfo(check.TrackingNumber), result.Responded ? "最新页面快照" : "请求失败后的最近缓存");

                RuntimeLog.Info(
                    "Scan",
                    $"Printed-refund snapshot checked: responded={result.Responded}, returned={result.Orders.Count}, scans={checks.Count}");
            }
        }

        private void CheckPrintedRefundAndAlert(PrintedRefundScanCheck check, OrderInfo orderInfo, string source)
        {
            if (!ShouldAlertPrintedRefund(check.Mode, Config.EnablePrintedRefundAlert, orderInfo) || !check.TryMarkAlerted())
                return;

            RuntimeLog.Warn(
                "Scan",
                $"Printed-refund order detected: tracking={check.TrackingNumber}, order={orderInfo.OrderId}, status={orderInfo.RefundStatus}, source={source}");
            string statusText = GetRefundStatusDisplayText(orderInfo);
            if (_isDisposed)
                return;

            _alertService?.Publish(new AlertRequest
            {
                Message = $"警告：快递单 {check.TrackingNumber}，{statusText}",
                SpeechText = DefaultSpeechCatalog.CreatePrintedRefundAnnouncement(statusText),
                Priority = AlertPriority.Critical,
                Sound = AlertSound.IndustrialAlarm,
                SoundRepeatCount = 1,
                SpeechRepeatCount = 3,
                DisplayDuration = TimeSpan.FromSeconds(12),
                DeduplicationKey = $"printed-refund:{check.TrackingNumber}:{check.AlertId}",
                DeduplicationWindow = TimeSpan.FromMinutes(1),
                FollowupSpeech = BuildOrderInfoSpeechFollowups(
                    orderInfo,
                    Config.EnableOrderInfoAnnounce,
                    Config.AnnounceBuyerMessage,
                    Config.AnnounceSellerMemo,
                    Config.AnnounceProductInfo)
            });
        }

        public bool IsAutoSubmitScanCandidate(string scanText)
        {
            return IsOrderScan((scanText ?? "").ToUpper().Trim());
        }

        private void QueuePostStopMux(string reason)
        {
            if (_isDisposed)
                return;

            Task previousMuxTask = _postStopMuxTask ?? Task.CompletedTask;
            Task finalizeTask = _lastFinalizeTask ?? Task.CompletedTask;
            _postStopMuxTask = Task.Run(async () =>
            {
                try
                {
                    await previousMuxTask.ConfigureAwait(false);
                    await finalizeTask.ConfigureAwait(false);
                    if (_isDisposed)
                        return;

                    RuntimeLog.Info("MkvToMp4", $"最终停止后开始合成 MP4：reason={reason}");
                    var result = await BatchConvertMkvToMp4Async(
                        new Progress<string>(msg => Debug.WriteLine($"[PostStopMux] {msg}")),
                        CancellationToken.None).ConfigureAwait(false);

                    RuntimeLog.Info("MkvToMp4", $"最终停止后合成完成：reason={reason}, success={result.success}, fail={result.fail}, skip={result.skip}");
                    if (result.fail > 0)
                    {
                        _ = Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (!_isDisposed)
                                ShowToast("部分视频合成失败，已保留原文件");
                        });
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("MkvToMp4", $"最终停止后合成异常：reason={reason}", ex);
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_isDisposed)
                            ShowToast("视频合成失败，已保留原文件");
                    });
                }
            });
        }

        private async Task SafeStopRecordingAsync(bool isManual = false, bool mergeAfterStop = true)
        {
            if (IsBusy || !IsRecording || _isDisposed) return;
            if (!await _recorderLock.WaitAsync(0)) return;
            try
            {
                PauseSpeechForRecording();
                await InternalStopRecordingAsync();
                if (mergeAfterStop)
                    QueuePostStopMux(isManual ? "手动停止" : "最终停止");
                if (isManual)
                {
                    CurrentOrderId = "";
                    ScanInputText = "";
                    ShowToast("已手动停止录制");
                    Speak(DefaultSpeechCatalog.StopRecording, cancelPrevious: false);
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

        public void ShowToast(string message)
        {
            message = AppLanguage.Translate(message);
            if (_alertService != null)
            {
                _alertService.Publish(new AlertRequest
                {
                    Message = message,
                    Priority = AlertPriority.Normal,
                    Sound = AlertSound.None,
                    DisplayDuration = TimeSpan.FromMilliseconds(2500)
                });
                return;
            }

            PresentToast(message, TimeSpan.FromMilliseconds(2500));
        }

        private void PresentAlert(AlertRequest request)
        {
            if (ShouldShowPreviewAlert(request))
                PresentPreviewAlert(request);
            else
                PresentToast(request.Message, request.DisplayDuration);
        }

        internal static bool ShouldShowPreviewAlert(AlertRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Message))
                return false;
            if (request.Priority == AlertPriority.Critical || request.Sound is AlertSound.Warning or AlertSound.IndustrialAlarm)
                return true;

            string message = request.Message;
            string[] exceptionTerms =
            [
                "警告", "异常", "失败", "错误", "断开", "丢失", "超时", "拦截", "退款", "不一致", "无法", "过短", "太小",
                "warning", "error", "failed", "failure", "exception", "disconnected", "timeout", "invalid", "refund"
            ];
            return exceptionTerms.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        internal static string BuildPreviewOrderNotice(OrderInfo orderInfo)
        {
            if (orderInfo == null)
                return "";

            string remarks = BuildPreviewOrderRemarkNotice(orderInfo);
            string details = BuildPreviewOrderDetailNotice(orderInfo);
            return string.Join(
                Environment.NewLine,
                new[] { remarks, details }.Where(value => value.Length > 0));
        }

        internal static string BuildPreviewOrderRemarkNotice(OrderInfo orderInfo)
        {
            if (orderInfo == null)
                return "";

            var lines = new List<string>();
            AddPreviewOrderLine(lines, "Main.PreviewBuyerMessage", orderInfo.BuyerMessage);
            AddPreviewOrderLine(lines, "Main.PreviewSellerMemo", orderInfo.SellerMemo);
            return string.Join(Environment.NewLine, lines);
        }

        internal static string BuildPreviewOrderDetailNotice(OrderInfo orderInfo)
        {
            if (orderInfo == null)
                return "";

            var lines = new List<string>();
            AddPreviewOrderLine(lines, "Main.PreviewProduct", orderInfo.ProductInfo);

            if (orderInfo.HasRefund || orderInfo.IsPrintedRefund)
            {
                string status = GetRefundStatusDisplayText(orderInfo);
                if (!string.Equals(status, "无退款", StringComparison.Ordinal))
                    lines.Add(AppLanguage.Format("Main.PreviewException", CompactPreviewText(status)));
            }

            return string.Join(Environment.NewLine, lines);
        }

        internal static IReadOnlyList<AlertSpeechFollowup> BuildOrderInfoSpeechFollowups(
            OrderInfo orderInfo,
            bool announcementsEnabled,
            bool announceBuyerMessage,
            bool announceSellerMemo,
            bool announceProductInfo)
        {
            if (!announcementsEnabled || orderInfo == null)
                return Array.Empty<AlertSpeechFollowup>();

            var announcements = new List<AlertSpeechFollowup>();
            if (announceBuyerMessage && !string.IsNullOrWhiteSpace(orderInfo.BuyerMessage))
            {
                announcements.Add(new AlertSpeechFollowup
                {
                    Text = DefaultSpeechCatalog.CreateBuyerMessageAnnouncement(orderInfo.BuyerMessage),
                    Sound = AlertSound.Remark
                });
            }
            if (announceSellerMemo && !string.IsNullOrWhiteSpace(orderInfo.SellerMemo))
            {
                announcements.Add(new AlertSpeechFollowup
                {
                    Text = DefaultSpeechCatalog.CreateSellerMemoAnnouncement(orderInfo.SellerMemo),
                    Sound = AlertSound.Remark
                });
            }
            if (announceProductInfo && !string.IsNullOrWhiteSpace(orderInfo.ProductInfo))
            {
                announcements.Add(new AlertSpeechFollowup
                {
                    Text = DefaultSpeechCatalog.CreateProductAnnouncement(orderInfo.ProductInfo),
                    Sound = AlertSound.None
                });
            }
            return announcements;
        }

        private static void AddPreviewOrderLine(List<string> lines, string resourceKey, string value)
        {
            string compact = CompactPreviewText(value);
            if (compact.Length > 0)
                lines.Add(AppLanguage.Format(resourceKey, compact));
        }

        private static string CompactPreviewText(string value)
        {
            string compact = string.Join(" ", (value ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            const int maxLength = 160;
            return compact.Length <= maxLength ? compact : compact[..maxLength] + "…";
        }

        private void SetPreviewOrderNotice(OrderInfo orderInfo)
        {
            PreviewOrderRemarkText = BuildPreviewOrderRemarkNotice(orderInfo);
            PreviewOrderDetailText = BuildPreviewOrderDetailNotice(orderInfo);
            IsPreviewOrderNoticeVisible = PreviewOrderRemarkText.Length > 0 || PreviewOrderDetailText.Length > 0;
        }

        private void ClearPreviewOrderNotice() => SetPreviewOrderNotice(null);

        private void PresentPreviewAlert(AlertRequest request)
        {
            Application.Current?.Dispatcher?.InvokeAsync(async () =>
            {
                _previewAlertCts?.Cancel();
                _previewAlertCts = new CancellationTokenSource();
                var token = _previewAlertCts.Token;
                PreviewAlertText = request.Message;
                IsPreviewAlertCritical = request.Priority == AlertPriority.Critical || request.Sound == AlertSound.IndustrialAlarm;
                IsPreviewAlertVisible = true;
                TimeSpan duration = request.DisplayDuration < TimeSpan.FromSeconds(5)
                    ? TimeSpan.FromSeconds(5)
                    : request.DisplayDuration;
                try { await Task.Delay(duration, token); }
                catch (OperationCanceledException) { return; }
                IsPreviewAlertVisible = false;
            });
        }

        private void PresentToast(string message, TimeSpan displayDuration)
        {
            Application.Current?.Dispatcher?.InvokeAsync(async () =>
            {
                _toastCts?.Cancel();
                _toastCts = new CancellationTokenSource();
                var token = _toastCts.Token;
                ToastMessage = message;
                IsToastVisible = true;
                try { await Task.Delay(displayDuration, token); }
                catch (OperationCanceledException) { return; }
                IsToastVisible = false;
            });
        }

        private void PublishScannerAlert(
            string deduplicationKey,
            string message,
            string speechText,
            int repeatCount = 1)
        {
            _alertService?.Publish(new AlertRequest
            {
                Message = message,
                SpeechText = speechText,
                Priority = AlertPriority.Normal,
                Sound = AlertSound.Warning,
                SoundRepeatCount = 1,
                SpeechRepeatCount = repeatCount,
                DisplayDuration = TimeSpan.FromMilliseconds(2500),
                DeduplicationKey = deduplicationKey,
                DeduplicationWindow = TimeSpan.FromSeconds(3)
            });
        }

        private void FilterLogs() { FilteredLogs.Clear(); var keyword = LogSearchText?.ToUpper() ?? ""; foreach (var log in _allLogs) { if (string.IsNullOrEmpty(keyword) || log.OrderId.ToUpper().Contains(keyword)) FilteredLogs.Add(log); } }
        private void AddRecord(ScanRecord record) { Application.Current.Dispatcher.InvokeAsync(() => { _allLogs.Insert(0, record); if (string.IsNullOrEmpty(LogSearchText)) FilteredLogs.Insert(0, record); if (_allLogs.Count > 200) _allLogs.RemoveAt(_allLogs.Count - 1); }); }

        private void LoadConfig() 
        { 
            bool containsRemovedStorageQuota = false;
            if (File.Exists(_configFilePath))
            {
                try
                {
                    string configJson = File.ReadAllText(_configFilePath, System.Text.Encoding.UTF8);
                    containsRemovedStorageQuota = configJson.Contains("\"QuotaGB\"", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("Config", $"Failed to inspect config migration markers: {ex.Message}");
                }
            }

            Config = WorkstationConfigStore.Load();

            bool configMigrated = containsRemovedStorageQuota;
            if (Config.VideoCqp <= 0)
            {
                Config.VideoCqp = 25;
                configMigrated = true;
            }
            if (Config.AudioSyncOffsetMs == 400)
            {
                Config.AudioSyncOffsetMs = 0;
                configMigrated = true;
            }
            if (!WorkstationRoles.IsKnown(Config.WorkstationRole))
            {
                Config.WorkstationRole = WorkstationRoles.CameraMonitor;
                configMigrated = true;
            }
            int normalizedAudioSyncOffsetMs = Math.Clamp(Config.AudioSyncOffsetMs, -5000, 5000);
            if (Config.AudioSyncOffsetMs != normalizedAudioSyncOffsetMs)
            {
                Config.AudioSyncOffsetMs = normalizedAudioSyncOffsetMs;
                configMigrated = true;
            }
            configMigrated = AppConfig.NormalizeAfterLoad(Config) || configMigrated;
            if (configMigrated)
                SaveConfig();

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
        private bool SaveConfig(bool notifyUser = false) => SaveConfig(Config, notifyUser);

        private bool SaveConfig(AppConfig config, bool notifyUser = false)
        {
            if (WorkstationConfigStore.TrySave(config, out string error))
                return true;

            if (notifyUser)
                ShowToast($"配置保存失败，请检查磁盘空间或权限: {error}");
            return false;
        }

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
                var mainWindow = Application.Current?.MainWindow as MainWindow;
                if (mainWindow != null) settingsWin.Owner = mainWindow;
                mainWindow?.SuspendCapsLockForModalWindow();
                try
                {
                    settingsWin.ShowDialog();
                }
                finally
                {
                    mainWindow?.ResumeCapsLockAfterModalWindow();
                }
            }
            catch (Exception ex) { ShowToast($"设置错误: {ex.Message}"); }
        }

        public async Task<bool> ApplySettingsAsync(AppConfig nextConfig)
        {
            try
            {
                    // 判断摄像头相关配置是否变更
                    bool cameraChanged = Config.CameraIndex != nextConfig.CameraIndex
                        || Config.CameraMonikerString != nextConfig.CameraMonikerString
                        || Config.FrameWidth != nextConfig.FrameWidth
                        || Config.FrameHeight != nextConfig.FrameHeight
                        || Config.Fps != nextConfig.Fps;
                    bool themeChanged = Config.Theme != nextConfig.Theme;
                    bool globalKeyChanged = Config.EnableGlobalKeyboard != nextConfig.EnableGlobalKeyboard;
                    bool cameraBarcodeChanged = Config.EnableCameraBarcodeRecognition != nextConfig.EnableCameraBarcodeRecognition;
                    bool workstationChanged = !string.Equals(_activeWorkstationRole, nextConfig.WorkstationRole, StringComparison.OrdinalIgnoreCase);
                    bool aiTtsChanged = Config.EnableAiTts != nextConfig.EnableAiTts
                        || Config.AiTtsEngine != nextConfig.AiTtsEngine;
                    bool webServerChanged = Config.EnableWebServer != nextConfig.EnableWebServer
                        || Config.WebServerPort != nextConfig.WebServerPort
                        || Config.TranscodeCacheMaxMB != nextConfig.TranscodeCacheMaxMB
                        || Config.EnableOrderInfoLog != nextConfig.EnableOrderInfoLog
                        || Config.RequireWebAccessKey != nextConfig.RequireWebAccessKey
                        || !string.Equals(Config.WebAccessKey, nextConfig.WebAccessKey, StringComparison.Ordinal);
                    bool webServerNeedsRecovery = nextConfig.EnableWebServer && _webServer == null;

                    AppConfig.NormalizeAfterLoad(nextConfig);
                    if (!SaveConfig(nextConfig, notifyUser: true))
                        return false;
                    Config = nextConfig;
                    if (cameraBarcodeChanged)
                        ResetCameraBarcodeRecognition();

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

                    ApplyGlobalKeyboardConfig();
                    if (globalKeyChanged && _globalKeyHook != null)
                    {
                        if (Config.EnableGlobalKeyboard)
                            _globalKeyHook.Start();
                        else
                            _globalKeyHook.Stop();
                    }
                    bool webServerApplied = true;
                    bool webServerShouldApply = (webServerChanged || webServerNeedsRecovery) && !workstationChanged;
                    if (webServerShouldApply)
                    {
                        ShowToast("正在应用局域网服务设置...");
                        webServerApplied = await RestartWebServerAsync(allowAccessSetup: Config.EnableWebServer);
                    }
                    else if (!webServerChanged && !webServerNeedsRecovery)
                    {
                        _ = RefreshWorkstationStatusAsync();
                    }

                    if (workstationChanged)
                    {
                        WorkstationNetwork.AskRestart(Application.Current?.MainWindow);
                    }
                    else if (cameraChanged)
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
                        if (!webServerShouldApply || webServerApplied)
                            ShowToast(webServerShouldApply ? "配置已保存，局域网服务已应用" : "提示：配置已保存");
                    }
                    return true;
            }
            catch (Exception ex)
            {
                ShowToast($"设置错误: {ex.Message}");
                return false;
            }
        }

        public async void RunStartupSetupFlowsIfNeeded(System.Windows.Window owner)
        {
            bool isExistingUser = Config.FirstUseWizardCompleted;
            if (!isExistingUser)
            {
                await RunFirstUseSetupWizardIfNeededAsync(owner);
                return;
            }

            RunCameraBarcodeUpgradePromptIfNeeded(owner);
        }

        private async Task RunFirstUseSetupWizardIfNeededAsync(System.Windows.Window owner)
        {
            if (Config.FirstUseWizardCompleted || _isDisposed)
                return;

            bool pausedCamera = false;
            try
            {
                _isSetupWizardActive = true;
                if (!IsRecording)
                {
                    pausedCamera = StopCamera();
                    if (!pausedCamera)
                    {
                        ShowToast("摄像头未能停止，暂时无法打开配置向导");
                        return;
                    }
                }

                var clonedConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
                var wizard = new FirstUseSetupWizardWindow(clonedConfig) { Owner = owner };
                MainWindow setupOwner = owner as MainWindow;
                setupOwner?.SuspendCapsLockForModalWindow();

                bool accepted;
                try
                {
                    accepted = wizard.ShowDialog() == true;
                }
                finally
                {
                    setupOwner?.ResumeCapsLockAfterModalWindow();
                }

                if (!accepted)
                    return;

                AppConfig nextConfig = wizard.WasSkipped ? clonedConfig : wizard.ResultConfig;
                AppConfig.ApplyFirstUseDefaults(nextConfig);
                AppConfig.NormalizeAfterLoad(nextConfig);
                if (!SaveConfig(nextConfig, notifyUser: true))
                    return;
                Config = nextConfig;
                ResetCameraBarcodeRecognition();
                ApplyGlobalKeyboardConfig();
                if (_globalKeyHook != null)
                {
                    if (Config.EnableGlobalKeyboard)
                        _globalKeyHook.Start();
                    else
                        _globalKeyHook.Stop();
                }

                if (Config.EnableWebServer)
                {
                    ShowToast("正在应用局域网服务设置...");
                    if (!await RestartWebServerAsync(allowAccessSetup: true))
                        return;
                }

                ShowToast(wizard.WasSkipped ? "已跳过配置向导" : "配置向导已完成");
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("SetupWizard", "First-use setup wizard failed", ex);
                ShowToast($"配置向导错误: {ex.Message}");
            }
            finally
            {
                _isSetupWizardActive = false;
                if (pausedCamera && !IsRecording && !_isDisposed)
                {
                    _consecutiveRestartFailures = 0;
                    RestartCamera();
                }
            }
        }

        private void RunCameraBarcodeUpgradePromptIfNeeded(System.Windows.Window owner)
        {
            if (_isDisposed || !AppConfig.ShouldPromptCameraBarcodeUpgrade(Config))
                return;

            var dialog = new CameraBarcodeUpgradeDialog { Owner = owner };
            bool enableRecognition = dialog.ShowDialog() == true;
            var nextConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
            AppConfig.ApplyCameraBarcodeUpgradeChoice(nextConfig, enableRecognition);
            if (!SaveConfig(nextConfig, notifyUser: true))
                return;

            Config = nextConfig;
            ResetCameraBarcodeRecognition();
            RuntimeLog.Info("CameraBarcode", $"Upgrade choice saved enabled={Config.EnableCameraBarcodeRecognition}");
            ShowToast(enableRecognition
                ? "已启用摄像头识别面单"
                : "已保留当前设置，可随时在设置中开启");
        }

        public bool SuspendCameraForSetupWizard()
        {
            if (IsRecording || _isDisposed)
                return false;

            _isSetupWizardActive = true;
            if (StopCamera())
                return true;

            _isSetupWizardActive = false;
            ShowToast("摄像头未能停止，暂时无法打开配置向导");
            return false;
        }

        public void ResumeCameraAfterSetupWizard()
        {
            _isSetupWizardActive = false;
            if (IsRecording || _isDisposed)
                return;

            _consecutiveRestartFailures = 0;
            RestartCamera();
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

            var mkvPaths = GetMkvConversionTargets();
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

                // 如果 MP4 已存在，直接删 MKV 并更新数据库；但带 WAV/audio.log 的 MKV 需要重新合并，避免误用录制中生成的半截 MP4。
                if (File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0)
                {
                    if (HasMuxRecoverySidecar(mkvPath))
                    {
                        RuntimeLog.Warn("MkvRecover", $"Existing MP4 ignored because MKV sidecar remains file={fileName}");
                        progress?.Report($"[{i + 1}/{total}] 发现疑似半截 MP4，重新合并: {fileName}");
                    }
                    else
                    {
                        try { File.Delete(mkvPath); } catch { }
                        DeleteAudioTempFile(Path.ChangeExtension(mkvPath, ".wav"));
                        _db.UpdateVideoFilePath(mkvPath, mp4Path);
                        success++;
                        progress?.Report($"[{i + 1}/{total}] MP4 已存在，已清理 MKV: {fileName}");
                        continue;
                    }
                }

                progress?.Report($"[{i + 1}/{total}] 正在转换: {fileName}");

                bool ok = await Task.Run(() =>
                {
                    var result = ConvertMkvToMp4ForPlayback(mkvPath, token);
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

        private List<string> GetMkvConversionTargets()
        {
            var paths = _db?.QueryActiveVideoFilePaths() ?? [];
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                if (path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                {
                    targets.Add(path);
                    continue;
                }

                if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    continue;

                string mkvPath = Path.ChangeExtension(path, ".mkv");
                if (File.Exists(mkvPath) && HasMuxRecoverySidecar(mkvPath))
                {
                    RuntimeLog.Warn("MkvRecover", $"Database points to MP4 but MKV sidecar remains, scheduling recovery file={Path.GetFileName(mkvPath)}");
                    targets.Add(mkvPath);
                }
            }

            return targets.ToList();
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
            // 新用户先完成首次配置向导，再请求局域网权限；已有用户则在权限缺失时自动修复。
            _ = RestartWebServerAsync(allowAccessSetup: ShouldRepairLanAccessAtStartup(Config));

            // 启动时自动将上次断电残留的 MKV 转换为 MP4
            _mkvRecoveryTask = Task.Run(RecoverOrphanedMkvAsync);
        }

        internal static bool ShouldRepairLanAccessAtStartup(AppConfig config)
        {
            return config?.FirstUseWizardCompleted == true && config.EnableWebServer;
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

        public async Task<bool> SaveRecordingsBeforeShutdownAsync(IProgress<string> progress = null)
        {
            if (_isDisposed) return true;
            if (!await _shutdownLock.WaitAsync(0)) return false;

            bool previousBusy = IsBusy;
            string previousBusyText = BusyText;
            bool prepared = false;
            _shutdownRequested = true;
            IsShutdownInProgress = true;
            try
            {
                RuntimeLog.Info("Shutdown", $"Save before shutdown start recording={IsRecording}");
                progress?.Report("正在保存录像，请稍候...");
                IsBusy = true;
                BusyText = "正在关闭程序...";

                if (IsRecording)
                {
                    progress?.Report("正在停止当前录像...");
                    await _recorderLock.WaitAsync();
                    try
                    {
                        if (IsRecording)
                        {
                            _stopReason = "程序退出";
                            PauseSpeechForRecording();
                            await InternalStopRecordingAsync();
                        }
                    }
                    finally
                    {
                        if (!IsRecording)
                            ResumeSpeechWhenCameraIdle();
                        _recorderLock.Release();
                    }
                }

                if (_lastFinalizeTask != null)
                {
                    progress?.Report("正在写入录像记录...");
                    await _lastFinalizeTask;
                }

                if (_postStopMuxTask != null && !_postStopMuxTask.IsCompleted)
                {
                    progress?.Report("正在等待当前录像合成...");
                    await _postStopMuxTask;
                }

                if (_mkvRecoveryTask != null && !_mkvRecoveryTask.IsCompleted)
                {
                    progress?.Report("正在等待后台录像恢复...");
                    await _mkvRecoveryTask;
                }

                progress?.Report("正在合成 MP4 录像...");
                var result = await BatchConvertMkvToMp4Async(progress, CancellationToken.None);
                RuntimeLog.Info("Shutdown", $"Save before shutdown done success={result.success}, fail={result.fail}, skip={result.skip}");

                if (result.fail > 0)
                {
                    ShowToast("部分历史录像转换失败，已保留原文件");
                    RuntimeLog.Warn("Shutdown", $"Save before shutdown has failed historical conversions, allowing exit. failedConversions={result.fail}");
                }

                _shutdownPrepared = true;
                prepared = true;
                return true;
            }
            catch (Exception ex)
            {
                ShowToast("录像保存失败，请检查日志");
                RuntimeLog.Error("Shutdown", "Save before shutdown exception", ex);
                return false;
            }
            finally
            {
                if (!prepared)
                {
                    _shutdownRequested = false;
                    IsShutdownInProgress = false;
                    IsBusy = previousBusy && !_isDisposed;
                    BusyText = previousBusy ? previousBusyText : "";
                }
                else
                {
                    IsBusy = true;
                    BusyText = "正在关闭程序...";
                }
                _shutdownLock.Release();
            }
        }

        private async Task<bool> RestartWebServerAsync(bool allowAccessSetup)
        {
            await _webServerLifecycleLock.WaitAsync();
            WebServer newServer = null;
            try
            {
                Interlocked.Increment(ref _workstationAddressRefreshVersion);
                WebServer previousServer = _webServer;
                _webServer = null;
                try { previousServer?.Dispose(); } catch { }

                if (!Config.EnableWebServer || _db == null || _isDisposed)
                {
                    MonitorAccessAddress = "";
                    WorkstationAccessText = "其他电脑查视频：未开启";
                    WorkstationPrintStatusText = "快递单打印工位：未连接";
                    WorkstationStatusToolTip = "开启后，其他电脑可在浏览器输入这里显示的网址，搜索、下载和播放打包视频。";
                    return true;
                }

                WorkstationAccessText = "其他电脑查视频：正在启动服务...";
                WorkstationPrintStatusText = "快递单打印工位：等待服务启动";
                int port = Config.WebServerPort;
                int cacheMaxMb = Config.TranscodeCacheMaxMB;
                bool enableOrderInfoLog = Config.EnableOrderInfoLog;
                bool requireAccessKey = Config.RequireWebAccessKey;
                string accessKey = Config.WebAccessKey;

                newServer = await Task.Run(() =>
                {
                    var server = new WebServer(
                        _db,
                        port,
                        cacheMaxMb,
                        () => IsRecording,
                        ConvertRecordMkvToMp4,
                        () => _currentVideoFilePath,
                        requireAccessKey,
                        accessKey)
                    {
                        EnableOrderInfoLog = enableOrderInfoLog
                    };
                    try
                    {
                        server.OrderInfoReceived += OnOrderInfoReceived;
                        server.Start(allowAccessSetup);
                        return server;
                    }
                    catch
                    {
                        server.Dispose();
                        throw;
                    }
                });

                if (_isDisposed)
                {
                    newServer.Dispose();
                    return false;
                }

                _webServer = newServer;
                newServer = null;
                await RefreshWorkstationStatusAsync();
                RuntimeLog.Info("Web", $"LAN service started port={port}, cacheMaxMB={cacheMaxMb}");
                return true;
            }
            catch (Exception ex)
            {
                try { newServer?.Dispose(); } catch { }
                RuntimeLog.Error("Web", "LAN service start failed", ex);
                MonitorAccessAddress = "";
                WorkstationAccessText = "其他电脑查视频：暂时不可用";
                WorkstationPrintStatusText = "快递单打印工位：Web 启动失败";
                WorkstationStatusToolTip = $"其他电脑暂时无法连接这台摄像头监控工位。\n{ex.Message}";
                ShowToast($"警告：局域网服务启动失败: {ex.Message}");
                return false;
            }
            finally
            {
                _webServerLifecycleLock.Release();
            }
        }

        private async Task RefreshWorkstationStatusAsync()
        {
            int version = Interlocked.Increment(ref _workstationAddressRefreshVersion);
            if (_webServer == null)
            {
                MonitorAccessAddress = "";
                WorkstationAccessText = "其他电脑查视频：暂时不可用";
                WorkstationPrintStatusText = "快递单打印工位：未连接";
                WorkstationStatusToolTip = "其他电脑暂时无法连接这台摄像头监控工位。";
                return;
            }

            MonitorAccessAddress = "";
            WorkstationAccessText = "其他电脑查视频：正在准备网址...";
            WorkstationPrintStatusText = "快递单打印工位：等待连接";
            WorkstationStatusToolTip = "正在准备给其他电脑浏览器使用的网址。两台电脑需要在同一局域网内。";

            string verifiedAddress;
            try
            {
                verifiedAddress = await WorkstationNetwork.GetVerifiedLocalAccessAddressAsync(Config.WebServerPort);
            }
            catch
            {
                verifiedAddress = WorkstationNetwork.GetBestLocalAccessAddress(Config.WebServerPort);
            }

            if (version != _workstationAddressRefreshVersion || _webServer == null)
                return;

            MonitorAccessAddress = verifiedAddress;
            WorkstationAccessText = Config.RequireWebAccessKey
                ? AppLanguage.Format("Main.RecordingAccessProtected", MonitorAccessAddress)
                : AppLanguage.Format("Main.RecordingAccess", MonitorAccessAddress);
            WorkstationPrintStatusText = "快递单打印工位：等待连接";
            WorkstationStatusToolTip = Config.RequireWebAccessKey
                ? "访问保护已开启。请点击底部地址复制完整访问链接，再发送到需要查看录像的设备。"
                : $"其他电脑在浏览器输入 http://{MonitorAccessAddress}，即可搜索、下载和播放视频。若打不开，请确认两台电脑在同一局域网，并检查防火墙。";
        }

        public void CopyMonitorAddress()
        {
            if (string.IsNullOrWhiteSpace(MonitorAccessAddress))
            {
                ShowToast("当前没有可复制的访问地址");
                return;
            }

            string url = BuildMonitorAccessUrl();
            bool copied = false;
            for (int i = 0; i < 3 && !copied; i++)
            {
                try
                {
                    Clipboard.SetDataObject(url, true);
                    copied = true;
                }
                catch
                {
                    Thread.Sleep(80);
                }
            }

            bool opened = WorkstationNetwork.TryOpenUrl(url, out string openError);
            if (copied && opened)
                ShowToast("已复制并打开监控网页");
            else if (copied)
                ShowToast($"已复制地址，打开网页失败: {openError}");
            else if (opened)
                ShowToast("已打开监控网页，复制失败请重试");
            else
                ShowToast($"复制和打开都失败: {openError}");
        }

        private string BuildMonitorAccessUrl()
        {
            string url = WorkstationNetwork.ToUrl(MonitorAccessAddress);
            if (!Config.RequireWebAccessKey || string.IsNullOrWhiteSpace(Config.WebAccessKey))
                return url;
            return $"{url}/?key={Uri.EscapeDataString(Config.WebAccessKey)}";
        }

        public void SwitchWorkstation()
        {
            var selector = new WorkstationSelectionWindow { Owner = Application.Current?.MainWindow };
            if (selector.ShowDialog() == true && !string.IsNullOrWhiteSpace(selector.SelectedRole))
            {
                if (string.Equals(_activeWorkstationRole, selector.SelectedRole, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(Config.WorkstationRole, _activeWorkstationRole, StringComparison.OrdinalIgnoreCase))
                    {
                        string currentRoleBeforeSave = Config.WorkstationRole;
                        Config.WorkstationRole = _activeWorkstationRole;
                        if (!SaveConfig(notifyUser: true))
                        {
                            Config.WorkstationRole = currentRoleBeforeSave;
                            return;
                        }
                    }
                    ShowToast($"当前已经是{WorkstationRoles.GetDisplayName(_activeWorkstationRole)}");
                    return;
                }

                string previousRole = Config.WorkstationRole;
                Config.WorkstationRole = selector.SelectedRole;
                if (!SaveConfig(notifyUser: true))
                {
                    Config.WorkstationRole = previousRole;
                    return;
                }
                WorkstationNetwork.AskRestart(Application.Current?.MainWindow);
            }
        }

        /// <summary>收到油猴脚本推送的订单信息时，提前生成 TTS 缓存</summary>
        private void OnOrderInfoReceived(List<OrderInfo> orders)
        {
            if (orders == null) return;

            bool hasTestOrder = orders.Any(x => x.IsTest);
            string printStatusText = hasTestOrder
                ? AppLanguage.Format("Main.PrintTestOrder", DateTime.Now.ToString("HH:mm"))
                : orders.Count == 0
                    ? AppLanguage.Format("Main.PrintNoRefund", DateTime.Now.ToString("HH:mm"))
                    : AppLanguage.Format("Main.PrintOrders", DateTime.Now.ToString("HH:mm"), orders.Count);
            Application application = Application.Current;
            if (application != null)
            {
                _ = application.Dispatcher.InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    if (_webServer != null)
                    {
                        if (!string.IsNullOrWhiteSpace(MonitorAccessAddress))
                            WorkstationAccessText = Config.RequireWebAccessKey
                                ? AppLanguage.Format("Main.RecordingAccessProtected", MonitorAccessAddress)
                                : AppLanguage.Format("Main.RecordingAccess", MonitorAccessAddress);
                        WorkstationPrintStatusText = printStatusText;
                    }

                    string activeOrderId = IsRecording ? _recordingOrderId : CurrentOrderId;
                    OrderInfo activeOrder = orders.FirstOrDefault(info =>
                        !info.IsTest
                        && string.Equals(info.TrackingNumber?.Trim(), activeOrderId?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (IsRecording && activeOrder != null)
                        SetPreviewOrderNotice(activeOrder);

                    if (hasTestOrder)
                    {
                        ShowToast("已收到测试订单");
                        SpeakWithRemarkTone(DefaultSpeechCatalog.TestOrderReceived, cancelPrevious: false);
                    }
                });
            }

            if (orders.Count == 0) return;

            var realOrders = orders.Where(x => !x.IsTest).ToList();
            if (realOrders.Count == 0)
                return;

            if (_alertService == null) return;
            if (Config.EnablePrintedRefundAlert)
            {
                foreach (string statusText in realOrders
                    .Where(info => info.IsPrintedRefund)
                    .Select(GetRefundStatusDisplayText)
                    .Distinct())
                {
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreatePrintedRefundAnnouncement(statusText), AlertVoiceStyle.Warning);
                }
            }
            if (!Config.EnableOrderInfoAnnounce) return;
            foreach (var info in realOrders)
            {
                if (Config.AnnounceBuyerMessage && !string.IsNullOrWhiteSpace(info.BuyerMessage))
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateBuyerMessageAnnouncement(info.BuyerMessage));
                if (Config.AnnounceSellerMemo && !string.IsNullOrWhiteSpace(info.SellerMemo))
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateSellerMemoAnnouncement(info.SellerMemo));
                if (Config.AnnounceProductInfo && !string.IsNullOrWhiteSpace(info.ProductInfo))
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateProductAnnouncement(info.ProductInfo));
            }
        }

        private void RestartCamera()
        {
            if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
            {
                RuntimeLog.Info("Camera", "RestartCamera skipped while setup wizard owns camera");
                return;
            }

            // 阻止并发重启
            if (_isRestartingCamera) return;
            _isRestartingCamera = true;
            try
            {
                RuntimeLog.Warn("Camera", $"RestartCamera start recording={IsRecording}, failures={_consecutiveRestartFailures}");
                if (!StopCamera())
                {
                    RuntimeLog.Warn("Camera", "RestartCamera aborted because previous camera did not stop");
                    ShowToast("摄像头停止失败，请重新插拔后重试");
                    return;
                }
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
            if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
            {
                RuntimeLog.Info("Camera", "RestartCameraWithRecordingStop skipped while setup wizard owns camera");
                return;
            }

            if (_isRestartingCamera) return;
            _isRestartingCamera = true;
            try
            {
                RuntimeLog.Warn("Camera", $"RestartCameraWithRecordingStop start recording={IsRecording}, failures={_consecutiveRestartFailures}");
                if (IsRecording)
                {
                    _stopReason = "摄像头重连";
                    RuntimeLog.Warn("Camera", "Camera reconnect requested while recording, stopping current recording before restart");
                    await SafeStopRecordingAsync();

                    if (!StopCamera())
                    {
                        ShowToast("摄像头停止失败，未继续重连");
                        return;
                    }
                    StartCamera();
                    _lastRestartAttempt = DateTime.Now;

                    if (_videoSource != null && _videoSource.IsRunning)
                    {
                        _consecutiveRestartFailures = 0;
                        RuntimeLog.Info("Camera", "Camera reconnected after stopping interrupted recording");
                        ShowToast("摄像头已重连，当前录像已保存，请重新扫码继续");
                        Speak(DefaultSpeechCatalog.CameraConnected);
                    }
                    else
                    {
                        _consecutiveRestartFailures++;
                        if (_consecutiveRestartFailures >= MaxConsecutiveRestartFailures)
                        {
                            RuntimeLog.Warn("Camera", $"Camera reconnect failed {_consecutiveRestartFailures} times after interrupted recording");
                            ShowToast($"警告：摄像头连续 {MaxConsecutiveRestartFailures} 次重连失败，录制已停止。请重新插拔后在设置中手动重启。");
                            SpeakWarning(DefaultSpeechCatalog.ReconnectCamera, 3);
                            Debug.WriteLine($"[Camera] 录制中连续 {_consecutiveRestartFailures} 次重连失败，停止录制和自动重连");
                        }
                        else
                        {
                            SpeakWarning(DefaultSpeechCatalog.CameraDisconnected);
                        }
                    }
                }
                else
                {
                    // 非录制状态：原有逻辑
                    if (!StopCamera())
                    {
                        ShowToast("摄像头停止失败，未继续重连");
                        return;
                    }
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
                            SpeakWarning(DefaultSpeechCatalog.ReconnectCamera, 3);
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
            if (_isSetupWizardActive)
                return;

            if (_isCameraSleeping)
            {
                IsCameraSleeping = false;
                _consecutiveRestartFailures = 0;
                RuntimeLog.Info("Camera", "Wake requested by user activity");
                StartCamera();
                ShowToast("摄像头已唤醒");
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
                if (_isDisposed || _shutdownRequested) break;
                if (!Config.EnableCameraIdle || Config.CameraIdleMinutes <= 0) continue;
                if (_isSetupWizardActive) continue;
                if (IsRecording || _isCameraSleeping) continue;

                double idleMinutes = (DateTime.Now - _lastActivityTime).TotalMinutes;
                if (idleMinutes >= Config.CameraIdleMinutes)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => {
                        if (_isCameraSleeping || IsRecording || _isSetupWizardActive) return; // 再次检查防止竞态
                        if (!StopCamera())
                        {
                            ShowToast("摄像头未能进入休眠，请重新插拔后重试");
                            return;
                        }
                        IsCameraSleeping = true; // SetProperty 会同时更新字段并触发 PropertyChanged
                        VideoFrame = null;
                        ShowToast($"提示：摄像头已休眠（空闲{Config.CameraIdleMinutes}分钟）");
                        Debug.WriteLine($"[Idle] 摄像头休眠: 空闲{idleMinutes:F1}分钟");
                        RuntimeLog.Info("MkvRecover", "Camera idle, start pending MKV conversion");
                        _mkvRecoveryTask = Task.Run(RecoverOrphanedMkvAsync);
                    });
                }
            }
        }

        private DateTime _lastFrameTime = DateTime.MinValue;

        private int BeginPreviewSession(bool clearFrame)
        {
            int sessionId = _previewSessionGate.BeginSession();
            _lastPreviewFrameAt = DateTime.MinValue;
            _lastPreviewPublishedAt = DateTime.Now;
            _lastPreviewFreezeLogAt = DateTime.Now;

            if (clearFrame)
            {
                _cameraFrameReady.BeginSession();
                var dispatcher = Application.Current?.Dispatcher;
                void ClearPreview()
                {
                    if (!_previewSessionGate.IsCurrent(sessionId)) return;
                    _previewWriteableBitmap = null;
                    VideoFrame = null;
                }

                if (dispatcher == null || dispatcher.CheckAccess())
                    ClearPreview();
                else
                    _ = dispatcher.BeginInvoke(new Action(ClearPreview));
            }

            return sessionId;
        }

        private void ReleasePreviewUpdate(int sessionId)
        {
            _previewSessionGate.Release(sessionId);
        }

        private async Task<bool> WaitForCameraFrameAsync(TimeSpan timeout)
        {
            if (_isDisposed || !await _cameraFrameReady.WaitAsync(timeout))
                return false;

            lock (_frameLock)
            {
                return _latestFrame != null && !_latestFrame.IsDisposed && !_latestFrame.Empty();
            }
        }

        private void StartCamera()
        {
            try
            {
                if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
                {
                    RuntimeLog.Info("Camera", "StartCamera skipped while setup wizard owns camera");
                    return;
                }

                if (_videoSource != null)
                {
                    RuntimeLog.Warn("Camera", $"StartCamera skipped because previous source still exists, running={_videoSource.IsRunning}");
                    return;
                }

                int previewSessionId = BeginPreviewSession(clearFrame: true);

                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (videoDevices.Count == 0) 
                { 
                    RuntimeLog.Warn("Camera", "StartCamera found no video devices");
                    ShowToast("警告：未检测到任何摄像头");
                    SpeakWarning(DefaultSpeechCatalog.CameraNotDetected);
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
                    if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
                        return;
                    _ = Application.Current.Dispatcher.InvokeAsync(() => {
                        if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
                            return;
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
                RuntimeLog.Info("Camera", $"StartCamera success {Config.FrameWidth}x{Config.FrameHeight}@{_actualCameraFps}, running={_videoSource.IsRunning}, previewSession={previewSessionId}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Camera", "StartCamera failed", ex);
                ShowToast("摄像头启动失败");
            }
        }

        private bool StopCamera()
        {
            if (!_isDisposed)
                ResetCameraBarcodeRecognition();

            VideoCaptureDevice source = _videoSource;
            if (source != null)
            {
                RuntimeLog.Info("Camera", $"StopCamera running={source.IsRunning}");
                try { source.NewFrame -= VideoSource_NewFrame; } catch { }
                try
                {
                    if (source.IsRunning)
                    {
                        source.SignalToStop();
                        for (int i = 0; i < 50 && source.IsRunning; i++)
                            Thread.Sleep(100);
                    }
                }
                catch (SEHException) { /* AForge COM cleanup on some laptops */ }
                catch (Exception ex) { RuntimeLog.Warn("Camera", $"Graceful camera stop failed: {ex.Message}"); }

                if (source.IsRunning)
                {
                    RuntimeLog.Warn("Camera", "Graceful camera stop timed out, forcing stop");
                    if (_cameraForceStopTask == null || _cameraForceStopTask.IsCompleted)
                        _cameraForceStopTask = Task.Run(() => source.Stop());

                    try { _cameraForceStopTask.Wait(2000); }
                    catch (Exception ex) { RuntimeLog.Warn("Camera", $"Forced camera stop failed: {ex.GetBaseException().Message}"); }
                }

                if (source.IsRunning)
                {
                    RuntimeLog.Error("Camera", "Camera source is still running after forced stop");
                    return false;
                }

                if (ReferenceEquals(_videoSource, source))
                    _videoSource = null;
                _cameraForceStopTask = null;
            }
            lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = null; }
            BeginPreviewSession(clearFrame: true);
            RuntimeLog.Info("Camera", "StopCamera completed");
            return true;
        }
        
        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs) { try { Mat newMat = BitmapToMat(eventArgs.Frame); lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = newMat; } _lastFrameTime = DateTime.Now; _cameraFrameReady.Signal(); } catch (Exception ex) { RuntimeLog.Error("Camera", "NewFrame conversion failed", ex); } }

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
                        TrySubmitCameraBarcodeFrame(currentFrame);
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

                        bool previewFrameDue = IsPreviewFrameDue();

                        // 非录制状态只为真正要发布的预览帧绘制水印，避免按摄像头满帧率克隆整帧。
                        if (Config.EnableWatermark && (IsRecording || previewFrameDue))
                        {
                            try
                            {
                                if (processedFrame == currentFrame)
                                {
                                    processedFrame = currentFrame.Clone();
                                }
                                string line1 = FormatWatermarkTimestamp(DateTimeOffset.Now);
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

                        if (frameTickCounter % 30 == 0) TryPerformMotionDetection(currentFrame);
                        if (previewFrameDue)
                            PublishPreviewFrameIfDue(processedFrame);

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
                        LogResourceHealthIfDue("video-loop");
                    }
                    else
                    {
                        // 休眠期间不做任何自动重连操作
                        if (_isCameraSleeping || _isSetupWizardActive)
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
                        else if (IsVideoSourceRunning())
                        {
                            double noFrameSeconds = (DateTime.Now - _lastFrameTime).TotalSeconds;
                            if (noFrameSeconds > 1.5)
                            {
                                Debug.WriteLine($"[Camera] 信号丢失 {noFrameSeconds:F1}s，尝试重连 (失败次数={_consecutiveRestartFailures})");
                                _ = Application.Current.Dispatcher.InvokeAsync(() => {
                                    ShowToast("警告：摄像头信号丢失，尝试重连...");
                                    SpeakWarning(DefaultSpeechCatalog.CameraReconnecting);
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
                                    SpeakWarning(DefaultSpeechCatalog.CameraReconnecting);
                                    RestartCameraWithRecordingStop();
                                });
                            }
                        }

                        // 摄像头休眠后无需高频轮询；用户活动仍会通过 NotifyUserActivity 立即 StartCamera。
                        int idleDelayMs = _isCameraSleeping ? 1000 : 200;
                        await Task.Delay(idleDelayMs, token);
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
                                Speak(DefaultSpeechCatalog.MotionDetected);
                            }

                            // 即将超时语音提示（确保预警阈值合理：超时总时长 + 5s）
                            if (!_autoStopWarned && Config.EnableAutoStop
                                && autoStopTotalSec > warnSec + 5
                                && motionIdleSec >= autoStopTotalSec - warnSec)
                            {
                                _autoStopWarned = true;
                                SpeakWarning(DefaultSpeechCatalog.MotionTimeoutWarning);
                            }
                            if (!_maxDurationWarned && Config.EnableMaxDuration
                                && maxDurTotalSec > warnSec * 2
                                && elapsedSec >= maxDurTotalSec - warnSec)
                            {
                                _maxDurationWarned = true;
                                SpeakWarning(DefaultSpeechCatalog.RecordingDurationWarning);
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
                                SpeakWarning(DefaultSpeechCatalog.MotionTimeoutStopped);
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
                                SpeakWarning(DefaultSpeechCatalog.RecordingDurationStopped);
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
                RuntimeLog.Error("VideoProcess", "VideoProcessLoop crashed, restarting", ex);
                if (!token.IsCancellationRequested && !_isDisposed)
                {
                    try { await Task.Delay(500, token); } catch (OperationCanceledException) { return; }
                    if (!token.IsCancellationRequested && !_isDisposed)
                    {
                        _videoTask = Task.Run(() => VideoProcessLoop(token), token);
                    }
                }
            }
        }

        private void CheckPreviewWatchdog()
        {
            if (_isDisposed || _isCameraSleeping || SuppressVideoPreviewUpdates) return;
            if (!IsVideoSourceRunning() || !_cameraEverConnected) return;
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
                    LogResourceHealthIfDue("preview-no-frame", force: true);
                }
                return;
            }

            if (sinceLastPreview < PreviewFreezeWarnThreshold) return;
            TimeSpan uiHeartbeatAge = now - _lastUiHeartbeatAt;
            if (_lastUiHeartbeatAt != DateTime.MinValue && uiHeartbeatAge > UiHeartbeatStaleThreshold)
            {
                if (now - _lastPreviewFreezeLogAt > PreviewFreezeWarnThreshold)
                {
                    _lastPreviewFreezeLogAt = now;
                    RuntimeLog.Warn("Preview", $"Preview publish delayed because UI dispatcher is busy for {uiHeartbeatAge.TotalSeconds:F1}s, frame age={sinceLastFrame.TotalSeconds:F1}s, preview age={sinceLastPreview.TotalSeconds:F1}s, recording={IsRecording}");
                    LogResourceHealthIfDue("preview-ui-busy", force: true);
                }
                return;
            }

            int queueCount = -1;
            try { queueCount = _videoWriteQueue?.Count ?? -1; } catch { }
            string writeTaskStatus = _writeTask == null ? "null" : _writeTask.Status.ToString();

            if (now - _lastPreviewFreezeLogAt > PreviewFreezeWarnThreshold)
            {
                _lastPreviewFreezeLogAt = now;
                RuntimeLog.Warn("Preview", $"Preview stale for {sinceLastPreview.TotalSeconds:F1}s while frames are fresh ({sinceLastFrame.TotalSeconds:F1}s), pending={(_previewSessionGate.IsPending ? 1 : 0)}, recording={IsRecording}, queue={queueCount}, writeTask={writeTaskStatus}");
                LogResourceHealthIfDue("preview-stale", force: true);
            }

            if (sinceLastPreview < PreviewFreezeRestartThreshold) return;
            if (_isRestartingCamera) return;
            if (now - _lastPreviewWatchdogRestartAt < PreviewFreezeRestartCooldown) return;

            _lastPreviewWatchdogRestartAt = now;
            RuntimeLog.Warn("Preview", $"Preview frozen for {sinceLastPreview.TotalSeconds:F1}s, restarting camera. recording={IsRecording}, queue={queueCount}, writeTask={writeTaskStatus}");
            LogResourceHealthIfDue("preview-restart", force: true);
            _previewSessionGate.ClearCurrentPending();
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

        private bool IsPreviewFrameDue()
        {
            return !SuppressVideoPreviewUpdates
                && !_isDisposed
                && DateTime.UtcNow - _lastPreviewFrameAt >= PreviewFrameInterval
                && !_previewSessionGate.IsPending;
        }

        private void PublishPreviewFrameIfDue(Mat frame)
        {
            if (SuppressVideoPreviewUpdates || _isDisposed) return;

            DateTime now = DateTime.UtcNow;
            if (now - _lastPreviewFrameAt < PreviewFrameInterval) return;

            if (!_previewSessionGate.TryAcquire(out int previewSessionId)) return;
            _lastPreviewFrameAt = now;

            Mat previewFrame = null;
            try
            {
                previewFrame = frame.Clone();

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    previewFrame.Dispose();
                    ReleasePreviewUpdate(previewSessionId);
                    return;
                }

                Mat frameToPublish = previewFrame;
                previewFrame = null;
                _ = dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!_isDisposed
                            && !SuppressVideoPreviewUpdates
                            && _previewSessionGate.IsCurrent(previewSessionId))
                        {
                            if (_previewWriteableBitmap == null
                                || _previewWriteableBitmap.PixelWidth != frameToPublish.Width
                                || _previewWriteableBitmap.PixelHeight != frameToPublish.Height)
                            {
                                _previewWriteableBitmap = new WriteableBitmap(
                                    frameToPublish.Width,
                                    frameToPublish.Height,
                                    96,
                                    96,
                                    System.Windows.Media.PixelFormats.Bgr24,
                                    null);
                                VideoFrame = _previewWriteableBitmap;
                            }

                            int stride = checked((int)frameToPublish.Step());
                            int bufferSize = checked(stride * frameToPublish.Height);
                            _previewWriteableBitmap.WritePixels(
                                new Int32Rect(0, 0, frameToPublish.Width, frameToPublish.Height),
                                frameToPublish.Data,
                                bufferSize,
                                stride);
                            _lastPreviewPublishedAt = DateTime.Now;
                        }
                    }
                    finally
                    {
                        frameToPublish.Dispose();
                        ReleasePreviewUpdate(previewSessionId);
                    }
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
            catch
            {
                previewFrame?.Dispose();
                ReleasePreviewUpdate(previewSessionId);
                if (DateTime.Now - _lastPreviewConvertErrorLogAt > TimeSpan.FromSeconds(30))
                {
                    _lastPreviewConvertErrorLogAt = DateTime.Now;
                    RuntimeLog.Warn("Preview", $"Preview bitmap conversion failed, {BuildResourceHealthSnapshot()}");
                }
            }
        }

        private void LogResourceHealthIfDue(string reason, bool force = false)
        {
            DateTime now = DateTime.Now;
            if (!force && now - _lastResourceHealthLogAt < ResourceHealthLogInterval)
                return;

            _lastResourceHealthLogAt = now;
            RuntimeLog.Info("Health", $"{reason}: {BuildResourceHealthSnapshot()}");
        }

        private string BuildResourceHealthSnapshot()
        {
            int videoQueueCount = -1;
            int audioQueueCount = -1;
            try { videoQueueCount = _videoWriteQueue?.Count ?? -1; } catch { }
            try { audioQueueCount = _audioWriteQueue?.Count ?? -1; } catch { }

            double frameAge = _lastFrameTime == DateTime.MinValue ? -1 : (DateTime.Now - _lastFrameTime).TotalSeconds;
            double previewAge = _lastPreviewPublishedAt == DateTime.MinValue ? -1 : (DateTime.Now - _lastPreviewPublishedAt).TotalSeconds;
            double uiAge = _lastUiHeartbeatAt == DateTime.MinValue ? -1 : (DateTime.Now - _lastUiHeartbeatAt).TotalSeconds;

            try
            {
                using var process = Process.GetCurrentProcess();
                long managedMb = GC.GetTotalMemory(false) / 1024 / 1024;
                long workingSetMb = process.WorkingSet64 / 1024 / 1024;
                long privateMb = process.PrivateMemorySize64 / 1024 / 1024;
                return $"ws={workingSetMb}MB, private={privateMb}MB, managed={managedMb}MB, handles={process.HandleCount}, threads={process.Threads.Count}, gc0={GC.CollectionCount(0)}, gc1={GC.CollectionCount(1)}, gc2={GC.CollectionCount(2)}, frameAge={frameAge:F1}s, previewAge={previewAge:F1}s, uiAge={uiAge:F1}s, pending={(_previewSessionGate.IsPending ? 1 : 0)}, recording={IsRecording}, videoQueue={videoQueueCount}, audioQueue={audioQueueCount}";
            }
            catch (Exception ex)
            {
                return $"health unavailable: {ex.Message}, frameAge={frameAge:F1}s, previewAge={previewAge:F1}s, uiAge={uiAge:F1}s, pending={(_previewSessionGate.IsPending ? 1 : 0)}, recording={IsRecording}, videoQueue={videoQueueCount}, audioQueue={audioQueueCount}";
            }
        }

        private bool IsVideoSourceRunning()
        {
            var source = _videoSource;
            if (source == null) return false;

            try
            {
                return source.IsRunning;
            }
            catch (Exception ex) when (ex is ThreadStateException || ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                if (DateTime.Now - _lastCameraStateErrorLogAt > TimeSpan.FromSeconds(30))
                {
                    _lastCameraStateErrorLogAt = DateTime.Now;
                    RuntimeLog.Warn("Camera", $"Read camera running state failed: {ex.GetType().Name}: {ex.Message}");
                }
                return false;
            }
        }

        private void TryPerformMotionDetection(Mat currentFrame)
        {
            try
            {
                if (currentFrame == null || currentFrame.IsDisposed || currentFrame.Empty()) return;
                PerformMotionDetection(currentFrame);
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is OpenCvSharpException || ex is AccessViolationException)
            {
                if (DateTime.Now - _lastVideoFrameErrorLogAt > TimeSpan.FromSeconds(30))
                {
                    _lastVideoFrameErrorLogAt = DateTime.Now;
                    RuntimeLog.Warn("VideoProcess", $"Motion detection skipped one frame: {ex.GetType().Name}: {ex.Message}");
                }
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

        private void Speak(string text, bool cancelPrevious = true) => PublishVoice(
            text,
            AlertVoiceStyle.Normal,
            AlertSound.None,
            repeatCount: 1,
            interruptCurrent: cancelPrevious);

        private void SpeakWithRemarkTone(string text, bool cancelPrevious = true) => PublishVoice(
            text,
            AlertVoiceStyle.Normal,
            AlertSound.Remark,
            repeatCount: 1,
            interruptCurrent: cancelPrevious);

        private void SpeakWarning(string text, int repeatCount = 1, bool cancelPrevious = true, bool playTonePerRepeat = false) => PublishVoice(
            text,
            AlertVoiceStyle.Warning,
            AlertSound.Warning,
            repeatCount: repeatCount,
            interruptCurrent: cancelPrevious,
            soundRepeatCount: playTonePerRepeat ? repeatCount : 1);

        private void PublishVoice(
            string text,
            AlertVoiceStyle voiceStyle,
            AlertSound sound,
            int repeatCount,
            bool interruptCurrent,
            int soundRepeatCount = 1)
        {
            _alertService?.Publish(new AlertRequest
            {
                SpeechText = text,
                VoiceStyle = voiceStyle,
                Sound = sound,
                SoundRepeatCount = soundRepeatCount,
                SpeechRepeatCount = repeatCount,
                InterruptCurrent = interruptCurrent,
                DisplayDuration = TimeSpan.Zero
            });
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _shutdownRequested = true;
            _isDisposed = true;
            _cts?.Cancel();
            _cameraBarcodeFeedbackCts?.Cancel();
            _previewAlertCts?.Cancel();
            try { _cameraBarcodeRecognition?.Dispose(); } catch { }
            try { _uiHeartbeatTimer?.Stop(); } catch { }
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

            // 正常关闭流程已异步等待录像收尾，不在 UI 关闭阶段重复阻塞。
            if (!_shutdownPrepared)
            {
                try { _lastFinalizeTask?.Wait(5000); } catch { }
                try { _postStopMuxTask?.Wait(5000); } catch { }
            }

            // 录制中退出：更新数据库并转换 MP4
            if (!string.IsNullOrEmpty(videoFileToConvert) && File.Exists(videoFileToConvert))
            {
                try
                {
                    long fileSize = GetCompletedRecordingSizeBytes(videoFileToConvert, audioFileToConvert);
                    long minFileSizeBytes = GetMinVideoFileSizeBytes();
                    bool tooSmall = minFileSizeBytes > 0 && fileSize < minFileSizeBytes;
                    if (!tooSmall && recordId > 0)
                    {
                        int durSec = Math.Max(1, (int)(DateTime.Now - recordStart).TotalSeconds);
                        _db?.UpdateVideoRecordOnStop(recordId, DateTime.Now, durSec, fileSize, _stopReason, _currentVideoCodec, _currentVideoEncoder);
                        RuntimeLog.Info("Recording", $"Exit finalized MKV, queued for startup/web conversion: {Path.GetFileName(videoFileToConvert)}");
                    }
                    else
                    {
                        if (tooSmall && recordId > 0)
                        {
                            string deleteReason = $"文件过小，小于 {FormatMinVideoFileSize(Config.MinVideoFileSizeKB)}";
                            int durSec = Math.Max(1, (int)(DateTime.Now - recordStart).TotalSeconds);
                            _db?.UpdateVideoRecordOnStop(recordId, DateTime.Now, durSec, fileSize, deleteReason, _currentVideoCodec, _currentVideoEncoder);
                            if (DeleteVideoFileForRule(videoFileToConvert, deleteReason))
                                _db?.MarkVideoDeleted(videoFileToConvert, deleteReason);
                        }
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
            _alertService?.Dispose();
            _speechService?.Dispose();
            _speechService = null;
            try { _globalKeyHook?.Dispose(); } catch { }
            try { _webServer?.Dispose(); } catch { }
            try { _db?.Dispose(); } catch { }
        }
    }
}
