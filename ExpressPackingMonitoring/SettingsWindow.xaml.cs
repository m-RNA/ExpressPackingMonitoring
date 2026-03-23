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
using System.IO;

namespace ExpressPackingMonitoring
{
    public class CameraInfo { public int Index { get; set; } public string Name { get; set; } public string Moniker { get; set; } public override string ToString() => Name; }
    public class ResOption { public string Name { get; set; } public int Width { get; set; } public int Height { get; set; } public override string ToString() => Name; }
    public class MicInfo { public string Name { get; set; } public override string ToString() => Name; }
    public class FpsOption { public int Fps { get; set; } public string Label { get; set; } public override string ToString() => Label; }

    public partial class SettingsWindow : Window
    {
        public MainViewModel MainVM { get; set; }
        public AppConfig Config { get; set; }
        public double CurrentDiskUsagePercent { get; set; }
        public string CurrentDiskUsageText { get; set; }

        private string _originalTheme;
        private bool _isRecording;
        private bool _isLoadingDevices;

        public SettingsWindow(MainViewModel mainVM, AppConfig clonedConfig, double diskUsagePercent, string diskUsageText, bool isRecording = false)
        {
            InitializeComponent();
            MainVM = mainVM;
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

            EnsurePrimaryStorageLocationExists();
            // 如果没有数据项，构造1个默认项，UI DataGrid 绑定后自动显示
            if (Config.StorageLocations.Count == 0)
            {
                Config.StorageLocations.Add(new StorageLocation());
            }

            // 从注册表读取实际的开机自启动状态
            Config.AutoStartOnBoot = IsAutoStartEnabled();

            // 窗口加载后异步枚举设备，避免阻塞UI线程
            this.Loaded += SettingsWindow_Loaded;
        }

        private void EnsurePrimaryStorageLocationExists()
        {
            if (Config.StorageLocations == null) Config.StorageLocations = new List<StorageLocation>();
            if (Config.StorageLocations.Count == 0)
            {
                Config.StorageLocations.Add(new StorageLocation());
            }
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
                        cams.Add(new CameraInfo { Index = i, Name = $"[{i}] {videoDevices[i].Name}", Moniker = videoDevices[i].MonikerString });

                    string targetMoniker = config.CameraMonikerString;
                    int targetIndex = -1;
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

                    if (targetIndex == -1 && config.CameraIndex >= 0 && config.CameraIndex < videoDevices.Count)
                    {
                        targetIndex = config.CameraIndex;
                    }

                    if (targetIndex != -1)
                    {
                        var device = new VideoCaptureDevice(videoDevices[targetIndex].MonikerString);
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
            if (CameraComboBox.SelectedItem is CameraInfo cam)
            {
                // 加载该摄像头的独立配置（如果存在）
                int w = Config.FrameWidth;
                int h = Config.FrameHeight;
                int fps = Config.Fps;

                if (!string.IsNullOrEmpty(cam.Moniker) && Config.CameraConfigs.TryGetValue(cam.Moniker, out var settings))
                {
                    w = settings.FrameWidth;
                    h = settings.FrameHeight;
                    fps = settings.Fps;
                    Config.AudioDeviceName = settings.AudioDeviceName ?? "";
                    Config.AudioSyncOffsetMs = settings.AudioSyncOffsetMs;

                    // 切换麦克风 UI 选中项
                    if (MicComboBox.ItemsSource is List<MicInfo> mics)
                    {
                        var micMatch = mics.FirstOrDefault(m => m.Name == Config.AudioDeviceName);
                        if (micMatch != null)
                        {
                            MicComboBox.SelectedItem = micMatch;
                        }
                    }
                }

                await LoadCameraCapabilitiesAsync(cam.Index, w, h, fps);
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

        private static string NormalizeGpuSetting(string setting) => EncodingHelper.NormalizeGpuSetting(setting);

        private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
        {
            EnsurePrimaryStorageLocationExists();
            var primary = Config.StorageLocations[0];
            var initialDir = !string.IsNullOrWhiteSpace(primary.Path) ? primary.Path : AppDomain.CurrentDomain.BaseDirectory;

            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择视频存储位置",
                InitialDirectory = initialDir,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) == true)
            {
                if (!IsPathWritable(dialog.FolderName))
                {
                    MessageBox.Show("所选路径不可写，请检查权限或磁盘状态。", "存储错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                primary.Path = dialog.FolderName;
            }
        }

        private bool IsPathWritable(string path)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                string testFile = Path.Combine(path, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch { return false; }
        }

        private void BtnAddStorage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择新的视频存储位置",
                Multiselect = false
            };

            if (dialog.ShowDialog(this) == true)
            {
                string selectedPath = dialog.FolderName;
                if (!IsPathWritable(selectedPath))
                {
                    MessageBox.Show("所选路径不可写，请检查权限或磁盘状态。", "存储错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (Config.StorageLocations.Any(x => x.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show("该路径已在列表中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                int nextPriority = Config.StorageLocations.Count > 0 ? Config.StorageLocations.Max(x => x.Priority) + 1 : 0;
                Config.StorageLocations.Add(new StorageLocation
                {
                    Path = selectedPath,
                    QuotaGB = 500.0,
                    Priority = nextPriority
                });

                StorageDataGrid.Items.Refresh();
            }
        }

        private void BtnRemoveStorage_Click(object sender, RoutedEventArgs e)
        {
            if (StorageDataGrid.SelectedItem is StorageLocation selected)
            {
                if (Config.StorageLocations.Count <= 1)
                {
                    MessageBox.Show("至少需要保留一个存储路径。", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = MessageBox.Show($"确定要移除路径: {selected.Path} 吗？\n注意：此操作不会删除物理文件，但系统将不再管理该目录。",
                                             "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    Config.StorageLocations.Remove(selected);
                    StorageDataGrid.Items.Refresh();
                }
            }
            else
            {
                MessageBox.Show("请先在列表中选中要移除的行。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 0. 验证音频
            if (Config.EnableAudioRecording && string.IsNullOrEmpty(Config.AudioDeviceName))
            {
                var mbr = MessageBox.Show("已开启录制声音，但未选择麦克风。录制可能会失败或没有声音。\n\n是否继续保存？", "音频提醒", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (mbr == MessageBoxResult.No) return;
            }

            // 1. 强制提交 DataGrid 中的未完成编辑
            StorageDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
            StorageDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);

            // 2. 手动同步部分控件（防止可焦点未切换时绑定未更新）
            if (CameraComboBox.SelectedItem is CameraInfo cam)
            {
                Config.CameraMonikerString = cam.Moniker;
                Config.CameraIndex = cam.Index;

                if (ResComboBox.SelectedItem is ResOption selectedRes)
                {
                    Config.FrameWidth = selectedRes.Width;
                    Config.FrameHeight = selectedRes.Height;
                }

                if (FpsComboBox.SelectedItem is ComboBoxItem fpsItem && fpsItem.Tag is int fps)
                {
                    Config.Fps = fps;
                }

                // 更新此摄像头的独立配置
                if (!string.IsNullOrEmpty(cam.Moniker))
                {
                    Config.CameraConfigs[cam.Moniker] = new CameraSettings
                    {
                        FrameWidth = Config.FrameWidth,
                        FrameHeight = Config.FrameHeight,
                        Fps = Config.Fps,
                        AudioDeviceName = Config.AudioDeviceName,
                        AudioSyncOffsetMs = Config.AudioSyncOffsetMs
                    };
                }
            }

            if (GpuEncoderComboBox.SelectedItem is GpuEncoderOption gpuOpt)
            {
                Config.GpuEncoder = gpuOpt.Value;
            }

            if (VideoCodecComboBox.SelectedItem is GpuEncoderOption codecOpt)
            {
                Config.VideoCodec = codecOpt.Value;
            }

            // 3. 校验并保存
            if (!ValidateEncoderSelectionBeforeSave())
                return;

            ApplyAutoStart(Config.AutoStartOnBoot);
            MainVM.PreviewZoomScale = null;
            this.DialogResult = true;
            this.Close();
        }

        private void ZoomScaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ZoomScaleComboBox.SelectedItem is double scale)
            {
                MainVM.PreviewZoomScale = scale;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            MainVM.PreviewZoomScale = null;
            base.OnClosed(e);
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
