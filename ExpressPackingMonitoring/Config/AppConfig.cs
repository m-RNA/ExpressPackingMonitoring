using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring.Config
{
    public static class WindowCloseBehaviors
    {
        public const string Ask = "Ask";
        public const string MinimizeToTray = "MinimizeToTray";
        public const string Exit = "Exit";

        public static string Normalize(string? value) =>
            value is MinimizeToTray or Exit ? value : Ask;
    }

    public partial class ScanRecord : ObservableObject
    {
        [ObservableProperty] private string _orderId;
        [ObservableProperty] private string _duration;
        [ObservableProperty] private string _dateStr;
        [ObservableProperty] private string _mode;

        // 新增活跃状态，用于前端变色
        [ObservableProperty] private bool _isActive;

        public ScanRecord(string orderId, string duration, string dateStr, string mode, bool isActive = false)
        { 
            OrderId = orderId; 
            Duration = duration; 
            DateStr = dateStr; 
            Mode = mode; 
            IsActive = isActive; 
        }
    }

    public class GpuEncoderOption
    {
        public string Value { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    // 存储节点模型
    public class StorageLocation
    {
        public string Path { get; set; } = "D:\\快递打包视频";
        public double ReserveGB { get; set; } = 0.0;
        public int Priority { get; set; } = 1; // 数字越小越优先

        [JsonIgnore]
        public double EffectiveReserveGB
        {
            get => StorageSpacePolicy.GetEffectiveReserveGB(this);
            set => ReserveGB = StorageSpacePolicy.NormalizeReserveGB(Path, value);
        }
    }

    internal readonly record struct StorageDriveCandidate(string RootPath, bool IsReady, DriveType DriveType);

    // 摄像头独立配置模型
    public class CameraSettings
    {
        public int FrameWidth { get; set; } = 1280;
        public int FrameHeight { get; set; } = 720;
        public int Fps { get; set; } = 15;
        public string AudioDeviceName { get; set; } = "";
        public string AudioDeviceMoniker { get; set; } = "";
        public int AudioSyncOffsetMs { get; set; } = 0;
    }

    public class AppConfig
    {
        public const int CurrentVoiceSettingsVersion = 2;
        public const int CurrentCameraBarcodeSetupVersion = 1;
        public const int CurrentMobileConnectionSetupVersion = 1;

        // 语音提醒设置迁移版本。旧配置没有该字段，加载后会从 0 迁移到当前版本。
        public int VoiceSettingsVersion { get; set; } = 0;

        // 摄像头识别升级引导版本。旧配置缺少该字段时会提示用户选择是否启用。
        public int CameraBarcodeSetupVersion { get; set; } = 0;

        // 手机扫码连接升级引导版本。旧配置缺少该字段时会在局域网服务就绪后提示一次。
        public int MobileConnectionSetupVersion { get; set; } = 0;

        // 录像方式："CameraMonitor"=使用电脑摄像头录像，"PrintStation"=不使用电脑摄像头（兼容旧配置），空值表示首次启动需要选择。
        public string WorkstationRole { get; set; } = "";
        // 主程序实际运行目录。发布包中指向 app 目录，供手动增量更新包定位安装目标。
        public string AppRootDirectory { get; set; } = "";
        public string PrintStationMonitorAddress { get; set; } = "";
        public bool FirstUseWizardCompleted { get; set; } = false;

        // 核心：多磁盘配置列表
        public List<StorageLocation> StorageLocations { get; set; } = CreateDefaultStorageLocations();

        public string CameraMonikerString { get; set; } = "";
        public int CameraIndex { get; set; } = 0; // 保留作为回退

        // 存储不同摄像头的配置：Key 为 MonikerString
        public Dictionary<string, CameraSettings> CameraConfigs { get; set; } = new();

        public int FrameWidth { get; set; } = 1280;
        public int FrameHeight { get; set; } = 720;
        public int Fps { get; set; } = 15;
        public bool EnableSmartZoom { get; set; } = false;
        public double ZoomScale { get; set; } = 1.5;
        public double ZoomDelaySeconds { get; set; } = 1.0;
        public double ZoomDurationSeconds { get; set; } = 3.0;
        public bool EnableZoomAnimation { get; set; } = true;
        public double ZoomAnimationDurationMs { get; set; } = 250.0;
        public bool EnableAutoStop { get; set; } = true;
        public double AutoStopMinutes { get; set; } = 1.0;
        public bool EnableMaxDuration { get; set; } = false;
        public double MaxDurationMinutes { get; set; } = 5.0;
        public double MinRecordingSeconds { get; set; } = 3.0;
        public int MinVideoFileSizeKB { get; set; } = 50;
        public bool EnableCameraIdle { get; set; } = true;
        public bool EnableCameraBarcodeRecognition { get; set; } = false;
        public bool EnableSameBarcodeStopRecording { get; set; } = false;
        public double CameraBarcodeRearmSeconds { get; set; } = 3.0;
        public double CameraSameBarcodeConfirmationSeconds { get; set; } = 1.0;
        public double CameraIdleMinutes { get; set; } = 5.0;
        public string CameraIdleNoSleepStart1 { get; set; } = "";
        public string CameraIdleNoSleepEnd1 { get; set; } = "";
        public string CameraIdleNoSleepStart2 { get; set; } = "";
        public string CameraIdleNoSleepEnd2 { get; set; } = "";

        public double MotionDetectThreshold { get; set; } = 15.0;
        public string OrderIdRegex { get; set; } = "^[a-zA-Z0-9-]{12,25}$";
        public bool EnableSoundPrompt { get; set; } = true;
        public bool MaximizeVolumeForSpeech { get; set; } = true;
        public double TimeoutWarningSeconds { get; set; } = 10.0;
        public string Theme { get; set; } = "Auto";
        public string Language { get; set; } = AppLanguage.Auto;
        public string WindowCloseBehavior { get; set; } = WindowCloseBehaviors.Ask;
        public bool ShowAdvancedSettings { get; set; } = false;
        public bool ShowDeletedVideos { get; set; } = true;
        public bool AutoStartOnBoot { get; set; } = true;
        public bool EnableAutoCheckUpdate { get; set; } = true;
        public bool EnableAudioRecording { get; set; } = true;
        public string AudioDeviceName { get; set; } = "";
        public string AudioDeviceMoniker { get; set; } = "";
        public int AudioSyncOffsetMs { get; set; } = 0;
        public double BarcodeCooldownSeconds { get; set; } = 2.0;
        public string GpuEncoder { get; set; } = "nvidia";
        public string VideoCodec { get; set; } = "h265"; // "h264" or "h265"
        public int VideoCqp { get; set; } = 30;

        // 全局键盘监听（后台接收扫码枪）
        public bool EnableGlobalKeyboard { get; set; } = true;
        public bool EnableScannerAutoSubmit { get; set; } = false;
        public int ScannerAutoSubmitMinLength { get; set; } = 12;
        public int ScannerAutoSubmitQuietMs { get; set; } = 220;
        public int ScannerAutoSubmitMaxAverageIntervalMs { get; set; } = 30;
        public int ScannerAutoSubmitMaxKeyIntervalMs { get; set; } = 50;

        // 水印
        public bool EnableWatermark { get; set; } = true;

        // 局域网 Web 服务
        public bool EnableWebServer { get; set; } = true;
        public int WebServerPort { get; set; } = 5280;
        public int TranscodeCacheMaxMB { get; set; } = 1024;  // 转码缓存上限(MB)，超出后按时间清理最旧的
        public bool RequireWebAccessKey { get; set; } = false;
        public string WebAccessKey { get; set; } = "";
        public string MobileBackupComputerId { get; set; } = "";

        // AI 语音合成
        public bool EnableAiTts { get; set; } = true;
        public string AiTtsEngine { get; set; } = "Edge"; // "Kokoro" or "Edge"
        public int AiTtsSpeakerId { get; set; } = 51;        // 普通播报声线
        public int AiTtsWarningSpeakerId { get; set; } = 50;  // 警告播报声线
        public float AiTtsSpeed { get; set; } = 1.0f;
        public string EdgeTtsVoice { get; set; } = "zh-CN-XiaoxiaoNeural";
        public string EdgeTtsWarningVoice { get; set; } = "zh-CN-YunjianNeural";
        public string EdgeTtsVoiceZhHans { get; set; } = "";
        public string EdgeTtsWarningVoiceZhHans { get; set; } = "";
        public string EdgeTtsVoiceEnUs { get; set; } = "en-US-JennyNeural";
        public string EdgeTtsWarningVoiceEnUs { get; set; } = "en-US-GuyNeural";

        // 订单备注播报（快递助手插件）
        public bool EnableOrderInfoAnnounce { get; set; } = true;
        public bool AnnounceBuyerMessage { get; set; } = true;
        public bool AnnounceSellerMemo { get; set; } = true;
        public bool AnnounceProductInfo { get; set; } = false;
        public bool EnablePrintedRefundAlert { get; set; } = true;
        public bool EnableOrderInfoLog { get; set; } = false;

        // TTS 断句关键词（电商场景，在这些词前自动插入停顿）
        public List<string> TtsBreakWords { get; set; } = new();

        // 缓存的检测结果
        public List<GpuEncoderOption> EncoderOptionsCache { get; set; } = new();
        public List<string> ValidatedEncodersCache { get; set; } = new();
        public bool IsEncoderDetected { get; set; } = false;

        public static bool NormalizeAfterLoad(AppConfig config)
        {
            bool changed = false;

            if (!config.EnableWebServer)
            {
                config.EnableWebServer = true;
                changed = true;
            }

            string normalizedLanguage = AppLanguage.NormalizePreference(config.Language);
            if (config.Language != normalizedLanguage)
            {
                config.Language = normalizedLanguage;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(config.WebAccessKey) || config.WebAccessKey.Trim().Length < 16)
            {
                config.WebAccessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                changed = true;
            }
            else if (!string.Equals(config.WebAccessKey, config.WebAccessKey.Trim(), StringComparison.Ordinal))
            {
                config.WebAccessKey = config.WebAccessKey.Trim();
                changed = true;
            }

            if (!Guid.TryParse(config.MobileBackupComputerId, out Guid computerId) || computerId == Guid.Empty)
            {
                config.MobileBackupComputerId = Guid.NewGuid().ToString("D");
                changed = true;
            }
            else
            {
                string normalizedComputerId = computerId.ToString("D");
                if (!string.Equals(config.MobileBackupComputerId, normalizedComputerId, StringComparison.Ordinal))
                {
                    config.MobileBackupComputerId = normalizedComputerId;
                    changed = true;
                }
            }

            string normalizedEngine = NormalizeAiTtsEngine(config.AiTtsEngine);
            if (config.AiTtsEngine != normalizedEngine)
            {
                config.AiTtsEngine = normalizedEngine;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(config.EdgeTtsVoice))
            {
                config.EdgeTtsVoice = "zh-CN-XiaoxiaoNeural";
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(config.EdgeTtsWarningVoice))
            {
                config.EdgeTtsWarningVoice = "zh-CN-YunxiNeural";
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(config.EdgeTtsVoiceZhHans))
            {
                config.EdgeTtsVoiceZhHans = config.EdgeTtsVoice;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(config.EdgeTtsWarningVoiceZhHans))
            {
                config.EdgeTtsWarningVoiceZhHans = config.EdgeTtsWarningVoice;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(config.EdgeTtsVoiceEnUs))
            {
                config.EdgeTtsVoiceEnUs = "en-US-JennyNeural";
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(config.EdgeTtsWarningVoiceEnUs))
            {
                config.EdgeTtsWarningVoiceEnUs = "en-US-GuyNeural";
                changed = true;
            }

            string effectiveLanguage = AppLanguage.Resolve(config.Language);
            string effectiveVoice = effectiveLanguage == AppLanguage.Chinese ? config.EdgeTtsVoiceZhHans : config.EdgeTtsVoiceEnUs;
            string effectiveWarningVoice = effectiveLanguage == AppLanguage.Chinese ? config.EdgeTtsWarningVoiceZhHans : config.EdgeTtsWarningVoiceEnUs;
            if (config.EdgeTtsVoice != effectiveVoice) { config.EdgeTtsVoice = effectiveVoice; changed = true; }
            if (config.EdgeTtsWarningVoice != effectiveWarningVoice) { config.EdgeTtsWarningVoice = effectiveWarningVoice; changed = true; }

            if (config.VoiceSettingsVersion < CurrentVoiceSettingsVersion)
            {
                // 旧版把“是否播放提示”和“是否使用 AI 语音”拆成两个开关。
                // 新版合并成“语音提醒”总开关 + “语音引擎”选择；旧用户只要曾启用 AI 语音，就保留语音提醒开启。
                if (config.EnableAiTts && !config.EnableSoundPrompt)
                {
                    config.EnableSoundPrompt = true;
                }

                config.VoiceSettingsVersion = CurrentVoiceSettingsVersion;
                changed = true;
            }

            if (config.StorageLocations == null)
            {
                config.StorageLocations = new List<StorageLocation>();
                changed = true;
            }

            if (config.StorageLocations.Count == 0)
            {
                config.StorageLocations.AddRange(CreateDefaultStorageLocations());
                changed = true;
            }

            string normalizedCloseBehavior = WindowCloseBehaviors.Normalize(config.WindowCloseBehavior);
            if (config.WindowCloseBehavior != normalizedCloseBehavior)
            {
                config.WindowCloseBehavior = normalizedCloseBehavior;
                changed = true;
            }

            foreach (var location in config.StorageLocations)
            {
                double normalizedReserveGB = StorageSpacePolicy.NormalizeReserveGB(location.Path, location.ReserveGB);
                if (System.Math.Abs(location.ReserveGB - normalizedReserveGB) > 0.001)
                {
                    location.ReserveGB = normalizedReserveGB;
                    changed = true;
                }
            }

            if (config.EnableGlobalKeyboard && config.EnableScannerAutoSubmit)
            {
                config.EnableGlobalKeyboard = false;
                changed = true;
            }

            double normalizedCameraBarcodeRearmSeconds = System.Math.Clamp(
                config.CameraBarcodeRearmSeconds,
                1.0,
                30.0);
            if (System.Math.Abs(config.CameraBarcodeRearmSeconds - normalizedCameraBarcodeRearmSeconds) > 0.001)
            {
                config.CameraBarcodeRearmSeconds = normalizedCameraBarcodeRearmSeconds;
                changed = true;
            }

            double normalizedCameraSameBarcodeConfirmationSeconds = System.Math.Clamp(
                config.CameraSameBarcodeConfirmationSeconds,
                0.5,
                10.0);
            if (System.Math.Abs(config.CameraSameBarcodeConfirmationSeconds - normalizedCameraSameBarcodeConfirmationSeconds) > 0.001)
            {
                config.CameraSameBarcodeConfirmationSeconds = normalizedCameraSameBarcodeConfirmationSeconds;
                changed = true;
            }

            int normalizedMinLength = System.Math.Clamp(config.ScannerAutoSubmitMinLength, 4, 30);
            if (config.ScannerAutoSubmitMinLength != normalizedMinLength)
            {
                config.ScannerAutoSubmitMinLength = normalizedMinLength;
                changed = true;
            }

            int normalizedQuietMs = System.Math.Clamp(config.ScannerAutoSubmitQuietMs, 120, 600);
            if (config.ScannerAutoSubmitQuietMs != normalizedQuietMs)
            {
                config.ScannerAutoSubmitQuietMs = normalizedQuietMs;
                changed = true;
            }

            int normalizedAverageMs = System.Math.Clamp(config.ScannerAutoSubmitMaxAverageIntervalMs, 10, 100);
            if (config.ScannerAutoSubmitMaxAverageIntervalMs != normalizedAverageMs)
            {
                config.ScannerAutoSubmitMaxAverageIntervalMs = normalizedAverageMs;
                changed = true;
            }

            int normalizedKeyIntervalMs = System.Math.Clamp(config.ScannerAutoSubmitMaxKeyIntervalMs, 20, 150);
            if (config.ScannerAutoSubmitMaxKeyIntervalMs != normalizedKeyIntervalMs)
            {
                config.ScannerAutoSubmitMaxKeyIntervalMs = normalizedKeyIntervalMs;
                changed = true;
            }

            return changed;
        }

        private static List<StorageLocation> CreateDefaultStorageLocations()
        {
            try
            {
                return CreateDefaultStorageLocations(
                    DriveInfo.GetDrives().Select(drive =>
                        new StorageDriveCandidate(drive.Name, drive.IsReady, drive.DriveType)));
            }
            catch
            {
                return CreateDefaultStorageLocations(Array.Empty<StorageDriveCandidate>());
            }
        }

        internal static List<StorageLocation> CreateDefaultStorageLocations(
            IEnumerable<StorageDriveCandidate> drives)
        {
            var roots = drives
                .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
                .Select(drive => Path.GetPathRoot(drive.RootPath) ?? drive.RootPath)
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
                .Where(root => !string.Equals(root, @"C:\", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (roots.Count == 0)
                roots.Add(@"C:\");

            return roots
                .Select((root, index) =>
                {
                    string path = Path.Combine(root, "快递打包视频");
                    return new StorageLocation
                    {
                        Path = path,
                        ReserveGB = StorageSpacePolicy.GetMinimumReserveGB(path),
                        Priority = index
                    };
                })
                .ToList();
        }

        internal bool IsCameraIdleNoSleepTime(DateTime now)
        {
            TimeSpan timeOfDay = now.TimeOfDay;
            return IsTimeInCameraIdlePeriod(timeOfDay, CameraIdleNoSleepStart1, CameraIdleNoSleepEnd1)
                || IsTimeInCameraIdlePeriod(timeOfDay, CameraIdleNoSleepStart2, CameraIdleNoSleepEnd2);
        }

        internal static bool TryNormalizeCameraIdlePeriod(
            string? startText,
            string? endText,
            out string normalizedStart,
            out string normalizedEnd)
        {
            normalizedStart = startText?.Trim() ?? "";
            normalizedEnd = endText?.Trim() ?? "";

            if (normalizedStart.Length == 0 && normalizedEnd.Length == 0)
                return true;

            if (!TryParseTimeOfDay(normalizedStart, out TimeSpan start)
                || !TryParseTimeOfDay(normalizedEnd, out TimeSpan end)
                || start == end)
            {
                return false;
            }

            normalizedStart = start.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
            normalizedEnd = end.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        private static bool IsTimeInCameraIdlePeriod(TimeSpan timeOfDay, string? startText, string? endText)
        {
            if (!TryNormalizeCameraIdlePeriod(startText, endText, out string normalizedStart, out string normalizedEnd)
                || normalizedStart.Length == 0)
            {
                return false;
            }

            TryParseTimeOfDay(normalizedStart, out TimeSpan start);
            TryParseTimeOfDay(normalizedEnd, out TimeSpan end);
            return start < end
                ? timeOfDay >= start && timeOfDay < end
                : timeOfDay >= start || timeOfDay < end;
        }

        private static bool TryParseTimeOfDay(string text, out TimeSpan value)
        {
            string[] formats = [@"h\:mm", @"hh\:mm"];
            return TimeSpan.TryParseExact(
                    text,
                    formats,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value)
                && value >= TimeSpan.Zero
                && value < TimeSpan.FromDays(1);
        }

        internal static void ApplyFirstUseDefaults(AppConfig config)
        {
            config.EnableCameraBarcodeRecognition = true;
            config.CameraBarcodeSetupVersion = CurrentCameraBarcodeSetupVersion;
            config.FirstUseWizardCompleted = true;
        }

        internal static bool ShouldPromptCameraBarcodeUpgrade(AppConfig config)
        {
            return config != null
                && config.FirstUseWizardCompleted
                && config.CameraBarcodeSetupVersion < CurrentCameraBarcodeSetupVersion;
        }

        internal static void ApplyCameraBarcodeUpgradeChoice(AppConfig config, bool enableRecognition)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (enableRecognition)
                config.EnableCameraBarcodeRecognition = true;
            config.CameraBarcodeSetupVersion = CurrentCameraBarcodeSetupVersion;
        }

        internal static bool ShouldPromptMobileConnection(AppConfig config)
        {
            return config != null
                && config.FirstUseWizardCompleted
                && config.EnableWebServer
                && config.MobileConnectionSetupVersion < CurrentMobileConnectionSetupVersion;
        }

        internal static void MarkMobileConnectionSetupCompleted(AppConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            config.MobileConnectionSetupVersion = CurrentMobileConnectionSetupVersion;
        }

        private static string NormalizeAiTtsEngine(string engine)
        {
            return string.Equals(engine, "Kokoro", System.StringComparison.OrdinalIgnoreCase) ? "Kokoro" : "Edge";
        }
    }
}
