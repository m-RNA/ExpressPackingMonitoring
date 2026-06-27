#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring
{
    /// <summary>
    /// 内嵌轻量 HTTP 服务器，供局域网客户端搜索、播放和下载视频。
    /// 基于 HttpListener，无需额外 NuGet 依赖。
    /// </summary>
    /// <summary>订单附加信息（从快递助手页面推送）</summary>
    public class OrderInfo
    {
        public string TrackingNumber { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string BuyerMessage { get; set; } = "";
        public string SellerMemo { get; set; } = "";
        public string ProductInfo { get; set; } = "";
        public DateTime PushTime { get; set; } = DateTime.Now;
    }

    public sealed class WebServer : IDisposable
    {
        private HttpListener _listener;
        private readonly VideoDatabase _db;
        private readonly Func<bool> _isRecordingProvider;
        private readonly Func<VideoRecord, MkvConversionResult> _mkvConverter;
        private readonly CancellationTokenSource _cts = new();
        private Task _listenTask;
        private bool _disposed;
        private static readonly string _logPath = AppPaths.WebDebugLogPath;
        private static readonly string _transCacheDir = AppPaths.TranscodeCacheDir;
        private long _transCacheMaxBytes = 1024L * 1024 * 1024; // 默认 1GB，可config覆盖

        // 订单信息缓存：Key 为快递单号(大写)，保留最近72小时的数据
        private readonly Dictionary<string, OrderInfo> _orderInfoCache = new();
        private readonly object _orderInfoLock = new();
        private const int MaxOrderInfoEntries = 5000;
        private static readonly string _orderInfoCachePath = AppPaths.OrderInfoCachePath;

        /// <summary>收到油猴脚本推送的订单信息时触发，参数为本次推送的所有订单</summary>
        public event Action<List<OrderInfo>> OrderInfoReceived;

        public int Port { get; }
        public bool EnableOrderInfoLog { get; set; }

        private void Log(string msg)
        {
            if (!EnableOrderInfoLog) return;
            WriteLog(msg);
        }

        private static void WriteLog(string msg)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}";
                lock (_logPath)
                    File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch { }
        }

        public WebServer(VideoDatabase db, int port = 5280, int transCacheMaxMB = 1024, Func<bool> isRecordingProvider = null, Func<VideoRecord, MkvConversionResult> mkvConverter = null)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _isRecordingProvider = isRecordingProvider ?? (() => false);
            _mkvConverter = mkvConverter;
            Port = port;
            _transCacheMaxBytes = (long)transCacheMaxMB * 1024 * 1024;
            _listener = CreateListener(port);
            LoadOrderInfoCache();
        }

        private static HttpListener CreateListener(int port)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{port}/");
            return listener;
        }

        public void Start()
        {
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException)
            {
                // URL ACL 未注册，尝试自动注册后重试
                RegisterUrlAcl(Port);
                try { _listener.Close(); } catch { }
                _listener = CreateListener(Port);
                try
                {
                    _listener.Start();
                }
                catch (HttpListenerException ex)
                {
                    throw new InvalidOperationException($"Web 服务监听 http://+:{Port}/ 失败，请检查端口占用、URL ACL 或防火墙权限。", ex);
                }
            }
            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
        }

        /// <summary>
        /// 注册 URL ACL 和防火墙规则，需要管理员权限时会弹出 UAC 提示。
        /// </summary>
        private static void RegisterUrlAcl(int port)
        {
            string url = $"http://+:{port}/";
            RunElevatedCmd($"netsh http add urlacl url={url} user=Everyone", "注册 Web 服务 URL ACL");
            // 同时确保防火墙规则存在
            RunElevatedCmd($"netsh advfirewall firewall add rule name=\"快递打包监控 Web服务\" dir=in action=allow protocol=TCP localport={port}", "注册 Web 服务防火墙规则");
        }

        private static void RunElevatedCmd(string arguments, string actionName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C {arguments}",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                if (proc == null)
                    throw new InvalidOperationException($"{actionName}失败：无法启动管理员命令。");

                if (!proc.WaitForExit(15000))
                    throw new TimeoutException($"{actionName}超时，请手动以管理员身份运行 netsh 或关闭 Web 服务。");

                if (proc.ExitCode != 0)
                    throw new InvalidOperationException($"{actionName}失败，netsh 退出码：{proc.ExitCode}。");
            }
            catch (Exception ex) when (ex is not InvalidOperationException && ex is not TimeoutException)
            {
                throw new InvalidOperationException($"{actionName}失败，可能是用户取消了管理员授权或系统拒绝执行。", ex);
            }
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequest(ctx), token);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
                string method = ctx.Request.HttpMethod;
                Log($">>> {method} {path} from {ctx.Request.RemoteEndPoint}");

                ApplyCorsHeaders(ctx);
                ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (method == "OPTIONS")
                {
                    ctx.Response.StatusCode = 204;
                    ctx.Response.OutputStream.Close();
                    return;
                }

                switch (path)
                {
                    case "" or "/":
                        ServeIndexPage(ctx);
                        break;
                    case "/api/videos":
                        HandleSearchVideos(ctx);
                        break;
                    case "/api/storage":
                        HandleStorageOverview(ctx);
                        break;
                    case "/api/orderinfo":
                        if (method == "POST")
                            HandlePushOrderInfo(ctx);
                        else
                            HandleQueryOrderInfo(ctx);
                        break;
                    default:
                        if (method == "HEAD" && path.StartsWith("/api/videos/") && path.EndsWith("/play"))
                        {
                            // HEAD 请求只返回 headers，不启动转码/传输
                            ctx.Response.ContentType = "video/mp4";
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentLength64 = 0;
                            ctx.Response.OutputStream.Close();
                        }
                        else if (path.StartsWith("/api/videos/") && path.EndsWith("/download"))
                            HandleDownload(ctx, path);
                        else if (path.StartsWith("/api/videos/") && path.EndsWith("/play"))
                            HandlePlay(ctx, path);
                        else
                            SendJson(ctx, 404, new { error = "Not Found" });
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"!!! HandleRequest 异常: {ex}");
                try { SendJson(ctx, 500, new { error = ex.Message }); } catch { }
            }
        }

        private static void ApplyCorsHeaders(HttpListenerContext ctx)
        {
            string origin = ctx.Request.Headers["Origin"];
            if (string.IsNullOrWhiteSpace(origin)) return;
            if (!IsAllowedCorsOrigin(origin)) return;

            ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
            ctx.Response.Headers["Vary"] = "Origin";
        }

        private static bool IsAllowedCorsOrigin(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            string host = uri.Host;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("::1", StringComparison.OrdinalIgnoreCase))
                return true;

            if (host.Equals("kuaidizs.cn", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".kuaidizs.cn", StringComparison.OrdinalIgnoreCase))
                return true;

            if (IPAddress.TryParse(host, out var ip))
                return IsPrivateAddress(ip);

            return false;
        }

        private static bool IsPrivateAddress(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;

            byte[] bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return bytes[0] == 10 ||
                       (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                       (bytes[0] == 192 && bytes[1] == 168) ||
                       (bytes[0] == 169 && bytes[1] == 254);
            }

            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        }

        // ───── API: 推送订单信息 (来自油猴脚本) ─────
        private void HandlePushOrderInfo(HttpListenerContext ctx)
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = reader.ReadToEnd();
                var items = JsonSerializer.Deserialize<List<OrderInfo>>(body, _jsonOptions);
                if (items == null || items.Count == 0)
                {
                    SendJson(ctx, 400, new { error = "空数据" });
                    return;
                }

                int count = 0;
                lock (_orderInfoLock)
                {
                    // 超过上限时清理72小时前的旧数据
                    if (_orderInfoCache.Count > MaxOrderInfoEntries)
                    {
                        var cutoff = DateTime.Now.AddHours(-72);
                        var expiredKeys = _orderInfoCache.Where(kv => kv.Value.PushTime < cutoff).Select(kv => kv.Key).ToList();
                        foreach (var k in expiredKeys) _orderInfoCache.Remove(k);
                    }

                    foreach (var item in items)
                    {
                        if (string.IsNullOrWhiteSpace(item.TrackingNumber)) continue;
                        string key = item.TrackingNumber.Trim().ToUpperInvariant();
                        item.PushTime = DateTime.Now;
                        _orderInfoCache[key] = item;
                        count++;
                    }
                }

                if (EnableOrderInfoLog)
                {
                    Log($"HandlePushOrderInfo: 接收 {count} 条订单信息, 缓存总数={_orderInfoCache.Count}");
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrWhiteSpace(item.TrackingNumber))
                            Log($"  订单: 运单号={item.TrackingNumber}, 订单号={item.OrderId}, 买家留言=[{item.BuyerMessage}], 卖家备注=[{item.SellerMemo}], 商品=[{item.ProductInfo}]");
                    }
                }

                // 持久化到磁盘
                SaveOrderInfoCache();

                // 通知订阅方预生成语音缓存
                try { OrderInfoReceived?.Invoke(items); } catch { }

                SendJson(ctx, 200, new { ok = true, count });
            }
            catch (Exception ex)
            {
                Log($"HandlePushOrderInfo 异常: {ex.Message}");
                SendJson(ctx, 400, new { error = ex.Message });
            }
        }

        // ───── API: 查询订单信息 ─────
        private void HandleQueryOrderInfo(HttpListenerContext ctx)
        {
            string trackingNo = (ctx.Request.QueryString["trackingNo"] ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(trackingNo))
            {
                SendJson(ctx, 400, new { error = "缺少 trackingNo 参数" });
                return;
            }

            lock (_orderInfoLock)
            {
                if (_orderInfoCache.TryGetValue(trackingNo, out var info))
                {
                    SendJson(ctx, 200, new
                    {
                        found = true,
                        info.TrackingNumber,
                        info.OrderId,
                        info.BuyerMessage,
                        info.SellerMemo,
                        info.ProductInfo
                    });
                    return;
                }
            }

            SendJson(ctx, 200, new { found = false });
        }

        /// <summary>根据快递单号查询已推送的订单信息（供 ViewModel 调用）</summary>
        public OrderInfo GetOrderInfo(string trackingNo)
        {
            if (string.IsNullOrWhiteSpace(trackingNo)) return null;
            string key = trackingNo.Trim().ToUpperInvariant();
            lock (_orderInfoLock)
            {
                if (_orderInfoCache.TryGetValue(key, out var info))
                {
                    if (EnableOrderInfoLog)
                        Log($"GetOrderInfo 命中: {key} => 买家留言=[{info.BuyerMessage}], 卖家备注=[{info.SellerMemo}], 商品=[{info.ProductInfo}]");
                    return info;
                }
                if (EnableOrderInfoLog)
                    Log($"GetOrderInfo 未命中: {key}, 缓存总数={_orderInfoCache.Count}");
                return null;
            }
        }

        // ───── 订单信息缓存持久化 ─────
        private void LoadOrderInfoCache()
        {
            try
            {
                if (!File.Exists(_orderInfoCachePath)) return;
                string json = File.ReadAllText(_orderInfoCachePath);
                var items = JsonSerializer.Deserialize<List<OrderInfo>>(json, _jsonOptions);
                if (items == null) return;

                var cutoff = DateTime.Now.AddHours(-72);
                lock (_orderInfoLock)
                {
                    foreach (var item in items)
                    {
                        if (string.IsNullOrWhiteSpace(item.TrackingNumber)) continue;
                        if (item.PushTime < cutoff) continue; // 跳过超过72小时的
                        string key = item.TrackingNumber.Trim().ToUpperInvariant();
                        _orderInfoCache[key] = item;
                    }
                }
                Debug.WriteLine($"[WebServer] 从磁盘恢复 {_orderInfoCache.Count} 条订单信息缓存");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebServer] 加载订单缓存失败: {ex.Message}");
            }
        }

        private void SaveOrderInfoCache()
        {
            try
            {
                List<OrderInfo> snapshot;
                var cutoff = DateTime.Now.AddHours(-72);
                lock (_orderInfoLock)
                {
                    // 保存前清理超过72小时的过期数据
                    var expiredKeys = _orderInfoCache.Where(kv => kv.Value.PushTime < cutoff).Select(kv => kv.Key).ToList();
                    foreach (var k in expiredKeys) _orderInfoCache.Remove(k);
                    snapshot = _orderInfoCache.Values.ToList();
                }
                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                File.WriteAllText(_orderInfoCachePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebServer] 保存订单缓存失败: {ex.Message}");
            }
        }

        // ───── API: 搜索视频 ─────
        private void HandleStorageOverview(HttpListenerContext ctx)
        {
            try
            {
                var config = LoadAppConfig();
                var locations = config.StorageLocations?
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                    .OrderByDescending(x => x.Priority)
                    .ToList() ?? new List<StorageLocation>();

                var configuredPaths = locations.Select(BuildStoragePathInfo).ToList();
                var records = _db.GetActiveStorageVideoFiles()
                    .Where(x => !string.IsNullOrWhiteSpace(x.FilePath))
                    .ToList();

                var existingRecords = records.Where(x =>
                {
                    try { return File.Exists(x.FilePath); }
                    catch { return false; }
                }).ToList();

                long usedBytes = existingRecords.Sum(x => GetExistingFileSize(x.FilePath, x.FileSizeBytes));
                long totalBytes = configuredPaths.Sum(x => x.TotalBytes);
                if (totalBytes <= 0 && usedBytes > 0)
                    totalBytes = usedBytes;
                long freeBytes = Math.Max(0, totalBytes - usedBytes);

                DateTime? oldest = existingRecords.Count > 0 ? existingRecords.Min(x => x.StartTime) : null;
                DateTime? latest = existingRecords.Count > 0 ? existingRecords.Max(x => x.StartTime) : null;
                int savedDays = CalculateSavedDays(oldest, latest);

                var recentRecords = existingRecords
                    .Where(x => x.StartTime.Date >= DateTime.Today.AddDays(-9))
                    .ToList();
                int historyDays = recentRecords.Select(x => x.StartTime.Date).Distinct().Count();
                long historyBytes = recentRecords.Sum(x => GetExistingFileSize(x.FilePath, x.FileSizeBytes));

                string estimateBasis = "";
                double avgGBPerDay = 0;
                double? estimatedRetentionDays = null;
                if (historyDays > 0 && historyBytes > 0)
                {
                    avgGBPerDay = BytesToGB(historyBytes) / historyDays;
                    if (avgGBPerDay > 0 && totalBytes > 0)
                    {
                        estimatedRetentionDays = BytesToGB(totalBytes) / avgGBPerDay;
                        estimateBasis = $"基于最近 {historyDays} 天录像占用 {FormatGB(historyBytes)} 估算";
                    }
                }
                else if (savedDays > 0 && usedBytes > 0)
                {
                    avgGBPerDay = BytesToGB(usedBytes) / savedDays;
                    if (avgGBPerDay > 0 && totalBytes > 0)
                    {
                        estimatedRetentionDays = BytesToGB(totalBytes) / avgGBPerDay;
                        historyDays = savedDays;
                        historyBytes = usedBytes;
                        estimateBasis = "基于当前已保存录像估算，结果仅供参考";
                    }
                }

                var pathDtos = configuredPaths.Select(path =>
                {
                    long pathUsed = existingRecords
                        .Where(x => IsPathUnderDirectory(x.FilePath, path.Path))
                        .Sum(x => GetExistingFileSize(x.FilePath, x.FileSizeBytes));
                    long pathFree = Math.Max(0, path.TotalBytes - pathUsed);
                    return new
                    {
                        path = path.DisplayPath,
                        totalGB = Math.Round(BytesToGB(path.TotalBytes), 1),
                        usedGB = Math.Round(BytesToGB(pathUsed), 1),
                        freeGB = Math.Round(BytesToGB(pathFree), 1),
                        available = path.Available
                    };
                }).ToList();

                SendJson(ctx, 200, new
                {
                    totalGB = Math.Round(BytesToGB(totalBytes), 1),
                    usedGB = Math.Round(BytesToGB(usedBytes), 1),
                    freeGB = Math.Round(BytesToGB(freeBytes), 1),
                    oldestVideoTime = oldest?.ToString("yyyy-MM-dd HH:mm:ss"),
                    latestVideoTime = latest?.ToString("yyyy-MM-dd HH:mm:ss"),
                    savedDays,
                    historyDays,
                    historyUsedGB = Math.Round(BytesToGB(historyBytes), 1),
                    avgGBPerDay = Math.Round(avgGBPerDay, 2),
                    estimatedRetentionDays = estimatedRetentionDays.HasValue ? Math.Round(estimatedRetentionDays.Value, 0) : (double?)null,
                    estimateBasis,
                    pathCount = configuredPaths.Count,
                    paths = pathDtos
                });
            }
            catch (Exception ex)
            {
                Log($"HandleStorageOverview 异常: {ex.Message}");
                SendJson(ctx, 500, new { error = "存储信息暂不可用" });
            }
        }

        private static AppConfig LoadAppConfig()
        {
            try
            {
                string configPath = AppPaths.ConfigPath;
                if (!File.Exists(configPath))
                    return new AppConfig();

                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath)) ?? new AppConfig();
            }
            catch
            {
                return new AppConfig();
            }
        }

        private sealed class StoragePathInfo
        {
            public string Path { get; init; } = "";
            public string DisplayPath { get; init; } = "";
            public long TotalBytes { get; init; }
            public bool Available { get; init; }
        }

        private static StoragePathInfo BuildStoragePathInfo(StorageLocation loc)
        {
            string normalizedPath = NormalizeStoragePath(loc.Path);
            long quotaBytes = loc.QuotaGB > 0 ? (long)(loc.QuotaGB * 1073741824.0) : 0;
            bool available = false;
            try
            {
                if (Directory.Exists(normalizedPath))
                {
                    string root = Path.GetPathRoot(Path.GetFullPath(normalizedPath));
                    if (!string.IsNullOrEmpty(root))
                    {
                        var drive = new DriveInfo(root);
                        available = drive.IsReady;
                        if (available)
                        {
                            long driveUsableBytes = drive.AvailableFreeSpace + GetDirectoryVideoBytes(normalizedPath);
                            if (quotaBytes <= 0)
                                quotaBytes = driveUsableBytes;
                            else
                                quotaBytes = Math.Min(quotaBytes, driveUsableBytes);
                        }
                    }
                }
            }
            catch { }

            return new StoragePathInfo
            {
                Path = normalizedPath,
                DisplayPath = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                TotalBytes = Math.Max(0, quotaBytes),
                Available = available
            };
        }

        private static string NormalizeStoragePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string normalized = Path.IsPathRooted(path) ? path : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            try { return Path.GetFullPath(normalized); }
            catch { return normalized; }
        }

        private static long GetDirectoryVideoBytes(string folderPath)
        {
            try
            {
                var dir = new DirectoryInfo(folderPath);
                if (!dir.Exists) return 0;
                return dir.EnumerateFiles("*.*", SearchOption.AllDirectories)
                    .Where(x => string.Equals(x.Extension, ".mp4", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.Extension, ".mkv", StringComparison.OrdinalIgnoreCase))
                    .Sum(x => x.Length);
            }
            catch
            {
                return 0;
            }
        }

        private static long GetExistingFileSize(string filePath, long fallbackBytes)
        {
            try
            {
                if (File.Exists(filePath))
                    return new FileInfo(filePath).Length;
            }
            catch { }
            return Math.Max(0, fallbackBytes);
        }

        private static bool IsPathUnderDirectory(string filePath, string directoryPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(directoryPath))
                    return false;
                string fullFile = Path.GetFullPath(filePath);
                string fullDir = Path.GetFullPath(directoryPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return fullFile.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static int CalculateSavedDays(DateTime? oldest, DateTime? latest)
        {
            if (!oldest.HasValue || !latest.HasValue) return 0;
            int days = (latest.Value.Date - oldest.Value.Date).Days + 1;
            return Math.Max(1, days);
        }

        private static double BytesToGB(long bytes) => bytes / 1073741824.0;

        private static string FormatGB(long bytes)
        {
            double gb = BytesToGB(bytes);
            return gb >= 10 ? $"{gb:F0}GB" : $"{gb:F1}GB";
        }

        private void HandleSearchVideos(HttpListenerContext ctx)
        {
            var qs = ctx.Request.QueryString;
            string keyword = qs["keyword"] ?? qs["q"] ?? "";

            DateTime? startDate = DateTime.TryParse(qs["start"], out var parsedStartDate) ? parsedStartDate : null;
            DateTime? endDate = DateTime.TryParse(qs["end"], out var parsedEndDate) ? parsedEndDate : null;

            int page = int.TryParse(qs["page"], out var p) ? Math.Max(1, p) : 1;
            int pageSize = int.TryParse(qs["size"], out var s) ? Math.Clamp(s, 1, 100) : 50;

            var result = _db.QueryVideosPaged(startDate, endDate, string.IsNullOrWhiteSpace(keyword) ? null : keyword, page, pageSize);
            // SQL 层只取当前页，文件存在性仅对当前页记录检查。
            var paged = result.Records.Select(r => new
            {
                r.Id,
                r.OrderId,
                r.Mode,
                r.FileName,
                videoCodec = r.VideoCodec ?? "",
                sizeMB = Math.Round(r.FileSizeBytes / 1048576.0, 1),
                startTime = r.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                durationSec = Math.Round(r.DurationSeconds, 0),
                duration = TimeSpan.FromSeconds(r.DurationSeconds).ToString(@"mm\:ss"),
                exists = File.Exists(r.FilePath)
            });

            SendJson(ctx, 200, new { total = result.Total, page, pageSize, data = paged });
        }

        // ───── API: 流式播放 (支持 Range) ─────
        private void HandlePlay(HttpListenerContext ctx, string path)
        {
            var record = FindRecordFromPath(path, "/play");
            Log($"HandlePlay: path={path}, record={(record != null ? $"Id={record.Id}, OrderId={record.OrderId}, VideoCodec='{record.VideoCodec}', FilePath='{record.FilePath}'" : "null")}");
            if (record == null || !File.Exists(record.FilePath))
            {
                Log($"HandlePlay: 文件不存在 filePath={record?.FilePath}");
                SendJson(ctx, 404, new { error = "文件不存在" });
                return;
            }

            string filePath = EnsureMp4ContainerForPlayback(ctx, record);
            if (string.IsNullOrEmpty(filePath))
                return;

            string codec = (record.VideoCodec ?? "").Trim().ToLowerInvariant();
            bool compatMode = ctx.Request.QueryString["compat"] != "0";
            bool preflight = ctx.Request.QueryString["preflight"] == "1";
            bool allowTranscodeWhileRecording = ctx.Request.QueryString["allowTranscodeWhileRecording"] == "1";
            bool shouldTranscode = compatMode && codec != "" && codec != "h264";
            bool recording = _isRecordingProvider();
            Log($"HandlePlay: codec='{codec}', compat={(compatMode ? "1" : "0")}, 判定={(shouldTranscode ? "转码" : "直传")}");

            if (shouldTranscode && recording && !allowTranscodeWhileRecording)
            {
                SendJson(ctx, 409, new
                {
                    requiresConfirmation = true,
                    message = "正在录制，H.265 转 H.264 可能影响实时预览和录制稳定性。是否仍要继续转码播放？",
                    url = BuildPlayUrl(record.Id, compatMode, allowTranscodeWhileRecording: true)
                });
                return;
            }

            if (preflight)
            {
                SendJson(ctx, 200, new
                {
                    ok = true,
                    requiresConfirmation = false,
                    url = BuildPlayUrl(record.Id, compatMode, allowTranscodeWhileRecording)
                });
                return;
            }

            if (shouldTranscode)
            {
                ServeTranscodedStream(ctx, filePath);
            }
            else
            {
                ServeFileWithRange(ctx, filePath, inline: true);
            }
        }

        private string EnsureMp4ContainerForPlayback(HttpListenerContext ctx, VideoRecord record)
        {
            string filePath = record.FilePath;
            if (!filePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                return filePath;

            string mp4Path = Path.ChangeExtension(filePath, ".mp4");
            if (File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0)
            {
                _db.UpdateVideoFilePath(filePath, mp4Path);
                return mp4Path;
            }

            if (_mkvConverter == null)
            {
                SendJson(ctx, 500, new { error = "服务器未配置 MKV 转 MP4 转换器" });
                return "";
            }

            Log($"EnsureMp4ContainerForPlayback: 优先转换 {filePath}");
            var result = _mkvConverter(record);
            if (!result.Success || string.IsNullOrWhiteSpace(result.FilePath) || !File.Exists(result.FilePath))
            {
                SendJson(ctx, 500, new { error = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "MKV 转 MP4 失败" : result.ErrorMessage });
                return "";
            }

            return result.FilePath;
        }

        private static string BuildPlayUrl(long id, bool compatMode, bool allowTranscodeWhileRecording)
        {
            var url = $"/api/videos/{Uri.EscapeDataString(id.ToString())}/play?compat={(compatMode ? "1" : "0")}";
            if (allowTranscodeWhileRecording)
                url += "&allowTranscodeWhileRecording=1";
            return url;
        }

        // ───── FFmpeg 转码：命中缓存直接 Range 传输，否则边转码边推流 + 同时写缓存 ─────
        private void ServeTranscodedStream(HttpListenerContext ctx, string filePath)
        {
            string ffmpegPath = AppPaths.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                SendJson(ctx, 500, new { error = "服务器未找到 ffmpeg.exe，无法转码播放" });
                return;
            }

            // 用源文件路径的哈希作为缓存键
            string cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(filePath))).Substring(0, 16);
            string cachePath = Path.Combine(_transCacheDir, $"{cacheKey}.mp4");

            if (File.Exists(cachePath))
            {
                // 命中缓存 → 标准 Range 传输（支持进度条拖拽、总时长正确）
                Log($"ServeTranscodedStream: 命中缓存 {cachePath}");
                ServeFileWithRange(ctx, cachePath, inline: true);
                return;
            }

            // 首次播放 → 边转码边推流，同时写入缓存文件
            Directory.CreateDirectory(_transCacheDir);
            string tmpPath = cachePath + ".tmp";

            // 流式转码：缩到 480p + 极速设置，确保转码速度 > 实时播放速度
            string scaleFilter = "-vf scale=-2:480";
            string hwArgs = $"-loglevel warning -hwaccel auto -i \"{filePath}\" {scaleFilter} -c:v h264_nvenc -preset p1 -cq 30 -c:a aac -b:a 96k -movflags frag_keyframe+empty_moov+default_base_moof -f mp4 pipe:1";
            string swArgs = $"-loglevel warning -i \"{filePath}\" {scaleFilter} -c:v libx264 -preset ultrafast -tune zerolatency -crf 28 -c:a aac -b:a 96k -movflags frag_keyframe+empty_moov+default_base_moof -f mp4 pipe:1";

            if (!StreamTranscodeToClient(ctx, ffmpegPath, hwArgs, tmpPath))
            {
                Log("ServeTranscodedStream: NVENC 流式转码失败，回退 CPU");
                if (!StreamTranscodeToClient(ctx, ffmpegPath, swArgs, tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                    return; // 响应已在内部处理
                }
            }

            // 转码成功，将临时文件提升为正式缓存
            try { File.Move(tmpPath, cachePath, overwrite: true); } catch { }
            Task.Run(() => CleanTranscodeCache());
        }

        /// <summary>
        /// 启动 FFmpeg，将 stdout 同时推送给浏览器和写入缓存文件。
        /// 返回 true 表示 FFmpeg 正常退出且数据已发送。
        /// </summary>
        private bool StreamTranscodeToClient(HttpListenerContext ctx, string ffmpegPath, string args, string tmpPath)
        {
            Log($"StreamTranscodeToClient: {args}");
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Process proc = null;
            FileStream cacheFs = null;
            try
            {
                proc = Process.Start(psi);
                if (proc == null) return false;

                // 异步消费 stderr
                var stderrBuf = new StringBuilder();
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuf.AppendLine(e.Data); };
                proc.BeginErrorReadLine();

                ctx.Response.ContentType = "video/mp4";
                ctx.Response.StatusCode = 200;
                ctx.Response.SendChunked = true;

                cacheFs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[65536];
                using var stdout = proc.StandardOutput.BaseStream;
                int read;
                bool clientOk = true;
                while ((read = stdout.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // 写缓存
                    try { cacheFs.Write(buffer, 0, read); } catch { }
                    // 推给浏览器
                    if (clientOk)
                    {
                        try { ctx.Response.OutputStream.Write(buffer, 0, read); }
                        catch { clientOk = false; } // 客户端断开，继续写缓存
                    }
                }

                cacheFs.Close();
                cacheFs = null;
                try { ctx.Response.OutputStream.Close(); } catch { }
                proc.WaitForExit(5000);
                string stderr = stderrBuf.ToString();
                Log($"StreamTranscodeToClient: 退出码={proc.ExitCode}, stderr={stderr}");

                if (proc.ExitCode != 0)
                {
                    try { File.Delete(tmpPath); } catch { }
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log($"StreamTranscodeToClient 异常: {ex.Message}");
                try { ctx.Response.Abort(); } catch { }
                try { File.Delete(tmpPath); } catch { }
                return false;
            }
            finally
            {
                cacheFs?.Dispose();
                if (proc != null && !proc.HasExited) { try { proc.Kill(); } catch { } }
                proc?.Dispose();
            }
        }

        // ───── 转码缓存清理：超过上限时按最旧访问时间删除 ─────
        private void CleanTranscodeCache()
        {
            try
            {
                if (!Directory.Exists(_transCacheDir)) return;

                var files = new DirectoryInfo(_transCacheDir)
                    .GetFiles("*.mp4")
                    .OrderBy(f => f.LastAccessTimeUtc)
                    .ToList();

                long totalSize = files.Sum(f => f.Length);
                if (totalSize <= _transCacheMaxBytes) return;

                Log($"CleanTranscodeCache: 当前 {totalSize / 1048576}MB / 上限 {_transCacheMaxBytes / 1048576}MB，开始清理");
                foreach (var f in files)
                {
                    if (totalSize <= _transCacheMaxBytes * 0.8) break; // 清到 80% 水位
                    try
                    {
                        long size = f.Length;
                        f.Delete();
                        totalSize -= size;
                        Log($"CleanTranscodeCache: 删除 {f.Name} ({size / 1048576}MB)");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"CleanTranscodeCache 异常: {ex.Message}");
            }
        }

        // ───── 运行 FFmpeg 转码，返回是否成功 ─────
        private bool TryRunFFmpeg(string ffmpegPath, string args, string outputPath)
        {
            Log($"TryRunFFmpeg: {args}");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(120_000);
                Log($"TryRunFFmpeg: 退出码={proc.ExitCode}, stderr={stderr}");
                return proc.ExitCode == 0 && File.Exists(outputPath);
            }
            catch (Exception ex)
            {
                Log($"TryRunFFmpeg 异常: {ex.Message}");
                try { File.Delete(outputPath); } catch { }
                return false;
            }
        }

        // ───── API: 下载 ─────
        private void HandleDownload(HttpListenerContext ctx, string path)
        {
            var record = FindRecordFromPath(path, "/download");
            if (record == null || !File.Exists(record.FilePath))
            {
                SendJson(ctx, 404, new { error = "文件不存在" });
                return;
            }

            ServeFileWithRange(ctx, record.FilePath, inline: false);
        }

        // ───── 文件传输 (支持 Range 请求实现拖拽播放) ─────
        private static void ServeFileWithRange(HttpListenerContext ctx, string filePath, bool inline)
        {
            var fi = new FileInfo(filePath);
            long fileLength = fi.Length;
            string ext = fi.Extension.ToLowerInvariant();
            string mime = ext switch { ".mp4" => "video/mp4", ".mkv" => "video/x-matroska", _ => "application/octet-stream" };

            ctx.Response.ContentType = mime;
            if (!inline)
            {
                ctx.Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{Uri.EscapeDataString(fi.Name)}\"");
            }
            ctx.Response.Headers.Add("Accept-Ranges", "bytes");

            string rangeHeader = ctx.Request.Headers["Range"];
            long start = 0, end = fileLength - 1;

            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                string rangeValue = rangeHeader.Substring(6);
                var parts = rangeValue.Split('-');
                if (long.TryParse(parts[0], out long rs)) start = rs;
                if (parts.Length > 1 && long.TryParse(parts[1], out long re)) end = re;
                if (start < 0) start = 0;
                if (end >= fileLength) end = fileLength - 1;

                ctx.Response.StatusCode = 206;
                ctx.Response.Headers.Add("Content-Range", $"bytes {start}-{end}/{fileLength}");
            }
            else
            {
                ctx.Response.StatusCode = 200;
            }

            long length = end - start + 1;
            ctx.Response.ContentLength64 = length;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(start, SeekOrigin.Begin);
            byte[] buffer = new byte[65536];
            long remaining = length;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = fs.Read(buffer, 0, toRead);
                if (read == 0) break;
                try { ctx.Response.OutputStream.Write(buffer, 0, read); }
                catch { break; } // 客户端断开
                remaining -= read;
            }
            ctx.Response.OutputStream.Close();
        }

        // ───── 根据 URL 中的 ID 查找记录 ─────
        private VideoRecord FindRecordFromPath(string path, string suffix)
        {
            string idStr = path.Replace("/api/videos/", "").Replace(suffix, "").Trim('/');
            if (!long.TryParse(idStr, out long id)) return null;
            return _db.GetVideoById(id);
        }

        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // ───── JSON 响应 ─────
        private static void SendJson(HttpListenerContext ctx, int statusCode, object data)
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, _jsonOptions);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        // ───── 内嵌前端页面 ─────
        private static void ServeIndexPage(HttpListenerContext ctx)
        {
            string indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Web", "index.html");
            string html = File.Exists(indexPath)
                ? File.ReadAllText(indexPath, Encoding.UTF8)
                : MissingIndexHtml;

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _cts.Dispose();
        }

        // ═══════════════════════════════════════════════
        //  内嵌 HTML 单页应用
        // ═══════════════════════════════════════════════
        private const string MissingIndexHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head><meta charset="UTF-8"><title>页面文件缺失</title></head>
<body style="font-family: Microsoft YaHei UI, sans-serif; padding: 32px; color: #172033;">
  <h1>页面文件缺失</h1>
  <p>未找到 Web/index.html，请检查程序发布目录是否包含该文件。</p>
</body>
</html>
""";
    }
}
