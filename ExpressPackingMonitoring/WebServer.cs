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
        private readonly CancellationTokenSource _cts = new();
        private Task _listenTask;
        private bool _disposed;
        private static readonly string _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web_debug.log");
        private static readonly string _transCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "transcache");
        private long _transCacheMaxBytes = 1024L * 1024 * 1024; // 默认 1GB，可config覆盖

        // 订单信息缓存：Key 为快递单号(大写)，保留最近72小时的数据
        private readonly Dictionary<string, OrderInfo> _orderInfoCache = new();
        private readonly object _orderInfoLock = new();
        private const int MaxOrderInfoEntries = 5000;
        private static readonly string _orderInfoCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "orderinfo_cache.json");

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

        public WebServer(VideoDatabase db, int port = 5280, int transCacheMaxMB = 1024)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
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
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
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

            if (!DateTime.TryParse(qs["start"], out var startDate))
                startDate = DateTime.Today.AddDays(-7);
            if (!DateTime.TryParse(qs["end"], out var endDate))
                endDate = DateTime.Today;

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

            string codec = (record.VideoCodec ?? "").Trim().ToLowerInvariant();
            Log($"HandlePlay: codec='{codec}', 判定={(codec != "" && codec != "h264" ? "转码" : "直传")}");
            if (codec != "" && codec != "h264")
            {
                ServeTranscodedStream(ctx, record.FilePath);
            }
            else
            {
                ServeFileWithRange(ctx, record.FilePath, inline: true);
            }
        }

        // ───── FFmpeg 转码：命中缓存直接 Range 传输，否则边转码边推流 + 同时写缓存 ─────
        private void ServeTranscodedStream(HttpListenerContext ctx, string filePath)
        {
            string ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
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
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            byte[] html = Encoding.UTF8.GetBytes(IndexHtml);
            ctx.Response.ContentLength64 = html.Length;
            ctx.Response.OutputStream.Write(html, 0, html.Length);
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
        private const string IndexHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>快递打包录像回放</title>
<style>
  :root{--bg:#f4f6f8;--panel:#fff;--panel2:#f8fafc;--text:#172033;--muted:#667085;--border:#dde3ea;--strong:#c8d2df;--primary:#2563eb;--primary2:#1d4ed8;--soft:#e8f0ff;--ok:#138a52;--warn:#b7791f;--bad:#c24135;--shadow:0 10px 26px rgba(27,39,63,.08)}
  *{box-sizing:border-box}body{margin:0;min-height:100vh;background:var(--bg);color:var(--text);font-family:"Microsoft YaHei UI","Microsoft YaHei",-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif}button,input{font:inherit}button{cursor:pointer}.page{max-width:1240px;margin:0 auto;padding:24px 28px 36px}.topbar{display:flex;align-items:flex-end;justify-content:space-between;gap:18px;margin-bottom:18px}.title-block h1{margin:0;font-size:26px;line-height:1.2;font-weight:750}.title-block p{margin:8px 0 0;color:var(--muted);font-size:13px}.server-badge{display:inline-flex;align-items:center;gap:8px;height:32px;padding:0 12px;border:1px solid var(--border);border-radius:6px;color:var(--muted);background:rgba(255,255,255,.72);font-size:12px;white-space:nowrap}.dot{width:8px;height:8px;border-radius:50%;background:var(--ok);box-shadow:0 0 0 3px rgba(19,138,82,.12)}
  .overview{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:14px;margin-bottom:16px}.summary-card{min-height:132px;background:var(--panel);border:1px solid var(--border);border-radius:8px;padding:16px;box-shadow:var(--shadow);display:flex;flex-direction:column;justify-content:space-between}.summary-head{display:flex;align-items:center;gap:9px;color:var(--muted);font-size:13px;font-weight:650}.summary-icon{width:28px;height:28px;display:inline-grid;place-items:center;border-radius:6px;background:var(--soft);color:var(--primary);flex:0 0 auto}.summary-main{margin:12px 0 6px;font-size:26px;font-weight:760;line-height:1.18;color:var(--text)}.summary-note{min-height:18px;color:var(--muted);font-size:12px;line-height:1.45}.progress{width:100%;height:8px;border-radius:999px;background:#e7ecf2;overflow:hidden;margin-top:11px}.progress span{display:block;height:100%;width:0;background:linear-gradient(90deg,#2563eb,#22a6f2);border-radius:inherit}
  .toolbar{background:var(--panel);border:1px solid var(--border);border-radius:8px;padding:14px;box-shadow:var(--shadow);margin-bottom:14px}.toolbar form{display:grid;grid-template-columns:152px 152px minmax(260px,1fr) auto;gap:12px;align-items:end}.field{display:flex;flex-direction:column;gap:6px;min-width:0}.field label{font-size:12px;color:var(--muted);font-weight:650}.field input{height:38px;border:1px solid var(--strong);border-radius:6px;padding:0 11px;color:var(--text);background:#fff;outline:none}.field input:focus{border-color:var(--primary);box-shadow:0 0 0 3px rgba(37,99,235,.12)}.btn{height:38px;border:1px solid var(--strong);border-radius:6px;padding:0 14px;background:#fff;color:var(--text);display:inline-flex;align-items:center;justify-content:center;gap:7px;font-weight:650;white-space:nowrap}.btn:hover{background:var(--panel2)}.btn-primary{background:var(--primary);border-color:var(--primary);color:#fff;min-width:102px}.btn-primary:hover{background:var(--primary2)}.btn[disabled]{opacity:.45;cursor:not-allowed}.icon{width:16px;height:16px;stroke:currentColor;stroke-width:2;fill:none;stroke-linecap:round;stroke-linejoin:round;flex:0 0 auto}
  .list-panel{background:var(--panel);border:1px solid var(--border);border-radius:8px;box-shadow:var(--shadow);overflow:hidden}.list-header{height:48px;padding:0 16px;display:flex;align-items:center;justify-content:space-between;gap:12px;border-bottom:1px solid var(--border)}.list-title{font-size:15px;font-weight:750}.results-info{color:var(--muted);font-size:12px}.video-list{display:flex;flex-direction:column}.video-item{display:grid;grid-template-columns:minmax(180px,1.25fr) minmax(320px,2.2fr) auto;gap:16px;align-items:center;padding:14px 16px;border-bottom:1px solid #edf1f5}.video-item:hover{background:#fbfdff}.video-item:last-child{border-bottom:none}.order-line{display:flex;align-items:center;gap:9px;min-width:0}.order-id{font-size:16px;font-weight:760;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.mode-badge,.status-badge,.codec-badge{display:inline-flex;align-items:center;height:22px;padding:0 8px;border-radius:999px;font-size:12px;font-weight:700;white-space:nowrap}.mode-badge{background:#eef6ff;color:#1d5fae}.mode-badge.return{background:#fff4e5;color:#9a5b10}.status-badge{margin-top:8px;background:#eaf7ef;color:var(--ok);width:fit-content}.status-badge.missing{background:#fff0ed;color:var(--bad)}.meta-grid{display:grid;grid-template-columns:150px 82px 86px minmax(120px,1fr);gap:8px 16px;align-items:center;color:var(--muted);font-size:12px;min-width:0}.meta-cell{min-width:0;display:flex;align-items:center;gap:6px}.meta-cell .text{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.codec-badge{background:#fff7db;color:var(--warn);border-radius:5px}.video-actions{display:flex;justify-content:flex-end;gap:8px}.btn-sm{height:32px;padding:0 11px;font-size:12px}.btn-play{color:var(--primary);border-color:#aac4ff;background:#f8fbff}.btn-play:hover{background:var(--soft)}
  .pagination{display:flex;justify-content:center;align-items:center;gap:6px;padding:14px 16px;border-top:1px solid var(--border);min-height:60px}.page-btn{min-width:34px;padding:0 10px}.page-btn.active{background:var(--primary);border-color:var(--primary);color:#fff}.empty{padding:58px 18px;text-align:center;color:var(--muted)}.empty-title{font-size:16px;font-weight:750;color:var(--text);margin-bottom:6px}.empty-sub{font-size:13px}.player-overlay{display:none;position:fixed;inset:0;z-index:100;background:rgba(15,23,42,.72);align-items:center;justify-content:center;padding:28px}.player-overlay.active{display:flex}.player-box{width:min(1080px,96vw);background:#0b1020;border-radius:8px;overflow:hidden;box-shadow:0 24px 80px rgba(0,0,0,.45)}.player-top{height:46px;padding:0 14px 0 18px;display:flex;align-items:center;justify-content:space-between;gap:12px;color:#fff;background:#111827}.player-title{font-size:14px;font-weight:650;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.player-close{width:32px;height:32px;border-radius:6px;border:1px solid rgba(255,255,255,.18);background:transparent;color:#fff;font-size:22px;line-height:1}.player-close:hover{background:rgba(255,255,255,.12)}.player-box video{display:block;width:100%;max-height:calc(90vh - 46px);background:#000}
  @media (max-width:820px){.page{padding:18px 14px 28px}.topbar{align-items:flex-start;flex-direction:column}.overview{grid-template-columns:1fr}.toolbar form{grid-template-columns:1fr}.video-item{grid-template-columns:1fr;gap:10px}.meta-grid{grid-template-columns:repeat(2,minmax(0,1fr))}.video-actions{justify-content:flex-start;flex-wrap:wrap}}
</style>
</head>
<body>
<div class="page">
  <header class="topbar"><div class="title-block"><h1>快递打包录像回放</h1><p>按日期或订单号检索局域网监控端录像，支持在线播放、转码预览和文件下载。</p></div><div class="server-badge"><span class="dot"></span><span>局域网 Web 服务</span></div></header>
  <section class="overview" aria-label="录像保留情况">
    <article class="summary-card"><div><div class="summary-head"><span class="summary-icon" data-icon="calendar"></span><span>当前可追溯到</span></div><div class="summary-main" id="oldestValue">加载中</div></div><div class="summary-note" id="oldestNote">正在读取录像存储情况</div></article>
    <article class="summary-card"><div><div class="summary-head"><span class="summary-icon" data-icon="clock"></span><span>预计可保留</span></div><div class="summary-main" id="retentionValue">加载中</div></div><div class="summary-note" id="retentionNote">估算值仅供参考</div></article>
    <article class="summary-card"><div><div class="summary-head"><span class="summary-icon" data-icon="database"></span><span>存储空间</span></div><div class="summary-main" id="storageValue">加载中</div></div><div class="summary-note" id="storageNote">正在读取配置目录</div><div class="progress"><span id="storageProgress"></span></div></article>
  </section>
  <section class="toolbar" aria-label="搜索条件"><form onsubmit="doSearch(); return false;"><div class="field"><label for="startDate">开始日期</label><input type="date" id="startDate"></div><div class="field"><label for="endDate">结束日期</label><input type="date" id="endDate"></div><div class="field"><label for="keyword">订单号或文件名</label><input type="text" id="keyword" placeholder="输入订单号关键词搜索"></div><button type="submit" class="btn btn-primary"><span data-icon="search"></span><span>搜索</span></button></form></section>
  <section class="list-panel" aria-label="录像列表"><div class="list-header"><div class="list-title">录像列表</div><div class="results-info" id="resultsInfo"></div></div><div class="video-list" id="videoList"></div><div class="pagination" id="pagination"></div></section>
</div>
<div class="player-overlay" id="playerOverlay" onclick="closePlayer(event)"><div class="player-box" onclick="event.stopPropagation()"><div class="player-top"><div class="player-title" id="playerTitle"></div><button class="player-close" type="button" aria-label="关闭播放器" onclick="closePlayer()">×</button></div><video id="videoPlayer" controls></video></div></div>
<script>
let currentPage=1;const pageSize=20;const icons={calendar:'<svg class="icon" viewBox="0 0 24 24"><path d="M8 2v4M16 2v4M3 10h18M5 4h14a2 2 0 0 1 2 2v13a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Z"/></svg>',clock:'<svg class="icon" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg>',database:'<svg class="icon" viewBox="0 0 24 24"><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5"/><path d="M4 11v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6"/></svg>',search:'<svg class="icon" viewBox="0 0 24 24"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>',play:'<svg class="icon" viewBox="0 0 24 24"><path d="M8 5v14l11-7Z"/></svg>',download:'<svg class="icon" viewBox="0 0 24 24"><path d="M12 3v12M7 10l5 5 5-5M5 21h14"/></svg>',file:'<svg class="icon" viewBox="0 0 24 24"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z"/><path d="M14 2v6h6"/></svg>',time:'<svg class="icon" viewBox="0 0 24 24"><path d="M12 6v6l4 2"/><circle cx="12" cy="12" r="9"/></svg>',size:'<svg class="icon" viewBox="0 0 24 24"><path d="M4 7h16M4 12h16M4 17h16"/></svg>'};
(function init(){mountIcons(document);const today=new Date(),weekAgo=new Date(today);weekAgo.setDate(today.getDate()-7);document.getElementById('startDate').value=fmt(weekAgo);document.getElementById('endDate').value=fmt(today);loadStorageOverview();doSearch();})();
function mountIcons(root){root.querySelectorAll('[data-icon]').forEach(el=>{el.innerHTML=icons[el.dataset.icon]||''})}function fmt(d){return d.toISOString().slice(0,10)}function text(id,v){document.getElementById(id).textContent=v}function formatDateOnly(v){return v?String(v).slice(0,10):''}function formatGB(v){v=Number(v||0);return v.toLocaleString('zh-CN',{maximumFractionDigits:v>=10?0:1})+'GB'}function formatDuration(sec){const s=Math.max(0,Number(sec||0)),h=Math.floor(s/3600),m=Math.floor((s%3600)/60),r=Math.floor(s%60);return h>0?h+':'+String(m).padStart(2,'0')+':'+String(r).padStart(2,'0'):String(m).padStart(2,'0')+':'+String(r).padStart(2,'0')}
function loadStorageOverview(){fetch('/api/storage').then(r=>{if(!r.ok)throw new Error();return r.json()}).then(renderStorageOverview).catch(()=>{text('oldestValue','暂不可用');text('oldestNote','存储信息接口请求失败，不影响录像搜索');text('retentionValue','暂无法估算');text('retentionNote','历史数据或存储配置暂不可用');text('storageValue','暂不可用');text('storageNote','存储信息暂不可用');document.getElementById('storageProgress').style.width='0%'})}
function renderStorageOverview(d){if(!d||!d.oldestVideoTime){text('oldestValue','暂无录像数据');text('oldestNote','当前存储库未找到可用录像')}else{text('oldestValue',formatDateOnly(d.oldestVideoTime));text('oldestNote','已保存约 '+(d.savedDays||1)+' 天录像'+(d.latestVideoTime?'，最新 '+d.latestVideoTime:''))}if(d&&d.estimatedRetentionDays){text('retentionValue','约 '+d.estimatedRetentionDays+' 天');text('retentionNote',(d.estimateBasis||'基于当前录像占用估算')+(d.avgGBPerDay?'，平均约 '+d.avgGBPerDay+'GB / 天':''))}else{text('retentionValue','暂无法估算');text('retentionNote','历史数据不足或平均每日占用为 0')}const used=Number(d&&d.usedGB||0),total=Number(d&&d.totalGB||0),free=Number(d&&d.freeGB||0),pct=total>0?Math.max(0,Math.min(100,used/total*100)):0;text('storageValue','已用 '+formatGB(used)+' / '+formatGB(total));text('storageNote',(d&&d.pathCount>1?'共 '+d.pathCount+' 个存储目录，':'')+'剩余 '+formatGB(free));document.getElementById('storageProgress').style.width=pct.toFixed(1)+'%'}
function doSearch(page){currentPage=page||1;const params=new URLSearchParams({start:document.getElementById('startDate').value,end:document.getElementById('endDate').value,keyword:document.getElementById('keyword').value.trim(),page:currentPage,size:pageSize});text('resultsInfo','正在搜索...');fetch('/api/videos?'+params).then(r=>r.json()).then(render).catch(e=>{text('resultsInfo','请求失败');renderEmpty('请求失败',e.message||'无法连接到 Web 服务')})}
function render(res){const list=document.getElementById('videoList'),pagi=document.getElementById('pagination');list.innerHTML='';pagi.innerHTML='';if(!res.data||res.data.length===0){text('resultsInfo','没有找到匹配记录');renderEmpty('没有找到匹配的录像','请调整日期范围或订单号关键词');return}const totalPages=Math.max(1,Math.ceil(res.total/res.pageSize));text('resultsInfo','共 '+res.total+' 条记录，第 '+res.page+' / '+totalPages+' 页');res.data.forEach(v=>list.appendChild(createVideoItem(v)));renderPagination(res.page,totalPages)}
function createVideoItem(v){const item=document.createElement('article');item.className='video-item';const orderCol=document.createElement('div'),line=document.createElement('div');line.className='order-line';const badge=document.createElement('span');badge.className='mode-badge'+(v.mode==='退货'?' return':'');badge.textContent=v.mode||'发货';const order=document.createElement('div');order.className='order-id';order.title=v.orderId||'';order.textContent=v.orderId||'未记录订单号';line.append(badge,order);const status=document.createElement('div');status.className='status-badge'+(v.exists?'':' missing');status.textContent=v.exists?'文件可用':'文件不存在';orderCol.append(line,status);const meta=document.createElement('div');meta.className='meta-grid';addMeta(meta,'calendar',v.startTime||'-');addMeta(meta,'time',v.duration||formatDuration(v.durationSec));addMeta(meta,'size',formatSize(v.sizeMB));addMeta(meta,'file',v.fileName||'-');if(v.videoCodec&&String(v.videoCodec).toLowerCase()!=='h264'){const codec=document.createElement('span');codec.className='codec-badge';codec.title='该编码会实时转码为 H.264 预览';codec.textContent=String(v.videoCodec).toUpperCase()+' 转码预览';meta.appendChild(codec)}const actions=document.createElement('div');actions.className='video-actions';const play=actionButton('play','播放','btn-play',()=>playVideo(v.id,v.orderId||v.fileName||'录像预览')),down=actionButton('download','下载','',()=>downloadVideo(v.id));if(!v.exists){play.disabled=true;down.disabled=true;play.title=down.title='文件不存在'}actions.append(play,down);item.append(orderCol,meta,actions);mountIcons(item);return item}
function addMeta(parent,icon,value){const cell=document.createElement('div');cell.className='meta-cell';const i=document.createElement('span');i.dataset.icon=icon;const s=document.createElement('span');s.className='text';s.title=value;s.textContent=value;cell.append(i,s);parent.appendChild(cell)}function actionButton(icon,label,extra,onClick){const btn=document.createElement('button');btn.type='button';btn.className='btn btn-sm '+extra;btn.innerHTML=(icons[icon]||'')+'<span></span>';btn.querySelector('span:last-child').textContent=label;btn.addEventListener('click',onClick);return btn}function formatSize(sizeMB){const mb=Number(sizeMB||0);return mb>=1024?(mb/1024).toFixed(1)+'GB':mb.toFixed(mb>=10?0:1)+'MB'}
function renderPagination(page,totalPages){const pagi=document.getElementById('pagination');if(totalPages<=1)return;const add=(label,target,active,disabled)=>{const btn=document.createElement('button');btn.type='button';btn.className='btn btn-sm page-btn'+(active?' active':'');btn.textContent=label;btn.disabled=!!disabled;btn.addEventListener('click',()=>doSearch(target));pagi.appendChild(btn)};add('上一页',Math.max(1,page-1),false,page<=1);const start=Math.max(1,Math.min(page-4,totalPages-8)),end=Math.min(totalPages,start+8);for(let i=start;i<=end;i++)add(String(i),i,i===page,false);add('下一页',Math.min(totalPages,page+1),false,page>=totalPages)}
function renderEmpty(title,sub){const list=document.getElementById('videoList');list.innerHTML='';const box=document.createElement('div');box.className='empty';const t=document.createElement('div');t.className='empty-title';t.textContent=title;const s=document.createElement('div');s.className='empty-sub';s.textContent=sub;box.append(t,s);list.appendChild(box);document.getElementById('pagination').innerHTML=''}
function playVideo(id,title){const player=document.getElementById('videoPlayer');player.src='/api/videos/'+encodeURIComponent(id)+'/play';document.getElementById('playerTitle').textContent=title;document.getElementById('playerOverlay').classList.add('active');player.play()}function closePlayer(e){if(e&&e.target!==document.getElementById('playerOverlay'))return;const player=document.getElementById('videoPlayer');player.pause();player.src='';document.getElementById('playerOverlay').classList.remove('active')}function downloadVideo(id){window.open('/api/videos/'+encodeURIComponent(id)+'/download','_blank')}document.addEventListener('keydown',e=>{if(e.key==='Escape')closePlayer()});
</script>
</body>
</html>
""";
    }
}
