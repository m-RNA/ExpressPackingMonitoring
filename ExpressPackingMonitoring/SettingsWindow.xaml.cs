#nullable disable
using System;
using System.Windows;
using System.Collections.Generic;
using Microsoft.Win32;
using ExpressPackingMonitoring.ViewModels;
using AForge.Video.DirectShow;
using System.Linq;
using System.Windows.Controls;

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
        public SettingsWindow(AppConfig clonedConfig, double diskUsagePercent, string diskUsageText, bool isRecording = false)
        {
            InitializeComponent();
            _originalTheme = clonedConfig.Theme;
            Config = clonedConfig;

            CurrentDiskUsagePercent = diskUsagePercent;
            CurrentDiskUsageText = diskUsageText;

            this.DataContext = this;

            LoadCameras();
            LoadMicrophones();
            LoadPresets(clonedConfig);
            LoadFpsOptions(clonedConfig.CameraIndex, clonedConfig.Fps);

            // 从注册表读取实际的开机自启动状态
            Config.AutoStartOnBoot = IsAutoStartEnabled();

            // 录制中禁用摄像头相关控件，提示下次生效
            if (isRecording)
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

        private void LoadPresets(AppConfig config)
        {
            LoadResolutionOptions(config.CameraIndex, config.FrameWidth, config.FrameHeight);

            ZoomScaleComboBox.ItemsSource = new List<double> { 1.2, 1.5, 2.0, 2.5, 3.0, 4.0 };
            if (!ZoomScaleComboBox.Items.Contains(config.ZoomScale)) config.ZoomScale = 1.5;
        }

        private void LoadResolutionOptions(int cameraIndex, int currentWidth, int currentHeight)
        {
            var resOptions = new List<ResOption>();
            try
            {
                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (cameraIndex >= 0 && cameraIndex < videoDevices.Count)
                {
                    var device = new VideoCaptureDevice(videoDevices[cameraIndex].MonikerString);
                    resOptions = device.VideoCapabilities
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
                }
            }
            catch { }

            if (resOptions.Count == 0)
            {
                resOptions = new List<ResOption> {
                    new ResOption { Name = "720P - 省空间", Width = 1280, Height = 720 },
                    new ResOption { Name = "1080P - 高清", Width = 1920, Height = 1080 },
                    new ResOption { Name = "2K - 超清", Width = 2560, Height = 1440 },
                    new ResOption { Name = "4K - 极清", Width = 3840, Height = 2160 }
                };
            }

            ResComboBox.ItemsSource = resOptions;
            var match = resOptions.FirstOrDefault(r => r.Width == currentWidth && r.Height == currentHeight);
            ResComboBox.SelectedItem = match ?? resOptions.FirstOrDefault();
        }

        private static string GetResLabel(int w, int h)
        {
            if (w == 1280 && h == 720) return " (720P)";
            if (w == 1920 && h == 1080) return " (1080P)";
            if (w == 2560 && h == 1440) return " (2K)";
            if (w == 3840 && h == 2160) return " (4K)";
            return "";
        }

        private void LoadCameras()
        {
            var cameraList = new List<CameraInfo>();
            try
            {
                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                for (int i = 0; i < videoDevices.Count; i++) { cameraList.Add(new CameraInfo { Index = i, Name = $"[{i}] {videoDevices[i].Name}" }); }
            }
            catch { }
            if (cameraList.Count == 0) { cameraList.Add(new CameraInfo { Index = 0, Name = "[0] 未检测到摄像头" }); }
            CameraComboBox.ItemsSource = cameraList;
        }

        private void CameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CameraComboBox.SelectedValue is int idx)
            {
                LoadResolutionOptions(idx, Config.FrameWidth, Config.FrameHeight);
                LoadFpsOptions(idx, Config.Fps);
            }
        }

        private void LoadFpsOptions(int cameraIndex, int currentFps)
        {
            var fpsList = new List<ComboBoxItem>();
            try
            {
                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (cameraIndex >= 0 && cameraIndex < videoDevices.Count)
                {
                    var device = new VideoCaptureDevice(videoDevices[cameraIndex].MonikerString);
                    var fpsValues = device.VideoCapabilities
                        .Select(c => c.AverageFrameRate)
                        .Where(f => f > 0)
                        .Distinct()
                        .OrderBy(f => f)
                        .ToList();
                    foreach (var fps in fpsValues)
                        fpsList.Add(new ComboBoxItem { Content = $"{fps} FPS", Tag = fps });
                }
            }
            catch { }

            if (fpsList.Count == 0)
            {
                foreach (var fps in new[] { 10, 15, 20, 25, 30 })
                    fpsList.Add(new ComboBoxItem { Content = $"{fps} FPS", Tag = fps });
            }

            FpsComboBox.ItemsSource = fpsList;
            // 选中当前配置的 FPS，如果不在列表中则选第一个
            var match = fpsList.FirstOrDefault(i => (int)i.Tag == currentFps);
            FpsComboBox.SelectedItem = match ?? fpsList.FirstOrDefault();
        }

        private void LoadMicrophones()
        {
            var micList = new List<MicInfo>();
            try
            {
                // DirectShow Audio Input Device category
                var audioDevices = new FilterInfoCollection(new Guid("33D9A762-90C8-11D0-BD43-00A0C911CE86"));
                for (int i = 0; i < audioDevices.Count; i++)
                {
                    micList.Add(new MicInfo { Name = audioDevices[i].Name });
                }
            }
            catch { }
            if (micList.Count == 0) { micList.Add(new MicInfo { Name = "未检测到麦克风" }); }
            MicComboBox.ItemsSource = micList;

            // 若当前配置为空，默认选第一个麦克风
            if (string.IsNullOrEmpty(Config.AudioDeviceName) && micList.Count > 0)
            {
                Config.AudioDeviceName = micList[0].Name;
            }
        }

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
            ApplyAutoStart(Config.AutoStartOnBoot);
            this.DialogResult = true; this.Close();
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



