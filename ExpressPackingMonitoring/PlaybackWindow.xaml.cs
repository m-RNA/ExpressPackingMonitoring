using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExpressPackingMonitoring
{
    public class VideoItem
    {
        public string DisplayName { get; set; }
        public string FullPath { get; set; }
        public FileInfo File { get; set; }
    }

    public partial class PlaybackWindow : Window
    {
        private readonly string _folderPath;
        private List<VideoItem> _allVideos = new List<VideoItem>();
        private DispatcherTimer _timer;
        private bool _isDragging = false;
        private bool _isPlaying = false;

        public PlaybackWindow(string folderPath)
        {
            InitializeComponent();
            _folderPath = folderPath;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += Timer_Tick;
            DpEndDate.SelectedDate = DateTime.Now;
            DpStartDate.SelectedDate = DateTime.Now.AddDays(-7);
            LoadVideos();
        }
// 【核心优化】：日历改变时，重新扫描本地硬盘对应日期的文件夹
        private void DateFilterChanged(object sender, SelectionChangedEventArgs e) { LoadVideos(); }
        // 搜索框改变时，只在内存中过滤，不读硬盘
        private void TextFilterChanged(object sender, TextChangedEventArgs e) { ApplyFilters(); }

        private void LoadVideos()
        {
            _allVideos.Clear();
            DateTime start = DpStartDate.SelectedDate ?? DateTime.Now.AddDays(-7);
            DateTime end = DpEndDate.SelectedDate ?? DateTime.Now;
            if (start > end) { var temp = start; start = end; end = temp; }

            // 【亿级数据检索优化】：不再全盘扫！只精确组合选中的日期文件夹去读取！
            for (DateTime d = start.Date; d <= end.Date; d = d.AddDays(1))
            {
                string dateFolder = Path.Combine(_folderPath, d.ToString("yyyy-MM-dd"));
                if (Directory.Exists(dateFolder))
                {
                    // 只扫当前这一层的 mp4
                    var files = new DirectoryInfo(dateFolder).GetFiles("*.mp4");
                    foreach (var f in files)
                    {
                        _allVideos.Add(new VideoItem { DisplayName = Path.GetFileNameWithoutExtension(f.Name), FullPath = f.FullName, File = f });
                    }
                }
            }
            _allVideos = _allVideos.OrderByDescending(v => v.File.CreationTime).ToList();
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            if (_allVideos == null || VideoList == null) return;
            var filtered = _allVideos.AsEnumerable();
            string keyword = SearchBox?.Text.Trim().ToUpper();
            if (!string.IsNullOrEmpty(keyword)) filtered = filtered.Where(v => v.File.Name.ToUpper().Contains(keyword));
            VideoList.ItemsSource = filtered.ToList();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _timer?.Stop();
            MediaPlayer.Stop();
            MediaPlayer.Close();
        }

        private void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VideoList.SelectedItem is VideoItem video)
            {
                MediaPlayer.Source = new Uri(video.FullPath);
                MediaPlayer.Play();
                _timer.Start();
                UpdatePlayState(true);
            }
        }

        private void BtnTogglePlay_Click(object sender, RoutedEventArgs e)
        {
            if (MediaPlayer.Source == null) return;
            if (_isPlaying) { MediaPlayer.Pause(); _timer.Stop(); UpdatePlayState(false); }
            else { MediaPlayer.Play(); _timer.Start(); UpdatePlayState(true); }
        }

        private void UpdatePlayState(bool isPlaying)
        {
            _isPlaying = isPlaying;
            if (isPlaying)
            {
                BtnTogglePlay.Content = "⏸";
                BtnTogglePlay.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFC107"));
                BtnTogglePlay.Foreground = Brushes.Black;
            }
            else
            {
                BtnTogglePlay.Content = "▶";
                BtnTogglePlay.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
                BtnTogglePlay.Foreground = Brushes.White;
            }
        }

        private void MediaPlayer_MediaOpened(object sender, RoutedEventArgs e) { if (MediaPlayer.NaturalDuration.HasTimeSpan) TimelineSlider.Maximum = MediaPlayer.NaturalDuration.TimeSpan.TotalSeconds; }
        private void Timer_Tick(object sender, EventArgs e) { if (!_isDragging && MediaPlayer.Source != null && MediaPlayer.NaturalDuration.HasTimeSpan) { TimelineSlider.Value = MediaPlayer.Position.TotalSeconds; TimeLabel.Text = $"{MediaPlayer.Position:hh\\:mm\\:ss} / {MediaPlayer.NaturalDuration.TimeSpan:hh\\:mm\\:ss}"; } }
        private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e) { _isDragging = true; MediaPlayer.Pause(); }
        private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e) { _isDragging = false; MediaPlayer.Position = TimeSpan.FromSeconds(TimelineSlider.Value); MediaPlayer.Play(); UpdatePlayState(true); }
        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_isDragging && Math.Abs(e.NewValue - e.OldValue) > 1) { MediaPlayer.Position = TimeSpan.FromSeconds(e.NewValue); } }
    }
}