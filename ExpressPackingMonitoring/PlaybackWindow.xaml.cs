using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExpressPackingMonitoring
{
    public class VideoItem
    {
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Duration { get; set; } = "";
        public string FileSize { get; set; } = "";
        public string StopReason { get; set; } = "";
        public string VideoCodec { get; set; } = "";
        public string VideoEncoder { get; set; } = "";
        public bool IsMissing { get; set; }
        public bool IsDeleted { get; set; }
        public string DeleteReason { get; set; } = "";
        public DateTime? DeletedAt { get; set; }
        public string EncoderDisplay
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(VideoEncoder))
                    return EncodingHelper.GetEncoderLabel(VideoEncoder);
                if (!string.IsNullOrWhiteSpace(VideoCodec))
                    return EncodingHelper.GetCodecLabel(VideoCodec);
                return "";
            }
        }
        public string StatusText
        {
            get
            {
                if (IsDeleted)
                {
                    string reason = string.IsNullOrEmpty(DeleteReason) ? "已删除" : DeleteReason;
                    string time = DeletedAt?.ToString("MM-dd HH:mm") ?? "";
                    return $"已覆盖 ({reason} {time})";
                }
                if (IsMissing) return "⚠ 文件已丢失";
                return "";
            }
        }
        public bool IsUnavailable => IsDeleted || IsMissing;
        public FileInfo? File { get; set; }
    }

    public partial class PlaybackWindow : Window
    {
        private readonly string _folderPath;
        private readonly VideoDatabase? _db;
        private readonly bool _showDeletedVideos;
        private List<VideoItem> _allVideos = new List<VideoItem>();
        private DispatcherTimer _timer;
        private bool _isDragging = false;
        private bool _isPlaying = false;
        private bool _suppressMediaFailed = false;
        private bool _isTranscoding = false;

        public PlaybackWindow(string folderPath, VideoDatabase? db = null, bool showDeletedVideos = true)
        {
            InitializeComponent();
            _folderPath = folderPath;
            _db = db;
            _showDeletedVideos = showDeletedVideos;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += Timer_Tick!;
            DpEndDate.SelectedDate = DateTime.Now;
            DpStartDate.SelectedDate = DateTime.Now.AddDays(-7);
            LoadVideos();
        }
// 【核心优化】：日历改变时，重新扫描本地硬盘对应日期的文件夹
        private void DateFilterChanged(object sender, SelectionChangedEventArgs e) { LoadVideos(); }
        // 搜索框改变时，只在内存中过滤，不读硬盘
        private void TextFilterChanged(object sender, TextChangedEventArgs e) { ApplyFilters(); }
        private void BtnClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; }

        private void LoadVideos()
        {
            _allVideos.Clear();
            DateTime start = DpStartDate.SelectedDate ?? DateTime.Now.AddDays(-7);
            DateTime end = DpEndDate.SelectedDate ?? DateTime.Now;
            if (start > end) { var temp = start; start = end; end = temp; }

            string? keyword = SearchBox?.Text.Trim();

            // 优先从数据库查询（快速索引）
            if (_db != null)
            {
                try
                {
                    var records = _db.QueryVideos(start, end, string.IsNullOrEmpty(keyword) ? null : keyword);
                    foreach (var r in records)
                    {
                        bool deleted = r.IsDeleted;
                        bool missing = !deleted && !File.Exists(r.FilePath);
                        FileInfo? fi = (deleted || missing) ? null : new FileInfo(r.FilePath);
                        _allVideos.Add(new VideoItem
                        {
                            DisplayName = Path.GetFileNameWithoutExtension(r.FileName),
                            FullPath = r.FilePath,
                            OrderId = r.OrderId,
                            Mode = r.Mode,
                            Duration = r.DurationSeconds > 0 ? $"{(int)r.DurationSeconds}s" : "",
                            FileSize = (deleted || missing) ? FormatFileSize(r.FileSizeBytes) : FormatFileSize(fi!.Length),
                            StopReason = r.StopReason,
                            VideoCodec = r.VideoCodec,
                            VideoEncoder = r.VideoEncoder,
                            IsMissing = missing,
                            IsDeleted = deleted,
                            DeleteReason = r.DeleteReason,
                            DeletedAt = r.DeletedAt,
                            File = fi
                        });
                    }
                }
                catch
                {
                    // 数据库查询失败，回退到文件系统
                    LoadVideosFromFileSystem(start, end);
                }
            }
            else
            {
                LoadVideosFromFileSystem(start, end);
            }

            ApplyFilters();
        }

        private void LoadVideosFromFileSystem(DateTime start, DateTime end)
        {
            for (DateTime d = start.Date; d <= end.Date; d = d.AddDays(1))
            {
                string dateFolder = Path.Combine(_folderPath, d.ToString("yyyy-MM-dd"));
                if (Directory.Exists(dateFolder))
                {
                    var files = new DirectoryInfo(dateFolder).GetFiles("*.mp4");
                    foreach (var f in files)
                    {
                        _allVideos.Add(new VideoItem
                        {
                            DisplayName = Path.GetFileNameWithoutExtension(f.Name),
                            FullPath = f.FullName,
                            FileSize = FormatFileSize(f.Length),
                            File = f
                        });
                    }
                }
            }
            _allVideos = _allVideos.OrderByDescending(v => v.File?.CreationTime ?? DateTime.MinValue).ToList();
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes}B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0}KB";
            return $"{bytes / (1024.0 * 1024.0):F1}MB";
        }

        private void ApplyFilters()
        {
            if (_allVideos == null || VideoList == null) return;
            var filtered = _allVideos.AsEnumerable();
            if (!_showDeletedVideos) filtered = filtered.Where(v => !v.IsDeleted && !v.IsMissing);
            string? keyword = SearchBox?.Text.Trim().ToUpper();
            if (!string.IsNullOrEmpty(keyword)) filtered = filtered.Where(v =>
                v.DisplayName.ToUpper().Contains(keyword) ||
                (v.OrderId?.ToUpper().Contains(keyword) ?? false));
            VideoList.ItemsSource = filtered.ToList();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _suppressMediaFailed = true;
            _timer?.Stop();
            MediaPlayer.Stop();
            MediaPlayer.Source = null;
            MediaPlayer.Close();
        }

        private void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VideoList.SelectedItem is VideoItem video)
            {
                if (video.IsDeleted)
                {
                    string reason = string.IsNullOrEmpty(video.DeleteReason) ? "系统清理" : video.DeleteReason;
                    string time = video.DeletedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
                    MessageBox.Show(
                        $"该视频已被覆盖删除，无法播放。\n\n" +
                        $"单号: {video.OrderId}\n" +
                        $"删除原因: {reason}\n" +
                        $"删除时间: {time}\n" +
                        $"原始大小: {video.FileSize}\n" +
                        $"录制时长: {video.Duration}",
                        "视频已删除", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (video.IsMissing)
                {
                    MessageBox.Show(
                        $"视频文件已被外部删除或移动，无法播放。\n\n" +
                        $"单号: {video.OrderId}\n" +
                        $"路径: {video.FullPath}\n" +
                        $"原始大小: {video.FileSize}\n" +
                        $"录制时长: {video.Duration}",
                        "文件丢失", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _timer.Stop();
                _suppressMediaFailed = true;
                MediaPlayer.Stop();
                MediaPlayer.Source = null;
                MediaPlayer.Close();
                _suppressMediaFailed = false;
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

        private async void BtnExportH264_Click(object sender, RoutedEventArgs e)
        {
            if (_isTranscoding)
                return;

            if (VideoList.SelectedItem is not VideoItem video || video.IsUnavailable || string.IsNullOrWhiteSpace(video.FullPath))
            {
                MessageBox.Show("请先选择一个可用视频。", "导出 H.264", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string outputPath = Path.Combine(
                Path.GetDirectoryName(video.FullPath) ?? _folderPath,
                $"{Path.GetFileNameWithoutExtension(video.FullPath)}_H264.mp4");

            bool ok = await ExportToH264Async(video.FullPath, outputPath, playAfterExport: false);
            if (ok)
            {
                MessageBox.Show($"已导出 H.264 视频：\n{outputPath}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadVideos();
            }
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
        private void MediaPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (_suppressMediaFailed || !IsLoaded)
                return;

            _timer.Stop();
            UpdatePlayState(false);
            _suppressMediaFailed = true;
            MediaPlayer.Stop();
            MediaPlayer.Source = null;
            if (VideoList.SelectedItem is VideoItem video && !string.IsNullOrEmpty(video.FullPath))
            {
                var result = MessageBox.Show(
                    $"内置播放器不支持此视频格式（通常是 H.265/AV1 或系统缺少解码扩展）。\n\n" +
                    $"选择“是”会先转码为 H.264 再在内置播放器中播放。\n" +
                    $"选择“否”则使用系统默认播放器打开原文件。",
                    "播放错误", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    _ = ExportToH264Async(video.FullPath, GetTranscodedPlaybackPath(video.FullPath), playAfterExport: true);
                }
                else if (result == MessageBoxResult.No)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(video.FullPath) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"无法打开外部播放器: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            else
            {
                MessageBox.Show($"视频播放失败：{e.ErrorException?.Message}", "播放错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private string GetTranscodedPlaybackPath(string sourcePath)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ExpressPackingMonitoring", "PlaybackCache");
            Directory.CreateDirectory(tempDir);
            return Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(sourcePath)}_play_h264.mp4");
        }

        private async Task<bool> ExportToH264Async(string sourcePath, string outputPath, bool playAfterExport)
        {
            string ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                MessageBox.Show("未找到 FFmpeg，无法执行转码。", "转码失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                _isTranscoding = true;
                SetExportUiState(false, playAfterExport ? "正在转码播放版本..." : "正在导出 H.264...");

                string args = $"-y -i \"{sourcePath}\" -c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p -movflags +faststart -c:a aac -b:a 128k \"{outputPath}\"";
                var result = await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };

                    using var process = Process.Start(psi);
                    if (process == null)
                        return (false, "FFmpeg 进程启动失败");

                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    bool ok = process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
                    return (ok, stderr);
                });

                if (!result.Item1)
                {
                    MessageBox.Show($"转码失败：\n{result.Item2}", "转码失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (playAfterExport)
                {
                    _suppressMediaFailed = true;
                    MediaPlayer.Stop();
                    MediaPlayer.Source = null;
                    MediaPlayer.Close();
                    _suppressMediaFailed = false;
                    MediaPlayer.Source = new Uri(outputPath);
                    MediaPlayer.Play();
                    _timer.Start();
                    UpdatePlayState(true);
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"转码失败：{ex.Message}", "转码失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                _isTranscoding = false;
                SetExportUiState(true, "00:00:00 / 00:00:00");
            }
        }

        private void SetExportUiState(bool enabled, string statusText)
        {
            if (BtnTogglePlay != null)
                BtnTogglePlay.IsEnabled = enabled;
            if (BtnExportH264 != null)
                BtnExportH264.IsEnabled = enabled;
            if (TimelineSlider != null)
                TimelineSlider.IsEnabled = enabled;
            if (TimeLabel != null)
                TimeLabel.Text = statusText;
        }

        private string FindFFmpeg()
        {
            string appDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            return File.Exists(appDir) ? appDir : string.Empty;
        }
    }
}