#nullable disable
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using System;
using System.Windows;
using System.Collections.Generic;
using Microsoft.Win32;
using ExpressPackingMonitoring.ViewModels;
using AForge.Video.DirectShow;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ExpressPackingMonitoring.Localization;
using System.Windows.Media.Imaging;
using ExpressPackingMonitoring.Services;
using NAudio.CoreAudioApi;
using System.Text.Json;

namespace ExpressPackingMonitoring.UI
{
    public class CameraInfo { public int Index { get; set; } public string Name { get; set; } public string Moniker { get; set; } public override string ToString() => Name; }
    public class ResOption { public string Name { get; set; } public int Width { get; set; } public int Height { get; set; } public override string ToString() => Name; }
    public class MicInfo
    {
        public string Name { get; set; }
        public string Moniker { get; set; }
        public override string ToString() => Name;
    }
    public class FpsOption { public int Fps { get; set; } public string Label { get; set; } public override string ToString() => Label; }
    public class EdgeVoiceOption { public string ShortName { get; set; } public string DisplayName { get; set; } public override string ToString() => DisplayName; }

    public sealed class CqpToQualitySliderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double cqp = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return Math.Clamp((51 - cqp) * 2.0, 0, 100);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double quality = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            int cqp = (int)Math.Round(51 - Math.Clamp(quality, 0, 100) / 2.0);
            return Math.Clamp(cqp, 1, 51);
        }
    }

    public sealed class IntSliderValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return 0d;
            return System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double sliderValue = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return (int)Math.Round(sliderValue);
        }
    }

    public sealed class VideoQualityLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double quality = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (quality < 34) return "更省空间";
            if (quality < 67) return "标准（推荐）";
            return "更清晰";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    public sealed class AnyTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            return values.Any(value => value is bool boolean && boolean);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }

    public partial class SettingsWindow : Window
    {
        public MainViewModel MainVM { get; set; }
        public AppConfig Config { get; set; }
        public double CurrentDiskUsagePercent { get; set; }
        public string CurrentDiskUsageText { get; set; }
        public string AppVersion { get; } = ExpressPackingMonitoring.Config.AppVersion.Current;
        public string AppBuildDate { get; } = ExpressPackingMonitoring.Config.AppVersion.BuildDateText;
        public ImageSource AppIconImage { get; } = GetLargestAppIconImage();
        public List<EdgeVoiceOption> EdgeVoiceOptions { get; } = new()
        {
            new EdgeVoiceOption { ShortName = "zh-CN-XiaoxiaoNeural", DisplayName = "晓晓 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-CN-XiaoyiNeural", DisplayName = "晓伊 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunjianNeural", DisplayName = "云健 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunxiNeural", DisplayName = "云希 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunxiaNeural", DisplayName = "云夏 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunyangNeural", DisplayName = "云扬 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-liaoning-XiaobeiNeural", DisplayName = "辽宁晓北 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-CN-shaanxi-XiaoniNeural", DisplayName = "陕西晓妮 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-HK-HiuGaaiNeural", DisplayName = "粤语 HiuGaai - 女声" },
            new EdgeVoiceOption { ShortName = "zh-HK-WanLungNeural", DisplayName = "粤语 WanLung - 男声" },
            new EdgeVoiceOption { ShortName = "zh-TW-HsiaoChenNeural", DisplayName = "台湾晓臻 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-TW-YunJheNeural", DisplayName = "台湾云哲 - 男声" },
            new EdgeVoiceOption { ShortName = "en-US-JennyNeural", DisplayName = "Jenny - Female (US)" },
            new EdgeVoiceOption { ShortName = "en-US-AriaNeural", DisplayName = "Aria - Female (US)" },
            new EdgeVoiceOption { ShortName = "en-US-GuyNeural", DisplayName = "Guy - Male (US)" },
            new EdgeVoiceOption { ShortName = "en-US-DavisNeural", DisplayName = "Davis - Male (US)" }
        };

        private string _originalTheme;
        private string _originalLanguage;
        private bool _isRecording;
        private bool _isLoadingDevices;
        private bool _isSyncingVoiceEngine;
        private bool _isSyncingScannerModes;

        public SettingsWindow(MainViewModel mainVM, AppConfig clonedConfig, double diskUsagePercent, string diskUsageText, bool isRecording = false)
        {
            InitializeComponent();
            MainVM = mainVM;
            _originalTheme = clonedConfig.Theme;
            _originalLanguage = clonedConfig.Language;
            _isRecording = isRecording;
            Config = clonedConfig;
            AppConfig.NormalizeAfterLoad(Config);

            CurrentDiskUsagePercent = diskUsagePercent;
            CurrentDiskUsageText = diskUsageText;

            this.DataContext = this;
            SyncVoiceEngineComboBoxFromConfig();

            // GPU编码器使用缓存，可立即加载
            LoadGpuEncoders();
            LoadVideoCodecs();

            if (Config.ZoomScale < 1.2 || Config.ZoomScale > 4.0) Config.ZoomScale = 1.5;

            EnsurePrimaryStorageLocationExists();
            // 如果没有数据项，构造1个默认项，UI DataGrid 绑定后自动显示
            if (Config.StorageLocations.Count == 0)
            {
                Config.StorageLocations.Add(new StorageLocation());
            }
            SortStorageLocationsByPriority();
            RefreshStoragePriorities();
            UpdateStorageButtonStates();

            // 从注册表读取实际的开机自启动状态
            Config.AutoStartOnBoot = IsAutoStartEnabled();

            // 窗口加载后异步枚举设备，避免阻塞UI线程
            this.Loaded += SettingsWindow_Loaded;
        }

        private void GlobalKeyboardCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isSyncingScannerModes) return;

            try
            {
                _isSyncingScannerModes = true;
                Config.EnableGlobalKeyboard = true;
                Config.EnableScannerAutoSubmit = false;
                if (ScannerAutoSubmitCheckBox != null)
                {
                    ScannerAutoSubmitCheckBox.IsChecked = false;
                }
            }
            finally
            {
                _isSyncingScannerModes = false;
            }
        }

        private void ScannerAutoSubmitCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isSyncingScannerModes) return;

            try
            {
                _isSyncingScannerModes = true;
                Config.EnableScannerAutoSubmit = true;
                Config.EnableGlobalKeyboard = false;
                if (GlobalKeyboardCheckBox != null)
                {
                    GlobalKeyboardCheckBox.IsChecked = false;
                }
            }
            finally
            {
                _isSyncingScannerModes = false;
            }
        }

        private void SyncScannerModeControlsFromConfig()
        {
            if (GlobalKeyboardCheckBox == null || ScannerAutoSubmitCheckBox == null)
                return;

            try
            {
                _isSyncingScannerModes = true;
                GlobalKeyboardCheckBox.IsChecked = Config.EnableGlobalKeyboard;
                ScannerAutoSubmitCheckBox.IsChecked = Config.EnableScannerAutoSubmit;
            }
            finally
            {
                _isSyncingScannerModes = false;
            }
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

            // 加载断句关键词到文本框
            if (Config.TtsBreakWords != null && Config.TtsBreakWords.Count > 0)
                TtsBreakWordsTextBox.Text = string.Join("\n", Config.TtsBreakWords);

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
            if (ThemeComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag != null)
            {
                string t = item.Tag.ToString();
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
                    using var enumerator = new MMDeviceEnumerator();
                    var audioDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                    for (int i = 0; i < audioDevices.Count; i++)
                        micList.Add(new MicInfo { Name = audioDevices[i].FriendlyName, Moniker = audioDevices[i].ID });
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
            var firstAvailableMic = mics.FirstOrDefault(IsAvailableMic);
            if (string.IsNullOrEmpty(config.AudioDeviceName) && firstAvailableMic != null)
            {
                config.AudioDeviceName = firstAvailableMic.Name;
                config.AudioDeviceMoniker = firstAvailableMic.Moniker ?? "";
            }
            SelectMicByConfig(mics);

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
                    Config.AudioDeviceMoniker = settings.AudioDeviceMoniker ?? "";
                    Config.AudioSyncOffsetMs = settings.AudioSyncOffsetMs;

                    // 切换麦克风 UI 选中项
                    if (MicComboBox.ItemsSource is List<MicInfo> mics)
                    {
                        SelectMicByConfig(mics);
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
                new GpuEncoderOption { Value = "h264", DisplayName = "H.264 (兼容性好)" },
                new GpuEncoderOption { Value = "h265", DisplayName = "H.265 / HEVC (体积更小)" },
                new GpuEncoderOption { Value = "av1",  DisplayName = "AV1 (极致压缩，推荐)" }
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

            string selectedPath = SelectDefaultStoragePathFromDrive();
            if (string.IsNullOrWhiteSpace(selectedPath)) return;

            if (!TryPrepareStoragePath(selectedPath, out string errorMessage))
            {
                MessageBox.Show($"无法创建或写入目录：\n{selectedPath}\n\n原因：{errorMessage}", "存储错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            primary.Path = selectedPath;
        }

        private bool IsPathWritable(string path)
        {
            return TryPrepareStoragePath(path, out _);
        }

        private void BtnAddStorage_Click(object sender, RoutedEventArgs e)
        {
            string selectedPath = SelectDefaultStoragePathFromDrive();
            if (string.IsNullOrWhiteSpace(selectedPath)) return;

            if (Config.StorageLocations.Any(x => string.Equals(x.Path, selectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("该路径已在列表中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string selectedRoot = GetStorageRoot(selectedPath);
            StorageLocation sameDisk = Config.StorageLocations.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.Path) &&
                string.Equals(GetStorageRoot(x.Path), selectedRoot, StringComparison.OrdinalIgnoreCase));
            if (sameDisk != null)
            {
                MessageBox.Show(
                    $"同一个磁盘已经添加过：\n{sameDisk.Path}\n\n请换一个磁盘，或直接调整已有路径的容量和列表顺序。",
                    "磁盘已存在",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!TryPrepareStoragePath(selectedPath, out string errorMessage))
            {
                MessageBox.Show($"无法创建或写入目录：\n{selectedPath}\n\n原因：{errorMessage}", "存储错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Config.StorageLocations.Add(new StorageLocation
            {
                Path = selectedPath,
                ReserveGB = StorageSpacePolicy.GetMinimumReserveGB(selectedPath),
                Priority = Config.StorageLocations.Count
            });

            RefreshStoragePriorities();
            StorageDataGrid.Items.Refresh();
            StorageDataGrid.SelectedIndex = Config.StorageLocations.Count - 1;
            UpdateStorageButtonStates();
        }

        private string SelectDefaultStoragePathFromDrive()
        {
            var dialog = new DriveSelectionDialog(Config.StorageLocations.Select(location => location.Path))
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedRootPath))
                return "";

            return Path.Combine(dialog.SelectedRootPath, "快递打包视频");
        }

        private bool TryPrepareStoragePath(string path, out string errorMessage)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                string testFile = Path.Combine(path, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                errorMessage = "";
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
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
                    int selectedIndex = StorageDataGrid.SelectedIndex;
                    Config.StorageLocations.Remove(selected);
                    RefreshStoragePriorities();
                    StorageDataGrid.Items.Refresh();
                    if (Config.StorageLocations.Count > 0)
                    {
                        StorageDataGrid.SelectedIndex = Math.Min(selectedIndex, Config.StorageLocations.Count - 1);
                    }
                    UpdateStorageButtonStates();
                }
            }
            else
            {
                MessageBox.Show("请先在列表中选中要移除的行。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void StorageDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateStorageButtonStates();
        }

        private void StorageReserveEditor_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: StorageLocation location })
            {
                location.EffectiveReserveGB = location.EffectiveReserveGB;
                StorageDataGrid.Items.Refresh();
            }
        }

        private void BtnMoveStorageUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedStorage(-1);
        }

        private void BtnMoveStorageDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedStorage(1);
        }

        private void MoveSelectedStorage(int direction)
        {
            if (StorageDataGrid?.SelectedItem is not StorageLocation selected) return;

            int oldIndex = Config.StorageLocations.IndexOf(selected);
            int newIndex = oldIndex + direction;
            if (oldIndex < 0 || newIndex < 0 || newIndex >= Config.StorageLocations.Count) return;

            Config.StorageLocations.RemoveAt(oldIndex);
            Config.StorageLocations.Insert(newIndex, selected);
            RefreshStoragePriorities();
            StorageDataGrid.Items.Refresh();
            StorageDataGrid.SelectedIndex = newIndex;
            UpdateStorageButtonStates();
        }

        private void SortStorageLocationsByPriority()
        {
            if (Config.StorageLocations == null || Config.StorageLocations.Count <= 1) return;

            var ordered = Config.StorageLocations
                .Select((location, index) => new { Location = location, Index = index })
                .OrderBy(x => x.Location.Priority)
                .ThenBy(x => x.Index)
                .Select(x => x.Location)
                .ToList();

            Config.StorageLocations.Clear();
            Config.StorageLocations.AddRange(ordered);
        }

        private void RefreshStoragePriorities()
        {
            if (Config.StorageLocations == null) return;

            for (int i = 0; i < Config.StorageLocations.Count; i++)
            {
                Config.StorageLocations[i].Priority = i;
            }
        }

        private void UpdateStorageButtonStates()
        {
            if (RemoveStorageButton == null) return;

            bool hasSelection = StorageDataGrid?.SelectedItem is StorageLocation;
            int selectedIndex = StorageDataGrid?.SelectedIndex ?? -1;
            int count = Config.StorageLocations?.Count ?? 0;

            RemoveStorageButton.IsEnabled = hasSelection;
            if (MoveStorageUpButton != null) MoveStorageUpButton.IsEnabled = hasSelection && selectedIndex > 0;
            if (MoveStorageDownButton != null) MoveStorageDownButton.IsEnabled = hasSelection && selectedIndex >= 0 && selectedIndex < count - 1;
        }

        private async void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (await SaveAndApplyAsync())
            {
                DialogResult = true;
                Close();
            }
        }

        private async void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            await SaveAndApplyAsync();
        }

        private async Task<bool> SaveAndApplyAsync()
        {
            Keyboard.ClearFocus();
            SyncSelectedMicToConfig();

            // 0. 验证音频
            if (Config.EnableAudioRecording && string.IsNullOrEmpty(Config.AudioDeviceName))
            {
                var mbr = MessageBox.Show("已开启录制声音，但未选择麦克风。录制可能会失败或没有声音。\n\n是否继续保存？", "音频提醒", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (mbr == MessageBoxResult.No) return false;
            }

            // 1. 强制提交 DataGrid 中的未完成编辑
            StorageDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
            StorageDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            RefreshStoragePriorities();

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
                        AudioDeviceMoniker = Config.AudioDeviceMoniker,
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

            // 保存断句关键词
            Config.TtsBreakWords = TtsBreakWordsTextBox.Text
                .Split(new[] { '\r', '\n', '，', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim())
                .Where(w => w.Length > 0)
                .Distinct()
                .ToList();

            // 3. 校验并保存
            if (AppLanguage.Resolve(Config.Language) == AppLanguage.Chinese)
            {
                Config.EdgeTtsVoiceZhHans = Config.EdgeTtsVoice;
                Config.EdgeTtsWarningVoiceZhHans = Config.EdgeTtsWarningVoice;
            }
            else
            {
                Config.EdgeTtsVoiceEnUs = Config.EdgeTtsVoice;
                Config.EdgeTtsWarningVoiceEnUs = Config.EdgeTtsWarningVoice;
            }
            AppConfig.NormalizeAfterLoad(Config);

            if (!ValidateEncoderSelectionBeforeSave())
                return false;

            ApplyAutoStart(Config.AutoStartOnBoot);
            MainVM.PreviewZoomScale = null;
            var appliedConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
            bool applied = await MainVM.ApplySettingsAsync(appliedConfig);
            if (applied)
            {
                _originalTheme = Config.Theme;
                if (_originalLanguage != Config.Language)
                {
                    MessageBox.Show(
                        AppLanguage.Get("RestartSaved"),
                        AppLanguage.Get("RestartRequired"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    _originalLanguage = Config.Language;
                }
            }
            return applied;
        }

        private async void RunSetupWizard_Click(object sender, RoutedEventArgs e)
        {
            Keyboard.ClearFocus();
            SyncSelectedMicToConfig();

            bool pausedCamera = false;
            try
            {
                if (!_isRecording)
                {
                    pausedCamera = MainVM.SuspendCameraForSetupWizard();
                    if (!pausedCamera)
                        return;
                }

                var wizard = new FirstUseSetupWizardWindow(Config) { Owner = this };
                if (wizard.ShowDialog() == true && !wizard.WasSkipped)
                {
                    Config.FirstUseWizardCompleted = true;
                    AppConfig.NormalizeAfterLoad(Config);
                    SyncScannerModeControlsFromConfig();
                    _isLoadingDevices = true;
                    try
                    {
                        await LoadAllDevicesAsync();
                    }
                    finally
                    {
                        _isLoadingDevices = false;
                    }
                }
            }
            finally
            {
                if (pausedCamera)
                    MainVM.ResumeCameraAfterSetupWizard();
            }
        }

        private void ZoomScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MainVM != null)
                MainVM.PreviewZoomScale = e.NewValue;
        }

        private void SyncVoiceEngineComboBoxFromConfig()
        {
            if (VoiceEngineComboBox == null) return;

            _isSyncingVoiceEngine = true;
            VoiceEngineComboBox.SelectedValue = Config.EnableAiTts
                ? NormalizeVoiceEngine(Config.AiTtsEngine)
                : "System";
            _isSyncingVoiceEngine = false;
        }

        private void VoiceEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingVoiceEngine || Config == null) return;

            string engine = VoiceEngineComboBox.SelectedValue?.ToString() ?? "System";
            if (string.Equals(engine, "System", StringComparison.OrdinalIgnoreCase))
            {
                Config.EnableAiTts = false;
                return;
            }

            Config.EnableAiTts = true;
            Config.AiTtsEngine = NormalizeVoiceEngine(engine);
        }

        private static string NormalizeVoiceEngine(string engine)
        {
            return string.Equals(engine, "Kokoro", StringComparison.OrdinalIgnoreCase) ? "Kokoro" : "Edge";
        }

        private void InstallTool_Click(object sender, RoutedEventArgs e)
        {
            int port = Config?.WebServerPort > 0 ? Config.WebServerPort : 5280;
            string address = WorkstationNetwork.NormalizeAddress(MainVM?.MonitorAccessAddress ?? "", port);
            if (string.IsNullOrWhiteSpace(address))
            {
                address = WorkstationNetwork.NormalizeAddress($"127.0.0.1:{port}", port);
            }

            string guidePath = PrintToolInstallGuide.CreateLocalGuide(address);
            try { Clipboard.SetDataObject(address, true); } catch { }

            Process.Start(new ProcessStartInfo(new Uri(guidePath).AbsoluteUri) { UseShellExecute = true });
            MessageBox.Show(this,
                $"已打开订单备注插件安装向导，并复制监控工位地址：{address}",
                "安装订单备注插件",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void SelectMicByConfig(List<MicInfo> mics)
        {
            var micMatch = mics.FirstOrDefault(m => !string.IsNullOrEmpty(Config.AudioDeviceMoniker)
                                                    && m.Moniker == Config.AudioDeviceMoniker)
                        ?? mics.FirstOrDefault(m => m.Name == Config.AudioDeviceName);
            if (micMatch != null)
            {
                MicComboBox.SelectedItem = micMatch;
                if (IsAvailableMic(micMatch))
                {
                    Config.AudioDeviceName = micMatch.Name;
                    Config.AudioDeviceMoniker = micMatch.Moniker ?? "";
                }
            }
        }

        private void SyncSelectedMicToConfig()
        {
            if (MicComboBox.SelectedItem is MicInfo mic && IsAvailableMic(mic))
            {
                Config.AudioDeviceName = mic.Name;
                Config.AudioDeviceMoniker = mic.Moniker ?? "";
            }
            else
            {
                Config.AudioDeviceName = "";
                Config.AudioDeviceMoniker = "";
            }
        }

        private static bool IsAvailableMic(MicInfo mic)
        {
            return mic != null
                && !string.IsNullOrWhiteSpace(mic.Name)
                && mic.Name != "未检测到麦克风";
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;
            var migrationCts = Interlocked.Exchange(ref _migrationCts, null);
            try { migrationCts?.Cancel(); } catch (ObjectDisposedException) { }
            MainVM.PreviewZoomScale = null;
            _previewSpeechService?.Stop();
            _previewSpeechService?.Dispose();
            _previewSpeechService = null;
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

        private void OpenRepository_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl("https://github.com/m-RNA/ExpressPackingMonitoring");
        }

        private void OpenLicense_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl("https://github.com/m-RNA/ExpressPackingMonitoring/blob/main/LICENSE");
        }

        private static string GetStorageRoot(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path.Trim());
                return Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? fullPath;
            }
            catch
            {
                return path?.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "";
            }
        }

        private static ImageSource GetLargestAppIconImage()
        {
            var decoder = BitmapDecoder.Create(
                new Uri("pack://application:,,,/app.ico", UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames
                .OrderByDescending(x => x.PixelWidth * x.PixelHeight)
                .First();
            frame.Freeze();
            return frame;
        }

        private static void OpenExternalUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开链接：{ex.Message}", "打开链接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateButton.Content = "正在检查...";

            try
            {
                var service = new UpdateCheckService();
                var result = await service.CheckManualAsync();
                if (!result.HasUpdate)
                {
                    CheckUpdateButton.Content = "已为最新";
                    return;
                }

                CheckUpdateButton.Content = "发现新版本";
                ShowUpdateDialog(result);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Update", "Manual update check failed", ex);
                CheckUpdateButton.Content = "检查失败";
                CheckUpdateButton.IsEnabled = true;
            }
        }

        private void ShowUpdateDialog(UpdateCheckResult result)
        {
            var dialog = new UpdateAvailableDialog(result)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    UpdateCheckService.OpenDownloadPage(dialog.DownloadUrl);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("Update", "Open download page failed", ex);
                    MainVM.ShowToast("打开下载页面失败");
                }
            }
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

        private SpeechService _previewSpeechService;

        private void BtnTtsPreview_Click(object sender, RoutedEventArgs e)
        {
            Keyboard.ClearFocus();

            string text = TtsPreviewTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                TtsPreviewStatus.Text = "请输入预览文本";
                return;
            }

            // 显示预处理后的文本
            string processed = SpeechService.PreprocessTextForTts(text);
            TtsPreviewStatus.Text = $"断句: {processed}";

            // 初始化或复用预览用 SpeechService
            if (_previewSpeechService == null)
            {
                _previewSpeechService = new SpeechService
                {
                    EnableSoundPrompt = true,
                    EnableAiTts = Config.EnableAiTts,
                    AiTtsEngine = Config.AiTtsEngine,
                    AiTtsSpeakerId = Config.AiTtsSpeakerId,
                    AiTtsWarningSpeakerId = Config.AiTtsWarningSpeakerId,
                    AiTtsSpeed = Config.AiTtsSpeed,
                    EdgeTtsVoice = Config.EdgeTtsVoice,
                    EdgeTtsWarningVoice = Config.EdgeTtsWarningVoice,
                };
                _previewSpeechService.PlaybackError += OnPreviewSpeechError;
                // 同步当前编辑中的断句关键词
                var words = TtsBreakWordsTextBox.Text
                    .Split(new[] { '\r', '\n', '，', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim()).Where(w => w.Length > 0);
                _previewSpeechService.UpdateBreakWords(words);
                if (Config.EnableAiTts)
                    _previewSpeechService.InitAiTts();
            }
            else
            {
                // 更新参数
                _previewSpeechService.EnableAiTts = Config.EnableAiTts;
                _previewSpeechService.AiTtsEngine = Config.AiTtsEngine;
                _previewSpeechService.AiTtsSpeakerId = Config.AiTtsSpeakerId;
                _previewSpeechService.AiTtsWarningSpeakerId = Config.AiTtsWarningSpeakerId;
                _previewSpeechService.AiTtsSpeed = Config.AiTtsSpeed;
                _previewSpeechService.EdgeTtsVoice = Config.EdgeTtsVoice;
                _previewSpeechService.EdgeTtsWarningVoice = Config.EdgeTtsWarningVoice;
                var words = TtsBreakWordsTextBox.Text
                    .Split(new[] { '\r', '\n', '，', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim()).Where(w => w.Length > 0);
                _previewSpeechService.UpdateBreakWords(words);
            }

            _previewSpeechService.Preview(text);
        }

        private void BtnTtsStop_Click(object sender, RoutedEventArgs e)
        {
            _previewSpeechService?.Stop();
            TtsPreviewStatus.Text = "已停止";
        }

        private void OnPreviewSpeechError(string message)
        {
            Dispatcher.InvokeAsync(() => TtsPreviewStatus.Text = $"试听失败：{message}");
        }

        private CancellationTokenSource _migrationCts;
        private bool _isClosing;

        private async void BtnMigrateMkv_Click(object sender, RoutedEventArgs e)
        {
            var runningMigration = _migrationCts;
            if (runningMigration != null)
            {
                // 正在迁移中，点击取消
                runningMigration.Cancel();
                return;
            }

            var migrationCts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref _migrationCts, migrationCts, null) != null)
            {
                migrationCts.Dispose();
                return;
            }

            BtnMigrateMkv.Content = "取消合并";
            MigrationProgress.Visibility = Visibility.Visible;
            MigrationStatusText.Text = "正在扫描 MKV 记录...";

            var progress = new Progress<string>(msg =>
            {
                if (!_isClosing)
                    MigrationStatusText.Text = msg;
            });

            try
            {
                var (success, fail, skip) = await MainVM.BatchConvertMkvToMp4Async(progress, migrationCts.Token);
                if (!_isClosing)
                    MigrationStatusText.Text = $"合并完成：成功 {success}，失败 {fail}，跳过 {skip}";
            }
            catch (OperationCanceledException)
            {
                if (!_isClosing)
                    MigrationStatusText.Text = "合并已取消";
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                    MigrationStatusText.Text = $"合并出错：{ex.Message}";
            }
            finally
            {
                Interlocked.CompareExchange(ref _migrationCts, null, migrationCts);
                migrationCts.Dispose();
                if (!_isClosing)
                {
                    BtnMigrateMkv.Content = "开始合并";
                    MigrationProgress.Visibility = Visibility.Collapsed;
                }
            }
        }
    }
}
