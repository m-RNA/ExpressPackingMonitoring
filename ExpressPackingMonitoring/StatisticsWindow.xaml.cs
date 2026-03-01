using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ExpressPackingMonitoring
{
    public class ChartItem
    {
        public string DateLabel { get; set; } = "";
        public string DateSubLabel { get; set; } = "";
        public int Pieces { get; set; }
        public double BarHeight { get; set; }

        // 【核心修复1】：改为 WPF 原生的 Thickness 类型
        public Thickness LabelMargin { get; set; }

        public string AvgTime { get; set; } = "";
        public string TotalTime { get; set; } = "";
    }

    public partial class StatisticsWindow : Window
    {
        public List<ChartItem> ChartData { get; set; } = new();
        public int WeekTotalPieces { get; set; }

        public StatisticsWindow(VideoDatabase db)
        {
            InitializeComponent();
            GenerateChartData(db);
            this.DataContext = this;
        }

        private void GenerateChartData(VideoDatabase db)
        {
            // 从 SQLite 数据库获取最近 7 天统计
            List<DailyStat> history = new List<DailyStat>();
            try { if (db != null) history = db.GetDailyStats(7); } catch { }

            ChartData = new List<ChartItem>();
            int maxPieces = 1; // 防除0

            // 预先扫一遍最近 7 天，找最大值用来算柱子比例
            for (int i = 6; i >= 0; i--)
            {
                var date = DateTime.Now.AddDays(-i);
                var stat = history?.FirstOrDefault(s => s.Date == date.ToString("yyyy-MM-dd"));
                if (stat != null && stat.TotalPieces > maxPieces) maxPieces = stat.TotalPieces;
            }

            // 生成最近 7 天的柱子数据
            for (int i = 6; i >= 0; i--)
            {
                var date = DateTime.Now.AddDays(-i);
                var stat = history?.FirstOrDefault(s => s.Date == date.ToString("yyyy-MM-dd"));

                int pieces = stat?.TotalPieces ?? 0;
                double totalSec = stat?.TotalDurationSec ?? 0;
                TimeSpan tTime = TimeSpan.FromSeconds(totalSec);
                TimeSpan aTime = pieces > 0 ? TimeSpan.FromSeconds(totalSec / pieces) : TimeSpan.Zero;

                // 限制柱状图最大高度 350px 进行按比例缩放
                double height = (pieces / (double)maxPieces) * 350.0;
                if (height < 5 && pieces > 0) height = 5;

                WeekTotalPieces += pieces;

                ChartData.Add(new ChartItem
                {
                    DateLabel = i == 0 ? "今天" : date.ToString("dd日"),
                    DateSubLabel = date.ToString("ddd"),
                    Pieces = pieces,
                    BarHeight = height,

                    // 【核心修复2】：在后台直接生成完整的 Margin 对象
                    LabelMargin = new Thickness(0, 0, 0, height + 5),

                    TotalTime = $"{(int)tTime.TotalHours:D2}:{tTime.Minutes:D2}:{tTime.Seconds:D2}",
                    AvgTime = aTime.ToString(@"mm\:ss")
                });
            }
        }
    }
}