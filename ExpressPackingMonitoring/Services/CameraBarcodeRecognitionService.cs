using ExpressPackingMonitoring.Logging;
using OpenCvSharp;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Services;

internal enum CameraBarcodeRecognitionState
{
    Idle,
    Candidate,
    Confirmed
}

internal sealed record CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState State, string Code = "");

internal sealed record CameraBarcodeObservation(
    string CandidateCode = "",
    string ConfirmedCode = "",
    bool KeepDecoding = false);

internal enum BarcodeRecordingDecisionAction
{
    Ignore,
    Queue,
    Start,
    Stop,
    Switch,
    ClearInput,
    SwitchToShipping,
    SwitchToReturn,
    ToggleRecording
}

internal enum BarcodeRecordingDecisionReason
{
    Ready,
    CannotProcess,
    EmptyInput,
    CameraCurrentCodeIgnored,
    CooldownOrderQueued,
    CooldownIgnored,
    ClearCommand,
    ShippingCommand,
    ReturnCommand,
    StartCommand,
    StopCommand,
    RecordingOrderMissing,
    RecordingOrderMismatch,
    SameCodeMatched,
    InvalidOrderNumber
}

internal sealed record BarcodeRecordingDecision(
    BarcodeRecordingDecisionAction Action,
    BarcodeRecordingDecisionReason Reason,
    string NormalizedValue);

internal static class BarcodeRecordingDecisionPolicy
{
    public static BarcodeRecordingDecision Evaluate(
        string? value,
        bool fromCamera,
        bool canProcess,
        bool isRecording,
        string? recordingOrderId,
        bool sameBarcodeStopEnabled,
        bool inputOnCooldown,
        string? orderIdRegex)
    {
        if (!canProcess)
            return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.CannotProcess, value);

        string normalized = (value ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.EmptyInput, normalized);

        if (fromCamera
            && CameraBarcodeCandidatePolicy.ShouldIgnoreCurrentRecordingCode(
                normalized,
                recordingOrderId,
                isRecording,
                sameBarcodeStopEnabled))
        {
            return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.CameraCurrentCodeIgnored, normalized);
        }

        if (inputOnCooldown)
        {
            return IsOrderScan(normalized, orderIdRegex)
                ? Create(BarcodeRecordingDecisionAction.Queue, BarcodeRecordingDecisionReason.CooldownOrderQueued, normalized)
                : Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.CooldownIgnored, normalized);
        }

        if (normalized.Contains("CLEAR") || normalized.Contains("清除"))
            return Create(BarcodeRecordingDecisionAction.ClearInput, BarcodeRecordingDecisionReason.ClearCommand, normalized);
        if (normalized.Contains("SHIP") || normalized.Contains("发货") || normalized.Contains("FAHUO"))
            return Create(BarcodeRecordingDecisionAction.SwitchToShipping, BarcodeRecordingDecisionReason.ShippingCommand, normalized);
        if (normalized.Contains("BACK") || normalized.Contains("退货") || normalized.Contains("TUIHUO"))
            return Create(BarcodeRecordingDecisionAction.SwitchToReturn, BarcodeRecordingDecisionReason.ReturnCommand, normalized);
        if (normalized.Contains("START") || normalized.Contains("开始录制"))
            return Create(BarcodeRecordingDecisionAction.ToggleRecording, BarcodeRecordingDecisionReason.StartCommand, normalized);
        if (normalized.Contains("STOP") || normalized.Contains("停止录制"))
            return Create(BarcodeRecordingDecisionAction.Stop, BarcodeRecordingDecisionReason.StopCommand, normalized);

        if (isRecording && sameBarcodeStopEnabled)
        {
            string current = (recordingOrderId ?? "").Trim().ToUpperInvariant();
            if (current.Length == 0)
                return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.RecordingOrderMissing, normalized);
            if (!string.Equals(normalized, current, StringComparison.Ordinal))
                return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.RecordingOrderMismatch, normalized);

            return Create(BarcodeRecordingDecisionAction.Stop, BarcodeRecordingDecisionReason.SameCodeMatched, normalized);
        }

        if (!IsOrderScan(normalized, orderIdRegex))
            return Create(BarcodeRecordingDecisionAction.Ignore, BarcodeRecordingDecisionReason.InvalidOrderNumber, normalized);

        return isRecording
            ? Create(BarcodeRecordingDecisionAction.Switch, BarcodeRecordingDecisionReason.Ready, normalized)
            : Create(BarcodeRecordingDecisionAction.Start, BarcodeRecordingDecisionReason.Ready, normalized);
    }

    internal static string GetReasonText(BarcodeRecordingDecisionReason reason) => reason switch
    {
        BarcodeRecordingDecisionReason.CannotProcess => "程序忙碌或正在关闭",
        BarcodeRecordingDecisionReason.EmptyInput => "空输入",
        BarcodeRecordingDecisionReason.CameraCurrentCodeIgnored => "未开启同码停录，摄像头忽略当前录制单号",
        BarcodeRecordingDecisionReason.CooldownOrderQueued => "扫码冷却中，保留最后一个单号",
        BarcodeRecordingDecisionReason.CooldownIgnored => "扫码冷却中",
        BarcodeRecordingDecisionReason.ClearCommand => "清除输入指令",
        BarcodeRecordingDecisionReason.ShippingCommand => "切换发货模式指令",
        BarcodeRecordingDecisionReason.ReturnCommand => "切换退货模式指令",
        BarcodeRecordingDecisionReason.StartCommand => "开始录制切换指令",
        BarcodeRecordingDecisionReason.StopCommand => "停止录制指令",
        BarcodeRecordingDecisionReason.RecordingOrderMissing => "当前录像未绑定单号",
        BarcodeRecordingDecisionReason.RecordingOrderMismatch => "同码停录模式下单号不一致",
        BarcodeRecordingDecisionReason.SameCodeMatched => "同码停录匹配",
        BarcodeRecordingDecisionReason.InvalidOrderNumber => "非法单号",
        _ => "通过录制规则"
    };

    private static BarcodeRecordingDecision Create(
        BarcodeRecordingDecisionAction action,
        BarcodeRecordingDecisionReason reason,
        string? value) => new(action, reason, (value ?? "").Trim().ToUpperInvariant());

    private static bool IsOrderScan(string value, string? orderIdRegex)
    {
        try { return Regex.IsMatch(value, orderIdRegex ?? ""); }
        catch { return true; }
    }
}

internal static class CameraBarcodeRuntimeOptions
{
    internal const string ShadowModeArgument = "--camera-barcode-shadow";
    internal const string ShadowModeEnvironmentVariable = "EPM_CAMERA_BARCODE_SHADOW";

    public static bool ShadowMode { get; private set; }

    public static void Initialize(IEnumerable<string>? arguments)
    {
        ShadowMode = IsShadowModeEnabled(
            arguments,
            Environment.GetEnvironmentVariable(ShadowModeEnvironmentVariable));
    }

    internal static bool IsShadowModeEnabled(IEnumerable<string>? arguments, string? environmentValue)
    {
        if (arguments?.Any(argument => string.Equals(
                argument,
                ShadowModeArgument,
                StringComparison.OrdinalIgnoreCase)) == true)
        {
            return true;
        }

        return string.Equals(environmentValue, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentValue, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(environmentValue, "yes", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class CameraBarcodeCandidatePolicy
{
    public static bool IsValid(string? value, string? orderIdRegex)
    {
        string normalized = (value ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            return false;
        if (normalized.Contains("CLEAR") || normalized.Contains("清除")) return false;
        if (normalized.Contains("SHIP") || normalized.Contains("发货") || normalized.Contains("FAHUO")) return false;
        if (normalized.Contains("BACK") || normalized.Contains("退货") || normalized.Contains("TUIHUO")) return false;
        if (normalized.Contains("START") || normalized.Contains("开始录制")) return false;
        if (normalized.Contains("STOP") || normalized.Contains("停止录制")) return false;

        try { return Regex.IsMatch(normalized, orderIdRegex ?? ""); }
        catch { return false; }
    }

    public static bool IsCurrentRecordingCode(string? value, string? recordingOrderId, bool isRecording)
    {
        if (!isRecording)
            return false;

        string normalized = (value ?? "").Trim();
        string current = (recordingOrderId ?? "").Trim();
        return normalized.Length > 0
            && current.Length > 0
            && string.Equals(normalized, current, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldIgnoreCurrentRecordingCode(
        string? value,
        string? recordingOrderId,
        bool isRecording,
        bool sameBarcodeStopEnabled)
    {
        return !sameBarcodeStopEnabled
            && IsCurrentRecordingCode(value, recordingOrderId, isRecording);
    }
}

internal sealed class CameraBarcodeStabilityTracker
{
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DefaultRearmDelay = TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, DateTimeOffset> _lockedCodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _missingLockedCodesSince = new(StringComparer.Ordinal);
    private string _candidateCode = "";
    private DateTimeOffset _candidateFirstSeen;
    private DateTimeOffset _candidateLastSeen;
    private TimeSpan _candidateRequiredPresence;
    private int _candidateHits;

    public CameraBarcodeObservation Observe(
        string? code,
        DateTimeOffset now,
        TimeSpan requiredPresence = default,
        TimeSpan rearmDelay = default)
    {
        RearmMissingCodes(
            now,
            code,
            rearmDelay > TimeSpan.Zero ? rearmDelay : DefaultRearmDelay);

        string normalized = (code ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0)
        {
            if (_candidateRequiredPresence > TimeSpan.Zero)
                ClearCandidate();
            else
                ExpireCandidate(now);
            return new CameraBarcodeObservation(_candidateCode);
        }

        if (_lockedCodes.ContainsKey(normalized))
        {
            _lockedCodes[normalized] = now;
            _missingLockedCodesSince.Remove(normalized);
            if (string.Equals(_candidateCode, normalized, StringComparison.Ordinal))
                ClearCandidate();
            return new CameraBarcodeObservation();
        }

        if (!string.Equals(_candidateCode, normalized, StringComparison.Ordinal)
            || now - _candidateLastSeen > ConfirmationWindow)
        {
            _candidateCode = normalized;
            _candidateFirstSeen = now;
            _candidateLastSeen = now;
            _candidateRequiredPresence = requiredPresence > TimeSpan.Zero
                ? requiredPresence
                : TimeSpan.Zero;
            _candidateHits = 1;
            return new CameraBarcodeObservation(
                _candidateCode,
                KeepDecoding: _candidateRequiredPresence > TimeSpan.Zero);
        }

        _candidateLastSeen = now;
        _candidateHits++;
        if (_candidateHits < 2 || now - _candidateFirstSeen < _candidateRequiredPresence)
        {
            return new CameraBarcodeObservation(
                _candidateCode,
                KeepDecoding: _candidateRequiredPresence > TimeSpan.Zero);
        }

        _lockedCodes[normalized] = now;
        ClearCandidate();
        return new CameraBarcodeObservation(ConfirmedCode: normalized);
    }

    public void Reset(bool preserveLockedCodes = false)
    {
        if (!preserveLockedCodes)
        {
            _lockedCodes.Clear();
            _missingLockedCodesSince.Clear();
        }
        ClearCandidate();
    }

    private void RearmMissingCodes(
        DateTimeOffset now,
        string? observedCode,
        TimeSpan rearmDelay)
    {
        string normalized = (observedCode ?? "").Trim().ToUpperInvariant();
        foreach (string code in _lockedCodes.Keys.ToArray())
        {
            if (string.Equals(code, normalized, StringComparison.Ordinal))
            {
                if (!_missingLockedCodesSince.TryGetValue(code, out DateTimeOffset missingSince)
                    || now - missingSince < rearmDelay)
                {
                    _missingLockedCodesSince.Remove(code);
                    continue;
                }

                // 运动门控会在空画面稳定后暂停解码，因此重新出现的这一帧可能是
                // 消失期间的下一次观察。先按实际经过时间解锁，再让它成为新候选。
                _lockedCodes.Remove(code);
                _missingLockedCodesSince.Remove(code);
                continue;
            }

            if (!_missingLockedCodesSince.TryGetValue(code, out DateTimeOffset firstMissingAt))
            {
                _missingLockedCodesSince[code] = now;
                continue;
            }

            if (now - firstMissingAt >= rearmDelay)
            {
                _lockedCodes.Remove(code);
                _missingLockedCodesSince.Remove(code);
            }
        }
    }

    private void ExpireCandidate(DateTimeOffset now)
    {
        if (_candidateCode.Length > 0 && now - _candidateLastSeen >= ConfirmationWindow)
            ClearCandidate();
    }

    private void ClearCandidate()
    {
        _candidateCode = "";
        _candidateFirstSeen = default;
        _candidateLastSeen = default;
        _candidateRequiredPresence = TimeSpan.Zero;
        _candidateHits = 0;
    }
}

internal sealed class CameraBarcodeFrameDecoder : IDisposable
{
    internal const double GuideWidthRatio = 0.85;
    internal const double GuideHeightRatio = 0.85;
    internal const int MaxDecodeDimension = 1440;
    internal const int MaxDecodePixels = 1_200_000;

    private static readonly HashSet<BarcodeFormat> AllowedFormats =
    [
        BarcodeFormat.CODE_128,
        BarcodeFormat.CODE_39,
        BarcodeFormat.ITF
    ];

    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = false,
        Options = new DecodingOptions
        {
            TryHarder = true,
            PossibleFormats = AllowedFormats.ToList()
        }
    };
    private readonly DecodeWorkspace _guideWorkspace = new();
    private readonly DecodeWorkspace _fullFrameWorkspace = new();
    private bool _disposed;

    internal int PixelBufferAllocationCount =>
        _guideWorkspace.Buffers.AllocationCount + _fullFrameWorkspace.Buffers.AllocationCount;

    public string? DecodeGuideRegion(Mat frame)
    {
        if (frame == null || frame.IsDisposed || frame.Empty())
            return null;

        Rect guide = GetGuideRect(frame.Width, frame.Height);
        if (guide.Width <= 0 || guide.Height <= 0)
            return null;

        using Mat cropped = new(frame, guide);
        return Decode(cropped, _guideWorkspace);
    }

    public string? DecodeFullFrame(Mat frame) => Decode(frame, _fullFrameWorkspace);

    internal static Rect GetGuideRect(int width, int height)
    {
        int guideWidth = Math.Clamp((int)Math.Round(width * GuideWidthRatio), 1, Math.Max(1, width));
        int guideHeight = Math.Clamp((int)Math.Round(height * GuideHeightRatio), 1, Math.Max(1, height));
        return new Rect((width - guideWidth) / 2, (height - guideHeight) / 2, guideWidth, guideHeight);
    }

    private string? Decode(Mat frame, DecodeWorkspace workspace)
    {
        if (frame == null || frame.IsDisposed || frame.Empty())
            return null;

        if (_disposed)
            return null;

        switch (frame.Channels())
        {
            case 1:
                frame.CopyTo(workspace.Gray);
                break;
            case 3:
                Cv2.CvtColor(frame, workspace.Gray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.CvtColor(frame, workspace.Gray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                return null;
        }

        Mat source = PrepareDecodeSize(workspace.Gray, workspace.Scaled);
        if (!source.IsContinuous())
        {
            source.CopyTo(workspace.Continuous);
            source = workspace.Continuous;
        }

        int length = checked(source.Width * source.Height);
        DecodeBuffers buffers = workspace.Buffers;
        buffers.EnsureSize(length);
        Marshal.Copy(source.Data, buffers.Pixels, 0, length);

        Result? result = null;
        for (int orientation = 0; orientation < 4 && result == null; orientation++)
        {
            var luminance = new ReusableGrayLuminanceSource(
                buffers.Pixels,
                buffers.OrientationScratch,
                source.Width,
                source.Height,
                orientation);
            result = _reader.Decode(luminance);
        }
        if (result == null || !AllowedFormats.Contains(result.BarcodeFormat))
            return null;

        string normalized = (result.Text ?? "").Trim().ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    private Mat PrepareDecodeSize(Mat gray, Mat scaled)
    {
        double dimensionScale = (double)MaxDecodeDimension / Math.Max(gray.Width, gray.Height);
        double pixelScale = Math.Sqrt((double)MaxDecodePixels / (gray.Width * (double)gray.Height));
        double scale = Math.Min(1, Math.Min(dimensionScale, pixelScale));
        if (scale >= 0.999)
            return gray;

        int width = Math.Max(1, (int)Math.Round(gray.Width * scale));
        int height = Math.Max(1, (int)Math.Round(gray.Height * scale));
        Cv2.Resize(gray, scaled, new OpenCvSharp.Size(width, height), interpolation: InterpolationFlags.Area);
        return scaled;
    }

    private sealed class DecodeWorkspace : IDisposable
    {
        public Mat Gray { get; } = new();
        public Mat Scaled { get; } = new();
        public Mat Continuous { get; } = new();
        public DecodeBuffers Buffers { get; } = new();

        public void Dispose()
        {
            Gray.Dispose();
            Scaled.Dispose();
            Continuous.Dispose();
        }
    }

    private sealed class DecodeBuffers
    {
        public byte[] Pixels { get; private set; } = [];
        public byte[] OrientationScratch { get; private set; } = [];
        public int AllocationCount { get; private set; }

        public void EnsureSize(int length)
        {
            if (Pixels.Length == length && OrientationScratch.Length == length)
                return;

            Pixels = GC.AllocateUninitializedArray<byte>(length);
            OrientationScratch = GC.AllocateUninitializedArray<byte>(length);
            AllocationCount++;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _guideWorkspace.Dispose();
        _fullFrameWorkspace.Dispose();
    }
}

internal sealed class ReusableGrayLuminanceSource : LuminanceSource
{
    private readonly byte[] _pixels;
    private readonly byte[] _orientationScratch;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly int _orientation;

    public ReusableGrayLuminanceSource(
        byte[] pixels,
        byte[] orientationScratch,
        int sourceWidth,
        int sourceHeight,
        int orientation)
        : base(
            orientation % 2 == 0 ? sourceWidth : sourceHeight,
            orientation % 2 == 0 ? sourceHeight : sourceWidth)
    {
        _pixels = pixels;
        _orientationScratch = orientationScratch;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _orientation = orientation;
    }

    public override byte[] getRow(int y, byte[] row)
    {
        if ((uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(y));
        if (row == null || row.Length < Width)
            row = new byte[Width];

        for (int x = 0; x < Width; x++)
            row[x] = GetPixel(x, y);
        return row;
    }

    public override byte[] Matrix
    {
        get
        {
            if (_orientation == 0)
                return _pixels;

            int index = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                    _orientationScratch[index++] = GetPixel(x, y);
            }
            return _orientationScratch;
        }
    }

    private byte GetPixel(int x, int y)
    {
        (int sourceX, int sourceY) = _orientation switch
        {
            1 => (_sourceWidth - 1 - y, x),
            2 => (_sourceWidth - 1 - x, _sourceHeight - 1 - y),
            3 => (y, _sourceHeight - 1 - x),
            _ => (x, y)
        };
        return _pixels[sourceY * _sourceWidth + sourceX];
    }
}

internal sealed class CameraBarcodeMotionGate : IDisposable
{
    internal static readonly TimeSpan DecodeHoldDuration = TimeSpan.FromSeconds(1);
    internal const int SampleWidth = 160;
    internal const int SampleHeight = 90;
    internal const double MeanDifferenceThreshold = 6.0;
    internal const double ChangedPixelRatioThreshold = 0.01;
    internal const double PixelDifferenceThreshold = 18;

    private static readonly OpenCvSharp.Size SampleSize = new(SampleWidth, SampleHeight);
    private readonly Mat _sampled = new();
    private readonly Mat _currentGray = new();
    private readonly Mat _previousGray = new();
    private readonly Mat _difference = new();
    private readonly Mat _changedPixels = new();
    private bool _hasBaseline;
    private DateTimeOffset _decodeUntil;
    private bool _disposed;

    public bool ShouldDecode(Mat frame, DateTimeOffset now, bool forceDecode = false)
    {
        if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
            return false;

        switch (frame.Channels())
        {
            case 1:
                Cv2.Resize(frame, _currentGray, SampleSize, interpolation: InterpolationFlags.Area);
                break;
            case 3:
                Cv2.Resize(frame, _sampled, SampleSize, interpolation: InterpolationFlags.Area);
                Cv2.CvtColor(_sampled, _currentGray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.Resize(frame, _sampled, SampleSize, interpolation: InterpolationFlags.Area);
                Cv2.CvtColor(_sampled, _currentGray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                return false;
        }

        if (!_hasBaseline)
        {
            _currentGray.CopyTo(_previousGray);
            _hasBaseline = true;
            _decodeUntil = now + DecodeHoldDuration;
            return true;
        }

        Cv2.Absdiff(_currentGray, _previousGray, _difference);
        double meanDifference = Cv2.Mean(_difference).Val0;
        Cv2.Threshold(
            _difference,
            _changedPixels,
            PixelDifferenceThreshold,
            255,
            ThresholdTypes.Binary);
        double changedPixelRatio = (double)Cv2.CountNonZero(_changedPixels) / (SampleWidth * SampleHeight);
        _currentGray.CopyTo(_previousGray);

        bool changed = meanDifference >= MeanDifferenceThreshold
            || changedPixelRatio >= ChangedPixelRatioThreshold;
        if (changed)
            _decodeUntil = now + DecodeHoldDuration;

        return forceDecode || now <= _decodeUntil;
    }

    public void Reset()
    {
        if (_disposed)
            return;

        _hasBaseline = false;
        _decodeUntil = default;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _sampled.Dispose();
        _currentGray.Dispose();
        _previousGray.Dispose();
        _difference.Dispose();
        _changedPixels.Dispose();
    }
}

internal sealed class CameraBarcodeRecognitionService : IDisposable
{
    private static readonly TimeSpan GuideInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FullFrameInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan SlowDecodeThreshold = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan SlowDecodeLogInterval = TimeSpan.FromSeconds(30);

    private readonly Func<string, bool> _candidateValidator;
    private readonly Func<bool>? _fullFrameAllowed;
    private readonly Func<string, TimeSpan>? _confirmationDurationProvider;
    private readonly Func<TimeSpan>? _rearmDelayProvider;
    private readonly CameraBarcodeFrameDecoder _decoder = new();
    private readonly CameraBarcodeMotionGate _motionGate = new();
    private readonly CameraBarcodeStabilityTracker _stabilityTracker = new();
    private readonly object _pendingLock = new();
    private readonly object _trackerLock = new();
    private readonly SemaphoreSlim _pendingSignal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private Mat? _pendingFrame;
    private bool _pendingAllowFullFrame;
    private int _pendingGeneration;
    private DateTimeOffset _lastAcceptedAt;
    private DateTimeOffset _lastFullFrameAttemptAt;
    private DateTimeOffset _lastSlowDecodeLogAt;
    private DateTimeOffset _lastRecognitionErrorLogAt;
    private long _droppedFrames;
    private long _forceDecodeUntilUtcTicks;
    private int _generation;
    private volatile bool _disposed;
    private int _workerResourcesDisposed;

    public event Action<CameraBarcodeRecognitionStatus>? StatusChanged;
    public event Action<string>? BarcodeConfirmed;

    public CameraBarcodeRecognitionService(
        Func<string, bool> candidateValidator,
        Func<bool>? fullFrameAllowed = null,
        Func<string, TimeSpan>? confirmationDurationProvider = null,
        Func<TimeSpan>? rearmDelayProvider = null)
    {
        _candidateValidator = candidateValidator ?? throw new ArgumentNullException(nameof(candidateValidator));
        _fullFrameAllowed = fullFrameAllowed;
        _confirmationDurationProvider = confirmationDurationProvider;
        _rearmDelayProvider = rearmDelayProvider;
        _workerTask = Task.Run(ProcessLoopAsync);
    }

    public bool TrySubmitFrame(Mat frame, bool allowFullFrame)
    {
        if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
            return false;

        Mat? replacement = null;
        Mat? dropped = null;
        bool shouldSignal = false;
        lock (_pendingLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_disposed || now - _lastAcceptedAt < GuideInterval)
                return false;

            _lastAcceptedAt = now;
            bool forceDecode = now.UtcTicks <= Volatile.Read(ref _forceDecodeUntilUtcTicks);
            if (!_motionGate.ShouldDecode(frame, now, forceDecode))
                return false;

            replacement = frame.Clone();
            dropped = _pendingFrame;
            _pendingFrame = replacement;
            _pendingAllowFullFrame = allowFullFrame;
            _pendingGeneration = _generation;
            if (dropped != null)
                Interlocked.Increment(ref _droppedFrames);
            shouldSignal = _pendingSignal.CurrentCount == 0;
        }

        dropped?.Dispose();
        if (shouldSignal)
        {
            try { _pendingSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }
        return true;
    }

    public void Reset(bool preserveConfirmedCodes = false)
    {
        if (_disposed)
            return;

        Mat? pending;
        lock (_pendingLock)
        {
            Interlocked.Increment(ref _generation);
            pending = _pendingFrame;
            _pendingFrame = null;
            _lastAcceptedAt = default;
            Volatile.Write(ref _forceDecodeUntilUtcTicks, 0);
            _motionGate.Reset();
        }
        pending?.Dispose();
        lock (_trackerLock)
            _stabilityTracker.Reset(preserveConfirmedCodes);
        StatusChanged?.Invoke(new CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState.Idle));
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _pendingSignal.WaitAsync(_cts.Token).ConfigureAwait(false);

                Mat? frame;
                bool allowFullFrame;
                int generation;
                lock (_pendingLock)
                {
                    frame = _pendingFrame;
                    allowFullFrame = _pendingAllowFullFrame;
                    generation = _pendingGeneration;
                    _pendingFrame = null;
                }
                if (frame == null)
                    continue;

                using (frame)
                    ProcessFrame(frame, allowFullFrame, generation);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Error("CameraBarcode", "Recognition worker stopped unexpectedly", ex);
        }
    }

    private void ProcessFrame(Mat frame, bool allowFullFrame, int generation)
    {
        var stopwatch = Stopwatch.StartNew();
        string? code = null;
        try
        {
            code = _decoder.DecodeGuideRegion(frame);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (code == null
                && allowFullFrame
                && (_fullFrameAllowed?.Invoke() ?? true)
                && now - _lastFullFrameAttemptAt >= FullFrameInterval)
            {
                _lastFullFrameAttemptAt = now;
                code = _decoder.DecodeFullFrame(frame);
            }

            if (code != null && !IsValidCandidate(code))
                code = null;

            if (generation != Volatile.Read(ref _generation) || _disposed)
                return;

            CameraBarcodeObservation observation;
            TimeSpan requiredPresence = code == null
                ? TimeSpan.Zero
                : _confirmationDurationProvider?.Invoke(code) ?? TimeSpan.Zero;
            TimeSpan rearmDelay = _rearmDelayProvider?.Invoke() ?? TimeSpan.Zero;
            lock (_trackerLock)
                observation = _stabilityTracker.Observe(code, now, requiredPresence, rearmDelay);

            Volatile.Write(
                ref _forceDecodeUntilUtcTicks,
                observation.KeepDecoding ? now.AddSeconds(2.5).UtcTicks : 0);

            if (observation.ConfirmedCode.Length > 0)
            {
                long dropped = Interlocked.Read(ref _droppedFrames);
                RuntimeLog.Info("CameraBarcode", $"Confirmed {observation.ConfirmedCode}, decode={stopwatch.ElapsedMilliseconds}ms, dropped={dropped}");
                StatusChanged?.Invoke(new CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState.Confirmed, observation.ConfirmedCode));
                BarcodeConfirmed?.Invoke(observation.ConfirmedCode);
            }
            else if (observation.CandidateCode.Length > 0)
            {
                StatusChanged?.Invoke(new CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState.Candidate, observation.CandidateCode));
            }
            else
            {
                StatusChanged?.Invoke(new CameraBarcodeRecognitionStatus(CameraBarcodeRecognitionState.Idle));
            }
        }
        catch (Exception ex)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - _lastRecognitionErrorLogAt >= SlowDecodeLogInterval)
            {
                _lastRecognitionErrorLogAt = now;
                RuntimeLog.Warn("CameraBarcode", $"Recognition frame skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            stopwatch.Stop();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (stopwatch.Elapsed >= SlowDecodeThreshold && now - _lastSlowDecodeLogAt >= SlowDecodeLogInterval)
            {
                _lastSlowDecodeLogAt = now;
                RuntimeLog.Warn("CameraBarcode", $"Recognition is slower than the target rate: decode={stopwatch.ElapsedMilliseconds}ms, dropped={Interlocked.Read(ref _droppedFrames)}");
            }
        }
    }

    private bool IsValidCandidate(string code)
    {
        try { return _candidateValidator(code); }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();

        Mat? pending;
        lock (_pendingLock)
        {
            pending = _pendingFrame;
            _pendingFrame = null;
        }
        pending?.Dispose();
        bool completed = false;
        try { completed = _workerTask.Wait(1000); } catch { completed = _workerTask.IsCompleted; }
        if (completed)
        {
            DisposeWorkerResources();
        }
        else
        {
            RuntimeLog.Warn("CameraBarcode", "Recognition worker is still stopping; native decoder cleanup deferred");
            _ = _workerTask.ContinueWith(
                _ => DisposeWorkerResources(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void DisposeWorkerResources()
    {
        if (Interlocked.Exchange(ref _workerResourcesDisposed, 1) != 0)
            return;
        _decoder.Dispose();
        _motionGate.Dispose();
        _pendingSignal.Dispose();
        _cts.Dispose();
    }
}
