using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using System.IO;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

public sealed class DashboardDataService
{
    private readonly VideoDatabase? _database;
    private readonly string _mobileBackupStateDirectory;

    internal DashboardDataService(VideoDatabase? database, string? mobileBackupStateDirectory = null)
    {
        _database = database;
        _mobileBackupStateDirectory = mobileBackupStateDirectory ?? Path.Combine(AppPaths.CacheDir, "mobile-backup");
    }

    public IReadOnlyList<MobileUploadDashboardItem> GetMobileUploads()
    {
        if (!Directory.Exists(_mobileBackupStateDirectory))
            return Array.Empty<MobileUploadDashboardItem>();

        var result = new List<MobileUploadDashboardItem>();
        foreach (string statePath in Directory.EnumerateFiles(_mobileBackupStateDirectory, "*.json"))
        {
            string uploadId = Path.GetFileNameWithoutExtension(statePath);
            if (uploadId.Length != 64 || !uploadId.All(Uri.IsHexDigit))
                continue;

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statePath));
                JsonElement root = document.RootElement;
                long total = ReadInt64(root, "totalBytes");
                string partPath = Path.Combine(_mobileBackupStateDirectory, $"{uploadId}.part");
                long received = File.Exists(partPath)
                    ? new FileInfo(partPath).Length
                    : ReadInt64(root, "receivedBytes");
                DateTime updatedAt = ReadDateTime(root, "updatedAtUtc");
                string status = received <= 0
                    ? "等待上传"
                    : total > 0 && received >= total ? "等待校验" : "正在上传";
                result.Add(new MobileUploadDashboardItem(
                    uploadId,
                    received,
                    total,
                    total <= 0 ? 0 : Math.Clamp(received * 100d / total, 0, 100),
                    status,
                    updatedAt));
            }
            catch (IOException)
            {
                // 上传线程可能正在原子替换状态文件，下次刷新再读取。
            }
            catch (JsonException)
            {
                result.Add(new MobileUploadDashboardItem(uploadId, 0, 0, 0, "状态文件需要处理", File.GetLastWriteTime(statePath)));
            }
        }

        return result.OrderByDescending(item => item.UpdatedAt).Take(50).ToList();
    }

    public IReadOnlyList<RecentMobileVideoItem> GetRecentMobileVideos(int limit = 20)
    {
        if (_database == null)
            return Array.Empty<RecentMobileVideoItem>();

        return _database.QueryVideos(DateTime.Today.AddDays(-30), DateTime.Today.AddDays(1))
            .Where(record => string.Equals(record.SourceType, "external", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.StartTime)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(record => new RecentMobileVideoItem(
                record.Id,
                string.IsNullOrWhiteSpace(record.TrackingNumber) ? "未识别面单" : record.TrackingNumber,
                string.IsNullOrWhiteSpace(record.SourceDeviceName) ? "手机设备" : record.SourceDeviceName,
                record.StartTime,
                string.IsNullOrWhiteSpace(record.ContentSha256) ? "未校验" : "SHA256 已校验"))
            .ToList();
    }

    public IReadOnlyList<RecentOrderDashboardItem> GetRecentOrders(int limit = 20)
    {
        if (_database == null)
            return Array.Empty<RecentOrderDashboardItem>();

        return _database.GetRecentOrderInfos()
            .OrderByDescending(order => order.PushTime)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(order => new RecentOrderDashboardItem(
                order.TrackingNumber,
                order.OrderId,
                order.PushTime,
                BuildOrderSummary(order)))
            .ToList();
    }

    private static string BuildOrderSummary(OrderInfo order)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(order.BuyerMessage)) parts.Add("买家留言");
        if (!string.IsNullOrWhiteSpace(order.SellerMemo)) parts.Add("卖家备注");
        if (!string.IsNullOrWhiteSpace(order.ProductInfo)) parts.Add("商品信息");
        if (order.HasRefund || order.IsPrintedRefund) parts.Add("退款信息");
        return parts.Count == 0 ? "基础订单信息" : string.Join("、", parts);
    }

    private static long ReadInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : 0;

    private static DateTime ReadDateTime(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.TryGetDateTime(out DateTime result)
            ? result.ToLocalTime()
            : DateTime.MinValue;
}

public sealed record MobileUploadDashboardItem(
    string UploadId,
    long ReceivedBytes,
    long TotalBytes,
    double ProgressPercent,
    string Status,
    DateTime UpdatedAt)
{
    public string DisplayId => UploadId.Length <= 12 ? UploadId : UploadId[..12];
    public string SizeText => $"{FormatBytes(ReceivedBytes)} / {FormatBytes(TotalBytes)}";
    public string UpdatedAtText => UpdatedAt == DateTime.MinValue ? "" : UpdatedAt.ToString("MM-dd HH:mm:ss");

    private static string FormatBytes(long value) => value switch
    {
        >= 1024L * 1024 * 1024 => $"{value / (1024d * 1024 * 1024):F1} GB",
        >= 1024L * 1024 => $"{value / (1024d * 1024):F1} MB",
        >= 1024L => $"{value / 1024d:F1} KB",
        _ => $"{Math.Max(0, value)} B"
    };
}

public sealed record RecentMobileVideoItem(
    long Id,
    string TrackingNumber,
    string DeviceName,
    DateTime StartedAt,
    string VerificationText)
{
    public string StartedAtText => StartedAt.ToString("MM-dd HH:mm:ss");
}

public sealed record RecentOrderDashboardItem(
    string TrackingNumber,
    string OrderId,
    DateTime PushTime,
    string Summary)
{
    public string PushTimeText => PushTime.ToString("MM-dd HH:mm:ss");
}
