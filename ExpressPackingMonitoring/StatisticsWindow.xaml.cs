using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ExpressPackingMonitoring
{
    public class ChartItem
    {
        public string DateLabel { get; set; } = "";
        public string DateSub { get; set; } = "";
        public string DateFull { get; set; } = "";
        public int Pieces { get; set; }
        public double BarHeight { get; set; }
        public string SizeText { get; set; } = "";
        public string TotalTime { get; set; } = "";
        public Visibility LabelVisibility { get; set; } = Visibility.Visible;
    }

    public partial class StatisticsWindow : Window, INotifyPropertyChanged
    {
        private VideoDatabase _db;
        private bool _isInternalUpdating = false; // 防止日期切换时触发多次刷新

        public ObservableCollection<ChartItem> ChartData { get; } = new();
        public ObservableCollection<string> YAxisLabels { get; } = new();

        private string _summaryPieces = "0 件";
        public string SummaryPieces { get => _summaryPieces; set { _summaryPieces = value; OnPropertyChanged(nameof(SummaryPieces)); } }

        private string _summarySize = "0 MB";
        public string SummarySize { get => _summarySize; set { _summarySize = value; OnPropertyChanged(nameof(SummarySize)); } }

        private string _summaryDuration = "0h 0m";
        public string SummaryDuration { get => _summaryDuration; set { _summaryDuration = value; OnPropertyChanged(nameof(SummaryDuration)); } }

        private string _summaryAvgTime = "00:00";
        public string SummaryAvgTime { get => _summaryAvgTime; set { _summaryAvgTime = value; OnPropertyChanged(nameof(SummaryAvgTime)); } }

        public StatisticsWindow(VideoDatabase db)
        {
            InitializeComponent();
            _db = db;
            this.DataContext = this;

            this.Loaded += (s, e) => {
                // 初始化默认日期：本周
                ApplyPreset("Last7"); 
                RefreshData();
            };
        }

        private void RefreshData()
        {
            if (_db == null || !this.IsLoaded) return;

            DateTime start = PickerStart.SelectedDate ?? DateTime.Now.AddDays(-6);
            DateTime end = PickerEnd.SelectedDate ?? DateTime.Now;
            string groupMode = (GroupCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "day";

            var history = _db.GetAggregatedStats(start, end, groupMode);
            
            ChartData.Clear();
            YAxisLabels.Clear();

            if (history == null || history.Count == 0)
            {
                ResetSummary();
                return;
            }

            // 1. 计算最大值用于 Y 轴缩放
            double maxVal = 0;
            foreach (var h in history)
            {
                double val = 0;
                if (ModePieces.IsChecked == true) val = h.TotalPieces;
                else if (ModeDuration.IsChecked == true) val = h.TotalDurationSec;
                else val = h.TotalBytes / 1024.0 / 1024.0; // MB
                if (val > maxVal) maxVal = val;
            }
            if (maxVal < 1) maxVal = 1;

            // 2. 生成 Y 轴刻度
            for (int i = 5; i >= 0; i--)
            {
                double tickVal = (maxVal / 5.0) * i;
                if (ModeDuration.IsChecked == true) YAxisLabels.Add(TimeSpan.FromSeconds(tickVal).ToString(@"hh\:mm"));
                else if (ModeSize.IsChecked == true) YAxisLabels.Add($"{tickVal:F0}M");
                else YAxisLabels.Add(tickVal.ToString("F0"));
            }

            // 3. 生成 X 轴数据
            int step = history.Count > 12 ? history.Count / 6 : 1;
            int totalPieces = 0;
            long totalBytes = 0;
            double totalSec = 0;

            for (int i = 0; i < history.Count; i++)
            {
                var h = history[i];
                double currentVal = ModePieces.IsChecked == true ? h.TotalPieces :
                                    ModeDuration.IsChecked == true ? h.TotalDurationSec :
                                    h.TotalBytes / 1024.0 / 1024.0;

                totalPieces += h.TotalPieces;
                totalBytes += h.TotalBytes;
                totalSec += h.TotalDurationSec;

                // 【修复核心】：处理非日期格式的字符串 (W11, 2024-03等)
                string subLabel = "";
                if (groupMode == "day")
                {
                    if (DateTime.TryParse(h.Date, out DateTime dt))
                        subLabel = GetChineseDayOfWeek(dt);
                }

                ChartData.Add(new ChartItem
                {
                    DateFull = h.Date,
                    DateLabel = h.Date.Length > 10 ? h.Date : h.Date, 
                    DateSub = subLabel,
                    Pieces = h.TotalPieces,
                    TotalTime = TimeSpan.FromSeconds(h.TotalDurationSec).ToString(@"hh\:mm\:ss"),
                    SizeText = FormatSize(h.TotalBytes),
                    BarHeight = (currentVal / maxVal) * 320.0, // 稍微调高
                    LabelVisibility = (i % step == 0) ? Visibility.Visible : Visibility.Hidden
                });
            }

            // 4. 更新汇总
            SummaryPieces = $"{totalPieces} 件";
            SummarySize = FormatSize(totalBytes);
            SummaryDuration = $"{(int)totalSec / 3600}h {((int)totalSec % 3600) / 60}m";
            SummaryAvgTime = totalPieces > 0 ? TimeSpan.FromSeconds(totalSec / totalPieces).ToString(@"mm\:ss") : "00:00";
        }

        private void ApplyPreset(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return;

            _isInternalUpdating = true;
            DateTime now = DateTime.Now;
            switch (tag)
            {
                case "Week": // 本周 (从周一开始)
                    int diff = (7 + (now.DayOfWeek - DayOfWeek.Monday)) % 7;
                    PickerStart.SelectedDate = now.AddDays(-1 * diff).Date;
                    PickerEnd.SelectedDate = now.Date;
                    break;
                case "Month": // 本月
                    PickerStart.SelectedDate = new DateTime(now.Year, now.Month, 1);
                    PickerEnd.SelectedDate = now.Date;
                    break;
                case "Last7":
                    PickerStart.SelectedDate = now.AddDays(-6);
                    PickerEnd.SelectedDate = now.Date;
                    break;
                case "Last30":
                    PickerStart.SelectedDate = now.AddDays(-29);
                    PickerEnd.SelectedDate = now.Date;
                    break;
                case "All":
                    PickerStart.SelectedDate = now.AddYears(-2);
                    PickerEnd.SelectedDate = now.Date;
                    break;
            }
            _isInternalUpdating = false;
        }

        private void RangePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PresetCombo.SelectedItem is ComboBoxItem item)
            {
                string tag = item.Tag?.ToString() ?? string.Empty;
                ApplyPreset(tag);
                RefreshData();
            }
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            if (!_isInternalUpdating) RefreshData();
        }

        private void ResetSummary()
        {
            SummaryPieces = "0 件"; SummarySize = "0 MB"; 
            SummaryDuration = "0h 0m"; SummaryAvgTime = "00:00";
        }

        private string GetChineseDayOfWeek(DateTime dt) => dt.DayOfWeek switch {
            DayOfWeek.Sunday => "周日", DayOfWeek.Monday => "周一", DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三", DayOfWeek.Thursday => "周四", DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六", _ => ""
        };

        private string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
            return $"{bytes / 1024.0 / 1024:F1} MB";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}