#nullable disable
using System;
using System.Windows;
using System.Collections.Generic;
using Microsoft.Win32;
using ExpressPackingMonitoring.ViewModels;
using AForge.Video.DirectShow;
using System.Linq;
using System.Windows.Controls;
using System.Threading;

namespace ExpressPackingMonitoring
{
    public class CameraInfo { public int Index { get; set; } public string Name { get; set; } }
    public class ResOption { public string Name { get; set; } public int Width { get; set; } public int Height { get; set; } }
    public class MicInfo { public string Name { get; set; } }
    public class FpsOption { public int Fps { get; set; } public string Label { get; set; } }

    public partial class SettingsWindow : Window
    {
        public AppConfig Config { get; set; }
        public double CurrentDiskUsagePercent { get; set; }
        public string CurrentDiskUsageText { get; set; }

        private string _originalTheme;
        private bool _isRecording;
        private bool _isLoadingDevices;

        public SettingsWindow(AppConfig clonedConfig, double diskUsagePercent, string diskUsageText, bool isRecording = false)
        {
            InitializeComponent();
            _originalTheme = clonedConfig.Theme;
            _isRecording = isRecording;
            Config = clonedConfig;

            CurrentDiskUsagePercent = diskUsagePercent;
            CurrentDiskUsageText = diskUsageText;

            this.DataContext = this;

            // GPU编码器使用缓存，可立即加载
            LoadGpuEncoders();
            LoadVideoCodecs();

            // 非DirectShow的预设
            ZoomScaleComboBox.ItemsSource = new List<double> { 1.2, 1.5, 2.0, 2.5, 3.0, 4.0 };
            if (!ZoomScaleComboBox.Items.Contains(Config.ZoomScale)) Config.ZoomScale = 1.5;

            // 从注册表读取实际的开机自启动状态
            Config.AutoStartOnBoot = IsAutoStartEnabled();

            // 窗口加载后异步枚举设备，避免阻塞UI线程
            this.Loaded += SettingsWindow_Loaded;
        }

        private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoadingDevices = true;
            try
            {
                await LoadAllDevicesAsync();
            }
            finally
            {
                _isLoadingDevices = false;
            }

            if (_isRecording)
            {
                CameraComboBox.IsEnabled = false;
                ResComboBox.IsEnabled = false;
                FpsComboBox.IsEnabled = false;
                CameraComboBox.ToolTip = "录制中不可修改，停止录制后再更改";
                ResComboBox.ToolTip = "录制中不可修改，停止录制后再更改";
                FpsComboBox.ToolTip = "录制中不可修改，停止录制后再更改";
            }
        }

        private void ThemeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ThemeComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Content != null)
            {
                string t = item.Content.ToString();
                if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(t, out var themeEnum))
                {
                    ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
                }
            }
        }

        /// <summary>
        /// 在独立 STA 线程上运行 DirectShow COM 操作，避免与 AForge 摄像头线程冲突。
        /// </summary>
        private static System.Threading.Tasks.Task<T> RunOnStaThread<T>(Func<T> func)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
            var thread = new Thread(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        private async System.Threading.Tasks.Task LoadAllDevicesAsync()
        {
            var config = Config;
            var result = await RunOnStaThread(() =>
            {
                var cams = new List<CameraInfo>();
                var micList = new List<MicInfo>();
                var resList = new List<ResOption>();
                var fpsList = new List<int>();

                try
                {
                    var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                    for (int i = 0; i < videoDevices.Count; i++)
                        cams.Add(new CameraInfo { Index = i, Name = $"[{i}] {videoDevices[i].Name}" });

                    if (config.CameraIndex >= 0 && config.CameraIndex < videoDevices.Count)
                    {
                        var device = new VideoCaptureDevice(videoDevices[config.CameraIndex].MonikerString);
                        resList = device.VideoCapabilities
                            .Select(c => new { c.FrameSize.Width, c.FrameSize.Height })
                            .Distinct()
                            .OrderByDescending(r => r.Width * r.Height)
                            .Select(r => new ResOption
                            {
                                Name = $"{r.Width}x{r.Height}{GetResLabel(r.Width, r.Height)}",
                                Width = r.Width,
                                Height = r.Height
                            })
                            .ToList();

                        fpsList = device.VideoCapabilities
                            .Select(c => c.AverageFrameRate)
                            .Where(f => f > 0)
                            .Distinct()
                            .OrderBy(f => f)
                            .ToList();
                    }
                }
                catch { }

                try
                {
                    var audioDevices = new FilterInfoCollection(new Guid("33D9A762-90C8-11D0-BD43-00A0C911CE86"));
                    for (int i = 0; i < audioDevices.Count; i++)
                        micList.Add(new MicInfo { Name = audioDevices[i].Name });
                }
                catch { }

                return (Cameras: cams, Mics: micList, Resolutions: resList, FpsValues: fpsList);
            });

            // 更新摄像头
            var cameras = result.Cameras;
            if (cameras.Count == 0)
                cameras.Add(new CameraInfo { Index = 0, Name = "[0] 未检测到摄像头" });
            CameraComboBox.ItemsSource = cameras;
            CameraComboBox.SelectedValue = config.CameraIndex;

            // 更新麦克风
            var mics = result.Mics;
            if (mics.Count == 0)
                mics.Add(new MicInfo { Name = "未检测到麦克风" });
            MicComboBox.ItemsSource = mics;
            if (string.IsNullOrEmpty(config.AudioDeviceName) && mics.Count > 0)
                config.AudioDeviceName = mics[0].Name;

            // 更新分辨率
            var resolutions = result.Resolutions;
            if (resolutions.Count == 0)
            {
                resolutions = new List<ResOption>
                {
                    new ResOption { Name = "720P - 省空间", Width = 1280, Height = 720 },
                    new ResOption { Name = "1080P - 高清", Width = 1920, Height = 1080 },
                    new ResOption { Name = "2K - 超清", Width = 2560, Height = 1440 },
                    new ResOption { Name = "4K - 极清", Width = 3840, Height = 2160 }
                };
            }
            ResComboBox.ItemsSource = resolutions;
            var resMatch = resolutions.FirstOrDefault(r => r.Width == config.FrameWidth && r.Height == config.FrameHeight);
            ResComboBox.SelectedItem = resMatch ?? resolutions.FirstOrDefault();

            // 更新帧率
            var fpsValues = result.FpsValues;
            var fpsCbiList = new List<ComboBoxItem>();
            if (fpsValues.Count == 0)
                fpsValues = new List<int> { 10, 15, 20, 25, 30 };
            foreach (var fps in fpsValues)
                fpsCbiList.Add(new ComboBoxItem { Content = $"{fps} FPS", Tag = fps });
            FpsComboBox.ItemsSource = fpsCbiList;
            var fpsMatch = fpsCbiList.FirstOrDefault(i => (int)i.Tag == config.Fps);
            FpsComboBox.SelectedItem = fpsMatch ?? fpsCbiList.FirstOrDefault();
        }

        private async System.Threading.Tasks.Task LoadCameraCapabilitiesAsync(int cameraIndex, int currentWidth, int currentHeight, int currentFps)
        {
            var result = await RunOnStaThread(() =>
            {
                var resList = new List<ResOption>();
                var fpsList = new List<int>();
                try
                {
                    var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                    if (cameraIndex >= 0 && cameraIndex < videoDevices.Count)
                    {
                        var device = new VideoCaptureDevice(videoDevices[cameraIndex].MonikerString);
                        resList = device.VideoCapabilities
                            .Select(c => new { c.FrameSize.Width, c.FrameSize.Height })
                            .Distinct()
                            .OrderByDescending(r => r.Width * r.Height)
                            .Select(r => new ResOption
                            {
                                Name = $"{r.Width}x{r.Height}{GetResLabel(r.Width, r.Height)}",
                                Width = r.Width,
                                Height = r.Height
                            })
                            .ToList();

                        fpsList = device.VideoCapabilities
                            .Select(c => c.AverageFrameRate)
                            .Where(f => f > 0)
                            .Distinct()
                            .OrderBy(f => f)
                            .ToList();
                    }
                }
                catch { }
                return (Resolutions: resList, FpsValues: fpsList);
            });

            var resolutions = result.Resolutions;
            if (resolutions.Count == 0)
            {
                resolutions = new List<ResOption>
                {
                    new ResOption { Name = "720P - 省空间", Width = 1280, Height = 720 },
                    new ResOption { Name = "1080P - 高清", Width = 1920, Height = 1080 },
                    new ResOption { Name = "2K - 超清", Width = 2560, Height = 1440 },
                    new ResOption { Name = "4K - 极清", Width = 3840, Height = 2160 }
                };
            }
            ResComboBox.ItemsSource = resolutions;
            var resMatch = resolutions.FirstOrDefault(r => r.Width == currentWidth && r.Height == currentHeight);
            ResComboBox.SelectedItem = resMatch ?? resolutions.FirstOrDefault();

            var fpsValues = result.FpsValues;
            var fpsCbiList = new List<ComboBoxItem>();
            if (fpsValues.Count == 0)
                fpsValues = new List<int> { 10, 15, 20, 25, 30 };
            foreach (var fps in fpsValues)
                fpsCbiList.Add(new ComboBoxItem { Content = $"{fps} FPS", Tag = fps });
            FpsComboBox.ItemsSource = fpsCbiList;
            var fpsMatch = fpsCbiList.FirstOrDefault(i => (int)i.Tag == currentFps);
            FpsComboBox.SelectedItem = fpsMatch ?? fpsCbiList.FirstOrDefault();
        }

        private static string GetResLabel(int w, int h)
        {
            if (w == 1280 && h == 720) return " (720P)";
            if (w == 1920 && h == 1080) return " (1080P)";
            if (w == 2560 && h == 1440) return " (2K)";
            if (w == 3840 && h == 2160) return " (4K)";
            return "";
        }

        private async void CameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingDevices) return;
            if (CameraComboBox.SelectedValue is int idx)
            {
                await LoadCameraCapabilitiesAsync(idx, Config.FrameWidth, Config.FrameHeight, Config.Fps);
            }
        }

        private void LoadGpuEncoders()
        {
            var encoders = MainViewModel.CachedEncoderOptions
                ?? new List<GpuEncoderOption>
                {
                    new GpuEncoderOption { Value = "auto", DisplayName = "自动检测（优先独显）" },
                    new GpuEncoderOption { Value = "cpu", DisplayName = "CPU 软编码" }
                };
            GpuEncoderComboBox.ItemsSource = encoders;
            // 兼容旧配置：将旧版编码器名映射为 GPU 标识
            string normalized = NormalizeGpuSetting(Config.GpuEncoder ?? "auto");
            var match = encoders.FirstOrDefault(e => e.Value == normalized)
                     ?? encoders.FirstOrDefault();
            GpuEncoderComboBox.SelectedItem = match;
        }

        private void LoadVideoCodecs()
        {
            var items = new[]
            {
                new GpuEncoderOption { Value = "h264", DisplayName = "H.264 (\u517c\u5bb9\u6027\u597d)" },
                new GpuEncoderOption { Value = "h265", DisplayName = "H.265 / HEVC (\u4f53\u79ef\u66f4\u5c0f)" },
                new GpuEncoderOption { Value = "av1",  DisplayName = "AV1 (\u6781\u81f4\u538b\u7f29\uff0c\u63a8\u8350)" }
            };
            VideoCodecComboBox.ItemsSource = items;
            string current = Config.VideoCodec?.ToLowerInvariant() ?? "h264";
            VideoCodecComboBox.SelectedItem = items.FirstOrDefault(i => i.Value == current) ?? items[0];
        }

        /// <summary>兼容旧版配置：将编码器名映射为 GPU 标识</summary>
        private static string NormalizeGpuSetting(string setting) => EncodingHelper.NormalizeGpuSetting(setting);

        private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "请选择视频保存的文件夹",
                // 指向我的电脑，避免网络驱动器引发卡顿
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
            };

            // 加上 this，强行把弹窗绑定在设置界面正上方，绝对不会跑到后台导致假死！
            if (dialog.ShowDialog(this) == true)
            {
                Config.VideoStoragePath = dialog.FolderName;
                PathTextBox.Text = dialog.FolderName;
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (ResComboBox.SelectedItem is ResOption selectedRes) { Config.FrameWidth = selectedRes.Width; Config.FrameHeight = selectedRes.Height; }
            if (FpsComboBox.SelectedItem is ComboBoxItem fpsItem && fpsItem.Tag is int fps) { Config.Fps = fps; }
            if (GpuEncoderComboBox.SelectedItem is GpuEncoderOption gpuOpt) { Config.GpuEncoder = gpuOpt.Value; }
            if (VideoCodecComboBox.SelectedItem is GpuEncoderOption codecOpt) { Config.VideoCodec = codecOpt.Value; }

            if (!ValidateEncoderSelectionBeforeSave())
                return;

            ApplyAutoStart(Config.AutoStartOnBoot);
            this.DialogResult = true; this.Close();
        }

        private bool ValidateEncoderSelectionBeforeSave()
        {
            string codec = (Config.VideoCodec ?? "h264").Trim().ToLowerInvariant();
            string gpu = NormalizeGpuSetting(Config.GpuEncoder ?? "auto");
            var validated = MainViewModel.ValidatedEncoders ?? new HashSet<string>();

            string requestedEncoder = EncodingHelper.ResolveRequestedEncoder(gpu, codec);
            string fallbackEncoder = EncodingHelper.ResolveFallbackEncoder(gpu, codec, validated);

            if (fallbackEncoder == requestedEncoder)
            {
                if (!string.Equals(NormalizeGpuSetting(Config.GpuEncoder ?? "auto"), NormalizeGpuSetting(fallbackEncoder), StringComparison.OrdinalIgnoreCase)
                    && gpu != "auto")
                {
                    string fallbackGpu = NormalizeGpuSetting(fallbackEncoder);
                    Config.GpuEncoder = string.IsNullOrEmpty(fallbackGpu) ? "cpu" : fallbackGpu;
                }
                return true;
            }

            string requestedLabel = EncodingHelper.GetEncoderLabel(requestedEncoder);
            string fallbackLabel = EncodingHelper.GetEncoderLabel(fallbackEncoder);

            // 该编解码器完全不可用：保存前直接改成可用方案
            if (codec != EncodingHelper.GetCodecFromEncoder(fallbackEncoder))
            {
                var result = MessageBox.Show(
                    $"当前设备或 FFmpeg 不支持 {EncodingHelper.GetCodecLabel(codec)}。\n\n" +
                    $"请求方案: {requestedLabel}\n" +
                    $"建议切换到: {fallbackLabel}\n\n" +
                    $"是否在保存时自动改为 {fallbackLabel}？",
                    "编码器不可用", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return false;

                EncodingHelper.ApplyEncoderSelectionToConfig(Config, fallbackEncoder);
                SyncEncoderComboboxes(fallbackEncoder);
                return true;
            }

            // 同一编解码器可用，但会回退到别的实现
            MessageBox.Show(
                $"当前选择的 {requestedLabel} 不可用。\n\n" +
                $"保存后实际会回退到: {fallbackLabel}\n\n" +
                $"设置将按可用方案保存。",
                "编码器将自动回退", MessageBoxButton.OK, MessageBoxImage.Information);

            EncodingHelper.ApplyEncoderSelectionToConfig(Config, fallbackEncoder);
            SyncEncoderComboboxes(fallbackEncoder);
            return true;
        }

        private void SyncEncoderComboboxes(string encoder)
        {
            string codec = EncodingHelper.GetCodecFromEncoder(encoder);
            string gpu = NormalizeGpuSetting(encoder);

            if (VideoCodecComboBox.ItemsSource is IEnumerable<GpuEncoderOption> codecs)
                VideoCodecComboBox.SelectedItem = codecs.FirstOrDefault(i => i.Value == codec);

            if (GpuEncoderComboBox.ItemsSource is IEnumerable<GpuEncoderOption> gpus)
                GpuEncoderComboBox.SelectedItem = gpus.FirstOrDefault(i => i.Value == gpu);
        }


        private void ApplyAutoStart(bool enable)
        {
            const string regKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            const string appName = "ExpressPackingMonitoring";
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(regKey, true);
                if (key == null) return;
                if (enable)
                {
                    string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(exePath))
                        key.SetValue(appName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(appName, false);
                }
            }
            catch { }
        }

        private bool IsAutoStartEnabled()
        {
            const string regKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            const string appName = "ExpressPackingMonitoring";
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(regKey, false);
                return key?.GetValue(appName) != null;
            }
            catch { return false; }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (Config.Theme != _originalTheme)
            {
                if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(_originalTheme, out var themeEnum))
                {
                    ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
                }
            }
            this.DialogResult = false;
            this.Close();
        }
    }
}



