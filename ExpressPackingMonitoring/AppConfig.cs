using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

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
}
