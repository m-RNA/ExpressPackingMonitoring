using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ExpressPackingMonitoring
{
    public partial class PlaybackWindow : Window
    {
        private readonly string _folderPath;
        private List<FileInfo> _allVideos = new List<FileInfo>();

        // 用于控制进度条的计时器
        private DispatcherTimer _timer;
        private bool _isDragging = false; // 是否正在手动拖动进度条

        public PlaybackWindow(string folderPath)
        {
            InitializeComponent();
            _folderPath = folderPath;

            // 初始化计时器
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += Timer_Tick;

            // 默认日期范围：最近 7 天
            DpEndDate.SelectedDate = DateTime.Now;
            DpStartDate.SelectedDate = DateTime.Now.AddDays(-7);

            LoadVideos();
        }

        private void LoadVideos()
        {
            if (Directory.Exists(_folderPath))
            {
                var dir = new DirectoryInfo(_folderPath);
                _allVideos = dir.GetFiles("*.mp4").OrderByDescending(f => f.CreationTime).ToList();
                ApplyFilters(); // 加载完立即应用筛选
            }
        }

        // ==================== 多维搜索与过滤算法 ====================
        private void FilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();
        private void FilterChanged(object sender, TextChangedEventArgs e) => ApplyFilters();

        private void ApplyFilters()
        {
            if (_allVideos == null || VideoList == null) return;

            var filtered = _allVideos.AsEnumerable();

            // 1. 日期过滤
            if (DpStartDate.SelectedDate.HasValue)
                filtered = filtered.Where(f => f.CreationTime.Date >= DpStartDate.SelectedDate.Value.Date);
            if (DpEndDate.SelectedDate.HasValue)
                filtered = filtered.Where(f => f.CreationTime.Date <= DpEndDate.SelectedDate.Value.Date);

            // 2. 快递公司过滤 (根据单号前缀推断)
            if (ExpressComboBox != null && ExpressComboBox.SelectedIndex > 0)
            {
                string carrier = ((ComboBoxItem)ExpressComboBox.SelectedItem).Content.ToString();
                string prefix = "";
                if (carrier.Contains("SF")) prefix = "_SF";
                else if (carrier.Contains("JD")) prefix = "_JD";
                else if (carrier.Contains("EM")) prefix = "_E"; // EX 或 EM
                else if (carrier.Contains("YT")) prefix = "_YT";
                else if (carrier.Contains("ZT")) prefix = "_ZT";
                else if (carrier.Contains("ST")) prefix = "_ST";
                else if (carrier.Contains("YD")) prefix = "_YD";
                else if (carrier.Contains("JT")) prefix = "_JT";

                if (!string.IsNullOrEmpty(prefix))
                    filtered = filtered.Where(f => f.Name.ToUpper().Contains(prefix));
            }

            // 3. 关键字过滤
            string keyword = SearchBox?.Text.Trim().ToUpper();
            if (!string.IsNullOrEmpty(keyword))
            {
                filtered = filtered.Where(f => f.Name.ToUpper().Contains(keyword));
            }

            VideoList.ItemsSource = filtered.ToList();
        }

        // ==================== 播放器与进度条控制 ====================
        private void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VideoList.SelectedItem is FileInfo file)
            {
                MediaPlayer.Source = new Uri(file.FullName);
                MediaPlayer.Play();
                _timer.Start();
            }
        }

        // 视频打开时，初始化进度条最大值
        private void MediaPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (MediaPlayer.NaturalDuration.HasTimeSpan)
            {
                TimelineSlider.Maximum = MediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            }
        }

        // 时钟跳动：更新进度条和文本
        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!_isDragging && MediaPlayer.Source != null && MediaPlayer.NaturalDuration.HasTimeSpan)
            {
                TimelineSlider.Value = MediaPlayer.Position.TotalSeconds;
                TimeLabel.Text = $"{MediaPlayer.Position:hh\\:mm\\:ss} / {MediaPlayer.NaturalDuration.TimeSpan:hh\\:mm\\:ss}";
            }
        }

        // 拖动开始
        private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDragging = true;
            MediaPlayer.Pause();
        }

        // 拖动结束
        private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _isDragging = false;
            MediaPlayer.Position = TimeSpan.FromSeconds(TimelineSlider.Value);
            MediaPlayer.Play();
        }

        // 鼠标点击某处直接跳跃
        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isDragging && Math.Abs(e.NewValue - e.OldValue) > 1)
            {
                MediaPlayer.Position = TimeSpan.FromSeconds(e.NewValue);
            }
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e) { MediaPlayer.Play(); _timer.Start(); }
        private void BtnPause_Click(object sender, RoutedEventArgs e) { MediaPlayer.Pause(); _timer.Stop(); }
    }
}