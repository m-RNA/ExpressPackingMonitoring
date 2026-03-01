#nullable disable
using System;
using System.Windows;
using System.Collections.Generic;
using ExpressPackingMonitoring.ViewModels;
using AForge.Video.DirectShow;

namespace ExpressPackingMonitoring
{
    public class CameraInfo { public int Index { get; set; } public string Name { get; set; } }
    public class ResOption { public string Name { get; set; } public int Width { get; set; } public int Height { get; set; } }

    public partial class SettingsWindow : Window
    {
        public AppConfig Config { get; set; }
        public double CurrentDiskUsagePercent { get; set; }
        public string CurrentDiskUsageText { get; set; }

        public SettingsWindow(AppConfig clonedConfig, double diskUsagePercent, string diskUsageText)
        {
            InitializeComponent();
            Config = clonedConfig;

            CurrentDiskUsagePercent = diskUsagePercent;
            CurrentDiskUsageText = diskUsageText;

            this.DataContext = this;

            LoadCameras();
            LoadPresets(clonedConfig);
        }

        private void LoadPresets(AppConfig config)
        {
            var resOptions = new List<ResOption> {
                new ResOption { Name = "720P (1280x720) - 推荐/省空间", Width = 1280, Height = 720 },
                new ResOption { Name = "1080P (1920x1080) - 高清画质", Width = 1920, Height = 1080 },
                new ResOption { Name = "4K (3840x2160) - 极清画质", Width = 3840, Height = 2160 }
            };
            ResComboBox.ItemsSource = resOptions;
            foreach (var item in resOptions)
            {
                if (item.Width == config.FrameWidth && item.Height == config.FrameHeight)
                {
                    ResComboBox.SelectedItem = item; break;
                }
            }
            if (ResComboBox.SelectedItem == null) ResComboBox.SelectedIndex = 0;

            ZoomScaleComboBox.ItemsSource = new List<double> { 1.2, 1.5, 2.0, 2.5, 3.0, 4.0 };
            if (!ZoomScaleComboBox.Items.Contains(config.ZoomScale)) config.ZoomScale = 1.5;
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

        private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "请选择视频保存的文件夹",
                // 指向我的电脑，避免网络驱动器引发卡顿
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
            };

            // 【需求3修复】：加上 this，强行把弹窗绑定在设置界面正上方，绝对不会跑到后台导致假死！
            if (dialog.ShowDialog(this) == true)
            {
                Config.VideoStoragePath = dialog.FolderName;
                PathTextBox.Text = dialog.FolderName;
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (ResComboBox.SelectedItem is ResOption selectedRes) { Config.FrameWidth = selectedRes.Width; Config.FrameHeight = selectedRes.Height; }
            this.DialogResult = true; this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) { this.DialogResult = false; this.Close(); }
    }
}