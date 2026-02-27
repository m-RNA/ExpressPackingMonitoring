#nullable disable
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
        public SettingsWindow(AppConfig clonedConfig)
        {
            InitializeComponent();
            this.DataContext = clonedConfig;
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

            FpsComboBox.ItemsSource = new List<int> { 12, 15, 24, 25, 30, 60 };

            // 【新增】：放大倍数预设
            ZoomScaleComboBox.ItemsSource = new List<double> { 1.2, 1.5, 2.0, 2.5, 3.0, 4.0 };
            if (!ZoomScaleComboBox.Items.Contains(config.ZoomScale))
                config.ZoomScale = 2.0;
        }

        private void LoadCameras()
        {
            var cameraList = new List<CameraInfo>();
            try
            {
                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                for (int i = 0; i < videoDevices.Count; i++)
                {
                    cameraList.Add(new CameraInfo { Index = i, Name = $"[{i}] {videoDevices[i].Name}" });
                }
            }
            catch { }

            if (cameraList.Count == 0)
            {
                cameraList.Add(new CameraInfo { Index = 0, Name = "[0] 未检测到摄像头" });
            }
            CameraComboBox.ItemsSource = cameraList;
        }

        private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "请选择视频保存的文件夹"
            };
            if (dialog.ShowDialog() == true)
            {
                var config = (AppConfig)this.DataContext;
                config.VideoStoragePath = dialog.FolderName;
                PathTextBox.Text = dialog.FolderName;
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (ResComboBox.SelectedItem is ResOption selectedRes)
            {
                var config = (AppConfig)this.DataContext;
                config.FrameWidth = selectedRes.Width;
                config.FrameHeight = selectedRes.Height;
            }
            this.DialogResult = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}