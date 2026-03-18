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
using LibVLCSharp.Shared;

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
        public FileInfo? File { get; set; }

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

                return IsMissing ? "⚠ 文件已丢失" : "";
            }
        }

        public bool IsUnavailable => IsDeleted || IsMissing;
    }

    public partial class PlaybackWindow : Window
    {
        private readonly string _folderPath;
        private readonly VideoDatabase? _db;
        private readonly bool _showDeletedVideos;
        private readonly DispatcherTimer _timer;
        private readonly string[] _videoExtensions = [".mkv"];
        private LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;
        private List<VideoItem> _allVideos = new();
        private bool _isDragging;
        private bool _isPlaying;
        private bool _isTranscoding;
        private bool _isLoadingVideos;
        private bool _playerInitializationFailed;
        private long _currentMediaLengthMs;
        private Process? _currentExportProcess;
        private readonly SemaphoreSlim _playerSemaphore = new SemaphoreSlim(1, 1);

        public PlaybackWindow(string folderPath, VideoDatabase? db = null, bool showDeletedVideos = true)
        {
            InitializeComponent();
            _folderPath = folderPath;
            _db = db;
            _showDeletedVideos = showDeletedVideos;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += Timer_Tick;

            DpEndDate.SelectedDate = DateTime.Now;
            DpStartDate.SelectedDate = DateTime.Now.AddDays(-7);
            BtnTogglePlay.IsEnabled = false;
            TimelineSlider.IsEnabled = false;
            TimeLabel.Text = "正在加载列表...";
            Loaded += PlaybackWindow_Loaded;
            UpdateExportButtonState();
        }

        private async void PlaybackWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadVideosAsync();
        }

        private async void DateFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            await LoadVideosAsync();
        }

        private void TextFilterChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
        }

        private async Task LoadVideosAsync()
        {
            if (_isLoadingVideos)
                return;

            DateTime start = DpStartDate.SelectedDate ?? DateTime.Now.AddDays(-7);
            DateTime end = DpEndDate.SelectedDate ?? DateTime.Now;
            if (start > end)
                (start, end) = (end, start);
            string? keyword = SearchBox?.Text.Trim();

            _isLoadingVideos = true;
            SetLoadingState(true, "正在加载列表...");
            try
            {
                _allVideos = await Task.Run(() => BuildVideoList(start, end, keyword));
                ApplyFilters();
            }
            catch (Exception ex)
            {
                _allVideos = new List<VideoItem>();
                ApplyFilters();
                MessageBox.Show($"加载回放列表失败：{ex.Message}", "回放错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _isLoadingVideos = false;
                SetLoadingState(false, "00:00:00 / 00:00:00");
            }
        }

        private List<VideoItem> BuildVideoList(DateTime start, DateTime end, string? keyword)
        {
            var videos = new List<VideoItem>();
            if (_db != null)
            {
                try
                {
                    var records = _db.QueryVideos(start, end, string.IsNullOrEmpty(keyword) ? null : keyword);
                    foreach (var record in records)
                    {
                        bool deleted = record.IsDeleted;
                        bool missing = !deleted && !File.Exists(record.FilePath);
                        FileInfo? info = (deleted || missing) ? null : new FileInfo(record.FilePath);
                        videos.Add(new VideoItem
                        {
                            DisplayName = Path.GetFileNameWithoutExtension(record.FileName),
                            FullPath = record.FilePath,
                            OrderId = record.OrderId,
                            Mode = record.Mode,
                            Duration = record.DurationSeconds > 0 ? $"{(int)record.DurationSeconds}s" : "",
                            FileSize = (deleted || missing) ? FormatFileSize(record.FileSizeBytes) : FormatFileSize(info!.Length),
                            StopReason = record.StopReason,
                            VideoCodec = record.VideoCodec,
                            VideoEncoder = record.VideoEncoder,
                            IsMissing = missing,
                            IsDeleted = deleted,
                            DeleteReason = record.DeleteReason,
                            DeletedAt = record.DeletedAt,
                            File = info
                        });
                    }
                }
                catch
                {
                    LoadVideosFromFileSystem(videos, start, end);
                }
            }
            else
            {
                LoadVideosFromFileSystem(videos, start, end);
            }

            return videos;
        }

        private void LoadVideosFromFileSystem(List<VideoItem> videos, DateTime start, DateTime end)
        {
            for (DateTime day = start.Date; day <= end.Date; day = day.AddDays(1))
            {
                string dateFolder = Path.Combine(_folderPath, day.ToString("yyyy-MM-dd"));
                if (!Directory.Exists(dateFolder))
                    continue;

                foreach (var file in EnumerateVideoFiles(dateFolder))
                {
                    videos.Add(new VideoItem
                    {
                        DisplayName = Path.GetFileNameWithoutExtension(file.Name),
                        FullPath = file.FullName,
                        FileSize = FormatFileSize(file.Length),
                        File = file
                    });
                }
            }

            videos.Sort((a, b) => DateTime.Compare(b.File?.CreationTime ?? DateTime.MinValue, a.File?.CreationTime ?? DateTime.MinValue));
        }

        private IEnumerable<FileInfo> EnumerateVideoFiles(string folderPath)
        {
            var dir = new DirectoryInfo(folderPath);
            foreach (string extension in _videoExtensions)
            {
                foreach (var file in dir.GetFiles($"*{extension}"))
                    yield return file;
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes}B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0}KB";
            return $"{bytes / (1024.0 * 1024.0):F1}MB";
        }

        private void ApplyFilters()
        {
            var filtered = _allVideos.AsEnumerable();
            if (!_showDeletedVideos)
                filtered = filtered.Where(v => !v.IsDeleted && !v.IsMissing);

            string? keyword = SearchBox?.Text.Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(keyword))
            {
                filtered = filtered.Where(v =>
                    v.DisplayName.ToUpperInvariant().Contains(keyword) ||
                    (v.OrderId?.ToUpperInvariant().Contains(keyword) ?? false));
            }

            VideoList.ItemsSource = filtered.ToList();
            UpdateExportButtonState();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // 1. 停止计时器
            _timer?.Stop();

            // 2. 如果正在转码，必须杀掉 FFmpeg 进程，否则会内存溢出
            if (_isTranscoding)
            {
                try
                {
                    if (_currentExportProcess != null && !_currentExportProcess.HasExited)
                    {
                        _currentExportProcess.Kill();
                    }
                    else
                    {
                        // 兜底：找到当前正在运行的 ffmpeg 并强制结束
                        var processes = Process.GetProcessesByName("ffmpeg");
                        foreach (var p in processes)
                        {
                            p.Kill();
                        }
                    }
                }
                catch { }
            }

            // 3. 彻底释放 LibVLC 资源（注意顺序）
            if (_mediaPlayer != null)
            {
                try
                {
                    // 重要：先解除事件订阅，防止销毁时触发回调导致死锁
                    _mediaPlayer.LengthChanged -= MediaPlayer_LengthChanged;
                    _mediaPlayer.TimeChanged -= MediaPlayer_TimeChanged;
                    _mediaPlayer.EndReached -= MediaPlayer_EndReached;
                    _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;

                    if (_mediaPlayer.IsPlaying)
                    {
                        _mediaPlayer.Stop();
                    }

                    // 断开视图连接
                    PlayerView.MediaPlayer = null;

                    _mediaPlayer.Dispose();
                    _mediaPlayer = null;
                }
                catch { }
            }

            if (_libVLC != null)
            {
                try
                {
                    _libVLC.Dispose();
                    _libVLC = null;
                }
                catch { }
            }
        }

        private async void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VideoList.SelectedItem is not VideoItem video)
            {
                UpdateExportButtonState();
                return;
            }

            // 增加 100ms 的防抖，防止极速连点
            await Task.Delay(100);
            if (VideoList.SelectedItem != video) return; // 如果选中的已经变了，就不执行了

            if (video.IsDeleted)
            {
                string reason = string.IsNullOrEmpty(video.DeleteReason) ? "系统清理" : video.DeleteReason;
                string time = video.DeletedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
                MessageBox.Show(
                    $"该视频已被覆盖删除，无法播放。\n\n单号: {video.OrderId}\n删除原因: {reason}\n删除时间: {time}\n原始大小: {video.FileSize}\n录制时长: {video.Duration}",
                    "视频已删除", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateExportButtonState(video);
                return;
            }

            if (video.IsMissing)
            {
                MessageBox.Show(
                    $"视频文件已被外部删除或移动，无法播放。\n\n单号: {video.OrderId}\n路径: {video.FullPath}\n原始大小: {video.FileSize}\n录制时长: {video.Duration}",
                    "文件丢失", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateExportButtonState(video);
                return;
            }

            PlaySelectedVideo(video);
            UpdateExportButtonState(video);
        }

        private async void PlaySelectedVideo(VideoItem video)
        {
            // 1. 尝试获取信号量，如果已经在切换中，则直接返回，防止疯狂点击导致的排队
            if (!await _playerSemaphore.WaitAsync(0)) return;

            try
            {
                if (!EnsurePlayerReady())
                    return;

                // UI 状态立即重置
                _timer.Stop();
                _currentMediaLengthMs = 0;
                TimelineSlider.Maximum = 0;
                TimelineSlider.Value = 0;
                TimeLabel.Text = "正在切换视频...";

                // 2. 在后台线程执行阻塞的 Stop 操作
                await Task.Run(() =>
                {
                    _mediaPlayer?.Stop();
                });

                // 3. 准备新媒体
                using var media = new Media(_libVLC!, new Uri(video.FullPath));

                // 增加一些优化参数，减少内存压力
                media.AddOption(":no-audio"); // 如果回放不需要声音可以加上，减轻解码负担
                media.AddOption(":file-caching=300"); // 减小缓存

                if (!_mediaPlayer!.Play(media))
                    throw new InvalidOperationException("播放器未能启动该文件。");

                _timer.Start();
                UpdatePlayState(true);
            }
            catch (Exception ex)
            {
                UpdatePlayState(false);
                MessageBox.Show($"视频播放失败：{ex.Message}", "播放错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                // 4. 释放信号量，允许下一次切换
                _playerSemaphore.Release();
            }
        }

        private void BtnTogglePlay_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer?.Media == null)
                return;

            if (_isPlaying)
            {
                _mediaPlayer.Pause();
                _timer.Stop();
                UpdatePlayState(false);
            }
            else
            {
                _mediaPlayer.SetPause(false);
                _timer.Start();
                UpdatePlayState(true);
            }
        }

        private async void BtnExportMp4_Click(object sender, RoutedEventArgs e)
        {
            if (_isTranscoding)
                return;

            if (VideoList.SelectedItem is not VideoItem video || video.IsUnavailable || string.IsNullOrWhiteSpace(video.FullPath))
            {
                MessageBox.Show("请先选择一个可用视频。", "导出 MP4", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string outputPath = Path.Combine(
                Path.GetDirectoryName(video.FullPath) ?? _folderPath,
                $"{Path.GetFileNameWithoutExtension(video.FullPath)}.mp4");

            bool ok = await ExportToMp4Async(video.FullPath, outputPath);
            if (ok)
            {
                // 导出成功后，在资源管理器中选中该文件
                try
                {
                    string argument = $"/select,\"{outputPath}\"";
                    Process.Start("explorer.exe", argument);
                }
                catch { }

                MessageBox.Show($"已导出 MP4：\n{outputPath}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadVideosAsync();
            }
        }

        private async Task<bool> ExportToMp4Async(string sourcePath, string outputPath)
        {
            string ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                MessageBox.Show("未找到 FFmpeg，无法执行转码。", "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                _isTranscoding = true;
                SetExportUiState(false, "正在转换容器...");
                // 使用 -vcodec copy -acodec copy 实现无损快速转换容器
                string args = $"-y -i \"{sourcePath}\" -vcodec copy -acodec copy \"{outputPath}\"";

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

                    _currentExportProcess = Process.Start(psi);
                    if (_currentExportProcess == null)
                        return (false, "FFmpeg 进程启动失败");

                    string stderr = _currentExportProcess.StandardError.ReadToEnd();
                    _currentExportProcess.WaitForExit();
                    bool success = _currentExportProcess.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
                    return (success, stderr);
                });

                if (!result.Item1)
                {
                    MessageBox.Show($"导出失败：\n{result.Item2}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                _currentExportProcess = null;
                _isTranscoding = false;
                SetExportUiState(true, "00:00:00 / 00:00:00");
                UpdateExportButtonState();
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

        private void MediaPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        {
            _currentMediaLengthMs = e.Length;
            Dispatcher.Invoke(() => TimelineSlider.Maximum = Math.Max(0, e.Length / 1000.0));
        }

        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            if (_isDragging || _mediaPlayer == null)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (!this.IsLoaded) return;
                TimelineSlider.Value = Math.Max(0, e.Time / 1000.0);
                TimeLabel.Text = $"{TimeSpan.FromMilliseconds(e.Time):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(_currentMediaLengthMs):hh\\:mm\\:ss}";
            });
        }

        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _timer.Stop();
                UpdatePlayState(false);
                TimelineSlider.Value = 0;
            });
        }

        private void MediaPlayer_EncounteredError(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _timer.Stop();
                UpdatePlayState(false);
                MessageBox.Show("播放器解码失败，请确认视频文件完整。", "播放错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_isDragging || _mediaPlayer?.Media == null)
                return;

            TimeLabel.Text = $"{TimeSpan.FromMilliseconds(_mediaPlayer.Time):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(_currentMediaLengthMs):hh\\:mm\\:ss}";
        }

        private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDragging = true;
            if (_mediaPlayer?.IsPlaying == true)
                _mediaPlayer.Pause();
        }

        private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_mediaPlayer == null)
                return;

            _isDragging = false;
            _mediaPlayer.Time = (long)(TimelineSlider.Value * 1000);
            _mediaPlayer.SetPause(false);
            _timer.Start();
            UpdatePlayState(true);
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDragging)
            {
                TimeLabel.Text = $"{TimeSpan.FromSeconds(e.NewValue):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(_currentMediaLengthMs):hh\\:mm\\:ss}";
            }
        }

        private void SetExportUiState(bool enabled, string statusText)
        {
            BtnTogglePlay.IsEnabled = enabled && _mediaPlayer != null;
            BtnExportMp4.IsEnabled = enabled && CanExportCurrentSelection();
            TimelineSlider.IsEnabled = enabled && _mediaPlayer != null;
            TimeLabel.Text = statusText;
        }

        private bool CanExportCurrentSelection()
        {
            if (VideoList.SelectedItem is not VideoItem video) return false;
            if (video.IsUnavailable) return false;

            // 即使是 h264，也允许导出（转码/复制容器），这样最保险
            return true;
        }

        private void UpdateExportButtonState(VideoItem? video = null)
        {
            var current = video ?? VideoList.SelectedItem as VideoItem;
            BtnExportMp4.IsEnabled = !_isTranscoding
                && current != null
                && !current.IsUnavailable;
        }

        private string FindFFmpeg()
        {
            string appDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            return File.Exists(appDir) ? appDir : string.Empty;
        }

        private bool EnsurePlayerReady()
        {
            if (_playerInitializationFailed)
                return false;

            if (_mediaPlayer != null)
                return true;

            try
            {
                Core.Initialize();
                _libVLC = new LibVLC("--avcodec-hw=any");
                _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
                _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
                _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
                _mediaPlayer.EndReached += MediaPlayer_EndReached;
                _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
                PlayerView.MediaPlayer = _mediaPlayer;
                BtnTogglePlay.IsEnabled = true;
                TimelineSlider.IsEnabled = true;
                return true;
            }
            catch (Exception ex)
            {
                _playerInitializationFailed = true;
                MessageBox.Show($"播放器初始化失败：{ex.Message}\n\n回放列表仍可查看，但当前机器暂时无法内置播放。", "回放错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private void SetLoadingState(bool loading, string statusText)
        {
            VideoList.IsEnabled = !loading;
            SearchBox.IsEnabled = !loading;
            DpStartDate.IsEnabled = !loading;
            DpEndDate.IsEnabled = !loading;
            TimeLabel.Text = statusText;
        }
    }
}
