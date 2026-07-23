#nullable disable
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring.Services
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
        public bool HasRefund { get; set; }
        public bool IsPrintedRefund { get; set; }
        public string RefundStatus { get; set; } = "";
        public string RefundProductInfo { get; set; } = "";
        public DateTime PushTime { get; set; } = DateTime.Now;
        public bool IsTest { get; set; }
    }

    public sealed class OrderLookupResult
    {
        public bool Responded { get; set; }
        public IReadOnlyList<OrderInfo> Orders { get; set; } = Array.Empty<OrderInfo>();
    }

    public sealed class WebServer : IDisposable
    {
        private sealed class PendingOrderLookup
        {
            public string RequestId { get; init; } = "";
            public IReadOnlyList<string> TrackingNumbers { get; init; } = Array.Empty<string>();
            public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
            public TaskCompletionSource<OrderLookupResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Claimed;
        }

        private sealed class OrderLookupResponse
        {
            public string RequestId { get; set; } = "";
            public bool Success { get; set; }
            public List<OrderInfo> Orders { get; set; }
            public string Error { get; set; } = "";
        }

        private const int MaxJsonBodyBytes = 64 * 1024;
        private const int MaxOrderInfoBodyBytes = 1024 * 1024;
        internal const int MaxOrderInfoItems = 200;
        private HttpListener _listener;
        private readonly VideoDatabase _db;
        private readonly Func<bool> _isRecordingProvider;
        private readonly Func<string> _currentRecordingFileProvider;
        private readonly Func<VideoRecord, MkvConversionResult> _mkvConverter;
        private readonly VideoClipService _clipService;
        private readonly bool _requireAccessKey;
        private readonly string _accessKey;
        private readonly Func<string> _mobileConnectionUrlProvider;
        private readonly MobileBackupService _mobileBackupService;
        private readonly MobileOrderReceiverRegistry _mobileOrderReceivers;
        private readonly ConnectedClientRegistry _connectedClients;
        private readonly string _mobileBackupComputerId;
        private readonly string _mobileBackupComputerName;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _requestSlots = new(32, 32);
        private readonly SemaphoreSlim _transcodeSlot = new(1, 1);
        private readonly FfmpegWorkLimiter _ffmpegWorkLimiter = new();
        private readonly ShortLivedSnapshotCache<byte[]> _storageOverviewCache = new(TimeSpan.FromSeconds(10));
        private Task _listenTask;
        private bool _disposed;
        private static readonly string _logPath = AppPaths.WebDebugLogPath;
        private static readonly string _transCacheDir = AppPaths.TranscodeCacheDir;
        private long _transCacheMaxBytes = 1024L * 1024 * 1024; // 默认 1GB，可config覆盖

        // SQLite 是订单信息唯一持久化来源；此字典仅用于运行时快速查询。
        private readonly Dictionary<string, OrderInfo> _orderInfoCache = new();
        private readonly object _orderInfoLock = new();
        private readonly ConcurrentDictionary<string, PendingOrderLookup> _pendingOrderLookups = new();
        private readonly SemaphoreSlim _orderLookupSignal = new(0);
        private int _activeOrderLookupPolls;
        private long _lastOrderLookupPollUtcTicks;

        /// <summary>收到油猴脚本推送的订单信息时触发，参数为本次推送的所有订单</summary>
        public event Action<List<OrderInfo>> OrderInfoReceived;
        internal event Action<IReadOnlyList<ConnectedClientInfo>> ConnectedClientsChanged;

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

        public WebServer(
            VideoDatabase db,
            int port = 5280,
            int transCacheMaxMB = 1024,
            Func<bool> isRecordingProvider = null,
            Func<VideoRecord, MkvConversionResult> mkvConverter = null,
            Func<string> currentRecordingFileProvider = null,
            bool requireAccessKey = false,
            string accessKey = null,
            string listenerHost = "+",
            Func<string> mobileConnectionUrlProvider = null,
            string mobileBackupComputerId = null,
            string mobileBackupComputerName = null,
            string mobileBackupStateDirectory = null,
            Func<string> mobileBackupRecordingRootResolver = null)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _isRecordingProvider = isRecordingProvider ?? (() => false);
            _currentRecordingFileProvider = currentRecordingFileProvider ?? (() => null);
            _mkvConverter = mkvConverter;
            _requireAccessKey = requireAccessKey;
            _accessKey = accessKey?.Trim() ?? "";
            _mobileConnectionUrlProvider = mobileConnectionUrlProvider ?? (() => "");
            _mobileBackupComputerId = mobileBackupComputerId?.Trim() ?? "";
            _mobileBackupComputerName = string.IsNullOrWhiteSpace(mobileBackupComputerName)
                ? Environment.MachineName
                : mobileBackupComputerName.Trim();
            _clipService = new VideoClipService(
                _db,
                WriteLog,
                _mkvConverter,
                IsCurrentRecordingFile,
                () => Task.Run(CleanWebCache),
                _ffmpegWorkLimiter);
            Port = port;
            _transCacheMaxBytes = (long)transCacheMaxMB * 1024 * 1024;
            _listener = CreateListener(port, listenerHost);
            MigrateLegacyOrderInfoCache();
            LoadOrderInfoCacheFromDatabase();
            string resolvedMobileBackupStateDirectory = mobileBackupStateDirectory
                ?? Path.Combine(AppPaths.CacheDir, "mobile-backup");
            _mobileBackupService = new MobileBackupService(
                _db,
                resolvedMobileBackupStateDirectory,
                mobileBackupRecordingRootResolver ?? (() => Path.Combine(AppPaths.UserDataDir, "mobile-backup-recordings")),
                GetOrderInfo);
            _mobileOrderReceivers = new MobileOrderReceiverRegistry(
                Path.Combine(resolvedMobileBackupStateDirectory, "order-receivers.json"));
            _connectedClients = new ConnectedClientRegistry();
            _connectedClients.Changed += clients =>
            {
                try { ConnectedClientsChanged?.Invoke(clients); } catch { }
            };
        }

        private static HttpListener CreateListener(int port, string listenerHost)
        {
            string host = string.Equals(listenerHost, "127.0.0.1", StringComparison.Ordinal)
                ? listenerHost
                : "+";
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{host}:{port}/");
            return listener;
        }

        public void Start(bool allowAccessSetup = false)
        {
            bool accessConfigured = false;
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                if (ex.ErrorCode != 5)
                    throw new InvalidOperationException($"Web 服务监听 http://+:{Port}/ 失败，请检查端口是否被占用。", ex);

                if (!allowAccessSetup)
                {
                    throw new InvalidOperationException(
                        "Web 服务缺少监听权限。请打开设置，在“局域网查看”中直接保存，以完成管理员授权。",
                        ex);
                }

                // 只有用户明确保存局域网设置时，才请求管理员权限并重试
                ConfigureLanAccess(Port, includeUrlAcl: true);
                accessConfigured = true;
                try { _listener.Close(); } catch { }
                _listener = CreateListener(Port, "+");
                try
                {
                    _listener.Start();
                }
                catch (HttpListenerException retryException)
                {
                    throw new InvalidOperationException($"Web 服务监听 http://+:{Port}/ 失败，请检查端口占用、URL ACL 或防火墙权限。", retryException);
                }
            }

            if (allowAccessSetup && !accessConfigured && !HasExpectedFirewallRule(Port))
            {
                try
                {
                    ConfigureLanAccess(Port, includeUrlAcl: false);
                }
                catch
                {
                    try { _listener.Stop(); } catch { }
                    throw;
                }
            }
            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
        }

        /// <summary>
        /// 注册 URL ACL 和防火墙规则，需要管理员权限时会弹出 UAC 提示。
        /// </summary>
        private static void ConfigureLanAccess(int port, bool includeUrlAcl)
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string userSid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(userSid))
                throw new InvalidOperationException("无法获取当前用户 SID，不能配置局域网服务监听权限。");

            RunElevatedCmd(BuildAccessSetupCommand(port, userSid, includeUrlAcl), "配置局域网服务访问权限");
        }

        internal const string FirewallRuleName = "快递打包监控 Web服务";

        internal static string BuildAccessSetupCommand(int port, string userSid, bool includeUrlAcl = true)
        {
            if (port <= 0 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrWhiteSpace(userSid))
                throw new ArgumentException("用户 SID 不能为空。", nameof(userSid));

            string url = $"http://+:{port}/";
            string firewallCommand = BuildFirewallSetupCommand(port);
            if (!includeUrlAcl)
                return firewallCommand;

            string urlAclCommand = $"(netsh http delete urlacl url={url} >nul 2>&1 & "
                + $"netsh http add urlacl url={url} sddl=\"D:(A;;GX;;;{userSid})\")";
            return $"{urlAclCommand} && ({firewallCommand})";
        }

        internal static string BuildFirewallSetupCommand(int port)
        {
            if (port <= 0 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));

            return $"netsh advfirewall firewall delete rule name=\"{FirewallRuleName}\" >nul 2>&1 & "
                + $"netsh advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port}";
        }

        internal static bool HasExpectedFirewallRule(int port)
        {
            if (port <= 0 || port > 65535)
                return false;

            object policyObject = null;
            try
            {
                Type policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null)
                    return false;

                policyObject = Activator.CreateInstance(policyType);
                dynamic policy = policyObject;
                foreach (dynamic rule in policy.Rules)
                {
                    try
                    {
                        if (string.Equals((string)rule.Name, FirewallRuleName, StringComparison.Ordinal)
                            && (bool)rule.Enabled
                            && (int)rule.Direction == 1
                            && (int)rule.Action == 1
                            && (int)rule.Protocol == 6
                            && (((int)rule.Profiles & 7) == 7 || (int)rule.Profiles == int.MaxValue)
                            && FirewallPortsContain((string)rule.LocalPorts, port))
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // 忽略无法读取的第三方或损坏规则，继续检查同名规则。
                    }
                    finally
                    {
                        try
                        {
                            if (Marshal.IsComObject(rule))
                                Marshal.FinalReleaseComObject(rule);
                        }
                        catch { }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Web", $"Unable to inspect Windows Firewall rule: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (policyObject != null && Marshal.IsComObject(policyObject))
                        Marshal.FinalReleaseComObject(policyObject);
                }
                catch { }
            }
        }

        internal static bool FirewallPortsContain(string localPorts, int port)
        {
            if (string.IsNullOrWhiteSpace(localPorts))
                return false;

            foreach (string entry in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (entry is "*" or "Any")
                    return true;
                if (int.TryParse(entry, out int singlePort) && singlePort == port)
                    return true;

                string[] range = entry.Split('-', 2, StringSplitOptions.TrimEntries);
                if (range.Length == 2
                    && int.TryParse(range[0], out int start)
                    && int.TryParse(range[1], out int end)
                    && port >= start
                    && port <= end)
                {
                    return true;
                }
            }
            return false;
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
                using var proc = Process.Start(psi);
                if (proc == null)
                    throw new InvalidOperationException($"{actionName}失败：无法启动管理员命令。");

                if (!proc.WaitForExit(15000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new TimeoutException($"{actionName}超时，请手动以管理员身份运行 netsh 或关闭 Web 服务。");
                }

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
                    try
                    {
                        await _requestSlots.WaitAsync(token).ConfigureAwait(false);
                    }
                    catch
                    {
                        try { ctx.Response.Abort(); } catch { }
                        throw;
                    }

                    try
                    {
                        _ = Task.Run(() =>
                        {
                            try { HandleRequest(ctx); }
                            finally { _requestSlots.Release(); }
                        });
                    }
                    catch
                    {
                        _requestSlots.Release();
                        try { ctx.Response.Abort(); } catch { }
                        throw;
                    }
                }
                catch (OperationCanceledException) { break; }
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
                ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, OPTIONS");
                ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Content-Range, X-EPM-Access-Key, X-Chunk-SHA256");

                if (method == "POST")
                {
                    int maxBodyBytes = path is "/api/orderinfo" or "/api/order-lookup/result"
                        ? MaxOrderInfoBodyBytes
                        : MaxJsonBodyBytes;
                    if (ctx.Request.ContentLength64 > maxBodyBytes)
                    {
                        SendJson(ctx, 413, new { error = $"请求内容过大，最大允许 {maxBodyBytes / 1024} KB" });
                        return;
                    }
                }

                if (method == "PUT" && IsMobileBackupPath(path)
                    && ctx.Request.ContentLength64 > MobileBackupService.ChunkSizeBytes)
                {
                    SendJson(ctx, 413, new { errorCode = "chunk_too_large", error = "分块超过服务端上限" });
                    return;
                }

                if (method == "OPTIONS")
                {
                    ctx.Response.StatusCode = 204;
                    ctx.Response.OutputStream.Close();
                    return;
                }

                if (IsMobileBackupPath(path) && !TryAuthorizeMobileBackupRequest(ctx, out bool missingBackupKey))
                {
                    SendJson(ctx, missingBackupKey ? 401 : 403, new
                    {
                        errorCode = missingBackupKey ? "pairing_required" : "access_key_invalid",
                        error = missingBackupKey ? "需要重新配对" : "访问密钥无效，请重新配对"
                    });
                    return;
                }

                if (_requireAccessKey && RequiresAccessKey(path))
                {
                    bool authorized = TryAuthorizeRequest(ctx, out bool authorizedByQuery);
                    if (!authorized)
                    {
                        SendUnauthorized(ctx, path);
                        return;
                    }

                    if (authorizedByQuery && path == "")
                    {
                        ctx.Response.StatusCode = 302;
                        ctx.Response.RedirectLocation = "/";
                        ctx.Response.OutputStream.Close();
                        return;
                    }
                }

                switch (path)
                {
                    case "" or "/":
                        ServeIndexPage(ctx);
                        break;
                    case "/api/videos":
                        HandleSearchVideos(ctx);
                        break;
                    case "/api/videos/status" when method == "GET":
                        HandleVideoStatuses(ctx);
                        break;
                    case "/api/storage":
                        HandleStorageOverview(ctx);
                        break;
                    case "/api/mobile-connection" when method == "GET":
                        HandleMobileConnection(ctx);
                        break;
                    case "/api/mobile-backup/capabilities" when method == "GET":
                        HandleMobileBackupCapabilities(ctx);
                        break;
                    case "/api/mobile-backup/uploads" when method == "POST":
                        HandleCreateMobileBackupUpload(ctx);
                        break;
                    case "/api/connections/heartbeat" when method == "POST":
                        HandleConnectionHeartbeat(ctx);
                        break;
                    case var p when method == "GET" && p.StartsWith("/api/clip-tasks/") && !p.EndsWith("/cancel"):
                        HandleGetClipTask(ctx, path);
                        break;
                    case var p when method == "POST" && p.StartsWith("/api/clip-tasks/") && p.EndsWith("/cancel"):
                        HandleCancelClipTask(ctx, path);
                        break;
                    case var p when method == "GET" && p.StartsWith("/api/clips/"):
                        HandleServeClip(ctx, path);
                        break;
                    case var p when method == "GET" && p.StartsWith("/api/clip-previews/"):
                        HandleServeClipPreview(ctx, path);
                        break;
                    case "/kuaidizs-install-guide":
                        ServeInstallGuidePage(ctx);
                        break;
                    case "/kuaidizs-order-push.user.js":
                        ServeUserscript(ctx);
                        break;
                    case "/api/orderinfo":
                        if (method == "POST")
                            HandlePushOrderInfo(ctx);
                        else
                            HandleQueryOrderInfo(ctx);
                        break;
                    case "/api/order-lookup/pending" when method == "GET":
                        HandlePollOrderLookup(ctx);
                        break;
                    case "/api/order-lookup/result" when method == "POST":
                        HandleOrderLookupResult(ctx);
                        break;
                    default:
                        if (method == "PUT" && IsMobileBackupUploadPath(path, "/chunks", out string chunkUploadId))
                            HandleMobileBackupChunk(ctx, chunkUploadId);
                        else if (method == "POST" && IsMobileBackupUploadPath(path, "/complete", out string completeUploadId))
                            HandleCompleteMobileBackupUpload(ctx, completeUploadId);
                        else if (method == "HEAD" && path.StartsWith("/api/videos/") && path.EndsWith("/play"))
                        {
                            // HEAD 请求只返回 headers，不启动转码/传输
                            ctx.Response.ContentType = "video/mp4";
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentLength64 = 0;
                            ctx.Response.OutputStream.Close();
                        }
                        else if (path.StartsWith("/api/videos/") && path.EndsWith("/download"))
                            HandleDownload(ctx, path);
                        else if (method == "POST" && path.StartsWith("/api/videos/") && path.EndsWith("/clip/prewarm"))
                            HandleClipPrewarm(ctx, path);
                        else if (method == "POST" && path.StartsWith("/api/videos/") && path.EndsWith("/clip/timeline"))
                            HandleClipTimeline(ctx, path);
                        else if (method == "POST" && path.StartsWith("/api/videos/") && path.EndsWith("/clip/frame"))
                            HandleClipFrame(ctx, path);
                        else if (method == "POST" && path.StartsWith("/api/videos/") && path.EndsWith("/clip/preview"))
                            HandleClipPreview(ctx, path);
                        else if (method == "POST" && path.StartsWith("/api/videos/") && path.EndsWith("/clip"))
                            HandleStartClip(ctx, path);
                        else if (method == "GET" && path.StartsWith("/api/videos/") && path.EndsWith("/thumbnail"))
                            HandleVideoThumbnail(ctx, path);
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
            if (string.Equals(
                    ctx.Request.Headers["Access-Control-Request-Private-Network"],
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            }
        }

        internal static bool RequiresAccessKey(string path)
        {
            return path == ""
                || path.StartsWith("/api/videos", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/clip", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/mobile-connection", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsMobileBackupPath(string path) =>
            path?.StartsWith("/api/mobile-backup", StringComparison.OrdinalIgnoreCase) == true;

        private bool TryAuthorizeMobileBackupRequest(HttpListenerContext ctx, out bool missingKey)
        {
            string headerKey = ctx.Request.Headers["X-EPM-Access-Key"];
            missingKey = string.IsNullOrWhiteSpace(headerKey);
            bool authorized = !missingKey && AccessKeysEqual(headerKey, _accessKey);
            if (authorized)
                _mobileOrderReceivers.Register(ctx.Request.RemoteEndPoint?.Address);
            return authorized;
        }

        private static bool IsMobileBackupUploadPath(string path, string suffix, out string uploadId)
        {
            uploadId = "";
            const string prefix = "/api/mobile-backup/uploads/";
            if (path == null
                || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int length = path.Length - prefix.Length - suffix.Length;
            if (length <= 0) return false;
            uploadId = path.Substring(prefix.Length, length).Trim('/');
            return uploadId.Length > 0 && !uploadId.Contains('/');
        }

        private void HandleMobileBackupCapabilities(HttpListenerContext ctx)
        {
            SendJson(ctx, 200, new
            {
                protocol = MobileBackupService.ProtocolVersion,
                version = 1,
                computerId = _mobileBackupComputerId,
                computerName = _mobileBackupComputerName,
                maxChunkBytes = MobileBackupService.ChunkSizeBytes,
                supportedFormats = new[] { "video/mp4" },
                features = new
                {
                    videoLibrary = true,
                    cursorVideoLibrary = true,
                    rangePlayback = true,
                    multipleSessionsPerFile = true
                },
                retryPolicy = new
                {
                    chunkMaxAttempts = 5,
                    chunkBackoffSeconds = new[] { 1, 2, 4, 8, 16 },
                    fileMaxAttempts = 3
                }
            });
        }

        private void HandleConnectionHeartbeat(HttpListenerContext ctx)
        {
            try
            {
                ConnectedClientHeartbeat heartbeat = ReadJsonBody<ConnectedClientHeartbeat>(ctx);
                string remoteAddress = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
                _connectedClients.Heartbeat(heartbeat, remoteAddress);
                SendJson(ctx, 200, new
                {
                    ok = true,
                    heartbeatIntervalSeconds = ConnectedClientRegistry.HeartbeatIntervalSeconds,
                    expiresInSeconds = ConnectedClientRegistry.ExpirationSeconds
                });
            }
            catch (ConnectedClientValidationException ex)
            {
                int statusCode = ex.ErrorCode is "connection_registry_full" or "too_many_clients" ? 429 : 400;
                SendJson(ctx, statusCode, new { errorCode = ex.ErrorCode, error = ex.Message });
            }
            catch (JsonException ex)
            {
                SendJson(ctx, 400, new { errorCode = "invalid_json", error = ex.Message });
            }
        }

        internal IReadOnlyList<ConnectedClientInfo> GetConnectedClients() => _connectedClients.GetSnapshot();

        private void HandleCreateMobileBackupUpload(HttpListenerContext ctx)
        {
            try
            {
                MobileBackupCreateResult result = _mobileBackupService.CreateOrResume(ReadJsonBody<MobileBackupCreateRequest>(ctx));
                SendJson(ctx, 200, new
                {
                    uploadId = result.UploadId,
                    offset = result.Offset,
                    chunkSize = result.ChunkSize,
                    fileReady = result.FileReady
                });
            }
            catch (Exception ex)
            {
                SendMobileBackupError(ctx, ex);
            }
        }

        private void HandleMobileBackupChunk(HttpListenerContext ctx, string uploadId)
        {
            try
            {
                if (!TryParseContentRange(ctx.Request.Headers["Content-Range"], out long start, out long end, out long total))
                    throw new MobileBackupValidationException("invalid_content_range", "Content-Range 格式应为 bytes start-end/total");
                string chunkSha256 = ctx.Request.Headers["X-Chunk-SHA256"] ?? "";
                byte[] content = ReadRequestBytes(ctx, MobileBackupService.ChunkSizeBytes);
                long offset = _mobileBackupService.AppendChunk(uploadId, start, end, total, content, chunkSha256);
                SendJson(ctx, 200, new { uploadId, offset });
            }
            catch (Exception ex)
            {
                SendMobileBackupError(ctx, ex);
            }
        }

        private void HandleCompleteMobileBackupUpload(HttpListenerContext ctx, string uploadId)
        {
            try
            {
                MobileBackupCompleteResult result = _mobileBackupService.Complete(uploadId, ReadJsonBody<MobileBackupCompleteRequest>(ctx));
                SendJson(ctx, 200, new
                {
                    status = result.Status,
                    fileSha256 = result.FileSha256,
                    recordId = result.RecordId,
                    recordIds = result.RecordIds,
                    alreadyCompleted = result.AlreadyCompleted,
                    message = "电脑校验完成，备份成功"
                });
            }
            catch (Exception ex)
            {
                SendMobileBackupError(ctx, ex);
            }
        }

        private static void SendMobileBackupError(HttpListenerContext ctx, Exception exception)
        {
            switch (exception)
            {
                case MobileBackupOffsetException offset:
                    SendJson(ctx, 409, new
                    {
                        errorCode = "offset_mismatch",
                        error = offset.Message,
                        expectedOffset = offset.ExpectedOffset
                    });
                    break;
                case MobileBackupFileHashException fileHash:
                    SendJson(ctx, 422, new
                    {
                        errorCode = "sha256_mismatch",
                        error = fileHash.Message,
                        expectedOffset = 0,
                        retryWholeFile = true,
                        maxFileAttempts = 3
                    });
                    break;
                case MobileBackupValidationException validation:
                    int statusCode = validation.ErrorCode == "upload_not_found" ? 404
                        : validation.ErrorCode.Contains("sha256_mismatch", StringComparison.Ordinal) ? 422
                        : 400;
                    SendJson(ctx, statusCode, new { errorCode = validation.ErrorCode, error = validation.Message });
                    break;
                case JsonException json:
                    SendJson(ctx, 400, new { errorCode = "invalid_json", error = json.Message });
                    break;
                case InvalidDataException invalidData:
                    SendJson(ctx, 400, new { errorCode = "invalid_request", error = invalidData.Message });
                    break;
                default:
                    SendJson(ctx, 500, new { errorCode = "mobile_backup_failed", error = exception.Message });
                    break;
            }
        }

        internal static bool TryParseContentRange(string value, out long start, out long end, out long total)
        {
            start = end = total = 0;
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
                return false;
            string[] rangeAndTotal = value[6..].Split('/', 2);
            if (rangeAndTotal.Length != 2) return false;
            string[] bounds = rangeAndTotal[0].Split('-', 2);
            return bounds.Length == 2
                && long.TryParse(bounds[0], out start)
                && long.TryParse(bounds[1], out end)
                && long.TryParse(rangeAndTotal[1], out total)
                && start >= 0 && end >= start && total > end;
        }

        private void HandleMobileConnection(HttpListenerContext ctx)
        {
            string url;
            try
            {
                url = _mobileConnectionUrlProvider()?.Trim() ?? "";
            }
            catch (Exception ex)
            {
                SendJson(ctx, 503, new { error = $"手机连接网址暂不可用: {ex.Message}" });
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed)
                || parsed.IsLoopback
                || !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                SendJson(ctx, 503, new { error = "监控端尚未准备好可供手机访问的局域网网址" });
                return;
            }

            SendJson(ctx, 200, new
            {
                url,
                qrCode = MobileConnectionService.CreateQrDataUri(url),
                accessProtected = _requireAccessKey
            });
        }

        private bool TryAuthorizeRequest(HttpListenerContext ctx, out bool authorizedByQuery)
        {
            authorizedByQuery = false;
            if (string.IsNullOrWhiteSpace(_accessKey)) return false;

            string headerKey = ctx.Request.Headers["X-EPM-Access-Key"];
            if (AccessKeysEqual(headerKey, _accessKey))
                return true;

            string queryKey = ctx.Request.QueryString["key"];
            if (AccessKeysEqual(queryKey, _accessKey))
            {
                authorizedByQuery = true;
                SetAccessCookie(ctx);
                return true;
            }

            string cookieValue = ctx.Request.Cookies["EPM_WEB_ACCESS"]?.Value;
            return AccessKeysEqual(cookieValue, ComputeAccessCookieValue(_accessKey));
        }

        private void SetAccessCookie(HttpListenerContext ctx)
        {
            var cookie = new Cookie("EPM_WEB_ACCESS", ComputeAccessCookieValue(_accessKey), "/")
            {
                HttpOnly = true,
                Expires = DateTime.UtcNow.AddDays(30)
            };
            ctx.Response.SetCookie(cookie);
        }

        internal static bool AccessKeysEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            byte[] leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
            byte[] rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
            return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
        }

        private static string ComputeAccessCookieValue(string accessKey)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKey))).ToLowerInvariant();
        }

        private static void SendUnauthorized(HttpListenerContext ctx, string path)
        {
            if (path == "")
            {
                const string html = """
<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8"><title>需要访问链接</title></head>
<body style="font-family:Microsoft YaHei UI,sans-serif;padding:32px;color:#172033">
<h1>此监控网页已启用访问保护</h1><p>请在监控端点击“复制并打开监控网页”，使用复制的完整链接访问。</p>
</body></html>
""";
                byte[] bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
                return;
            }

            SendJson(ctx, 401, new { error = "需要有效的监控网页访问链接" });
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
                string body = ReadRequestBody(ctx, MaxOrderInfoBodyBytes);
                var items = JsonSerializer.Deserialize<List<OrderInfo>>(body, _jsonOptions);
                if (items == null || items.Count == 0)
                {
                    SendJson(ctx, 400, new { error = "空数据" });
                    return;
                }

                ValidateOrderInfoItems(items);

                var realItems = items.Where(x => !x.IsTest).ToList();
                var testItems = items.Where(x => x.IsTest).ToList();
                int count = StoreOrderInfos(realItems, preserveConfirmedRefund: true);

                if (EnableOrderInfoLog)
                {
                    Log($"HandlePushOrderInfo: 接收 {count} 条订单信息, 测试={testItems.Count}, 缓存总数={_orderInfoCache.Count}");
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrWhiteSpace(item.TrackingNumber))
                            Log($"  订单: 运单号={item.TrackingNumber}, 订单号={item.OrderId}, 测试={item.IsTest}, 打印后退款={item.IsPrintedRefund}, 退款状态=[{item.RefundStatus}], 买家留言=[{item.BuyerMessage}], 卖家备注=[{item.SellerMemo}], 商品=[{item.ProductInfo}]");
                    }
                }

                // 通知订阅方预生成语音缓存
                try { OrderInfoReceived?.Invoke(items); } catch { }

                SendJson(ctx, 200, new { ok = true, count, testCount = testItems.Count });
            }
            catch (Exception ex)
            {
                Log($"HandlePushOrderInfo 异常: {ex.Message}");
                SendJson(ctx, 400, new { error = ex.Message });
            }
        }

        private int StoreOrderInfos(List<OrderInfo> items, bool preserveConfirmedRefund)
        {
            int count = 0;
            if (items == null || items.Count == 0) return count;

            lock (_orderInfoLock)
            {
                DateTime cutoff = DateTime.Now.Subtract(VideoDatabase.OrderInfoRetention);
                foreach (string expiredKey in _orderInfoCache
                    .Where(x => x.Value.PushTime < cutoff)
                    .Select(x => x.Key)
                    .ToList())
                {
                    _orderInfoCache.Remove(expiredKey);
                }

                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.TrackingNumber)) continue;
                    string key = item.TrackingNumber.Trim().ToUpperInvariant();
                    if (preserveConfirmedRefund && _orderInfoCache.TryGetValue(key, out var existing) && existing.IsPrintedRefund && !item.IsPrintedRefund)
                    {
                        // 普通页面的旧 DOM 不覆盖已确认退款；扫码触发的实时查询可以覆盖。
                        item.HasRefund = true;
                        item.IsPrintedRefund = true;
                        if (string.IsNullOrWhiteSpace(item.RefundStatus))
                            item.RefundStatus = existing.RefundStatus;
                        if (string.IsNullOrWhiteSpace(item.RefundProductInfo))
                            item.RefundProductInfo = existing.RefundProductInfo;
                    }
                    item.PushTime = DateTime.Now;
                    _orderInfoCache[key] = item;
                    count++;
                }

                if (_orderInfoCache.Count > VideoDatabase.MaxOrderInfoRecords)
                {
                    foreach (string overflowKey in _orderInfoCache
                        .OrderByDescending(x => x.Value.PushTime)
                        .Skip(VideoDatabase.MaxOrderInfoRecords)
                        .Select(x => x.Key)
                        .ToList())
                    {
                        _orderInfoCache.Remove(overflowKey);
                    }
                }
            }

            if (count > 0)
            {
                _db.UpsertOrderInfos(items);
                _db.CleanupExpiredOrderInfos();
                _db.UpdateRecentVideoOrderInfos(items);
            }
            return count;
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
                        info.ProductInfo,
                        info.HasRefund,
                        info.IsPrintedRefund,
                        info.RefundStatus,
                        info.RefundProductInfo
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
                        Log($"GetOrderInfo 命中: {key} => 打印后退款={info.IsPrintedRefund}, 退款状态=[{info.RefundStatus}], 买家留言=[{info.BuyerMessage}], 卖家备注=[{info.SellerMemo}], 商品=[{info.ProductInfo}]");
                    return info;
                }
                if (EnableOrderInfoLog)
                    Log($"GetOrderInfo 未命中: {key}, 缓存总数={_orderInfoCache.Count}");
                return null;
            }
        }

        public bool HasActiveOrderLookupClient
        {
            get
            {
                long ticks = Volatile.Read(ref _lastOrderLookupPollUtcTicks);
                return Volatile.Read(ref _activeOrderLookupPolls) > 0 ||
                    (ticks > 0 && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromSeconds(5));
            }
        }

        public async Task<OrderLookupResult> RequestFreshOrderSnapshotAsync(TimeSpan timeout, IEnumerable<string> trackingNumbers = null)
        {
            if (!HasActiveOrderLookupClient)
                return new OrderLookupResult { Responded = false };

            CleanupExpiredOrderLookups();
            var pending = new PendingOrderLookup
            {
                RequestId = Guid.NewGuid().ToString("N"),
                TrackingNumbers = (trackingNumbers ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(50)
                    .ToArray()
            };
            _pendingOrderLookups[pending.RequestId] = pending;
            _orderLookupSignal.Release();

            Task completed = await Task.WhenAny(pending.Completion.Task, Task.Delay(timeout));
            _pendingOrderLookups.TryRemove(pending.RequestId, out _);
            return completed == pending.Completion.Task
                ? await pending.Completion.Task
                : new OrderLookupResult { Responded = false };
        }

        private void HandlePollOrderLookup(HttpListenerContext ctx)
        {
            Interlocked.Increment(ref _activeOrderLookupPolls);
            Interlocked.Exchange(ref _lastOrderLookupPollUtcTicks, DateTime.UtcNow.Ticks);
            try
            {
                CleanupExpiredOrderLookups();
                PendingOrderLookup pending = ClaimNextOrderLookup();
                if (pending == null)
                {
                    try { _orderLookupSignal.Wait(TimeSpan.FromSeconds(20), _cts.Token); }
                    catch (OperationCanceledException) { }
                    CleanupExpiredOrderLookups();
                    pending = ClaimNextOrderLookup();
                }

                if (pending == null)
                {
                    SendJson(ctx, 200, new { pending = false });
                    return;
                }

                SendJson(ctx, 200, new
                {
                    pending = true,
                    requestId = pending.RequestId,
                    trackingNumbers = pending.TrackingNumbers
                });
            }
            finally
            {
                Interlocked.Decrement(ref _activeOrderLookupPolls);
                Interlocked.Exchange(ref _lastOrderLookupPollUtcTicks, DateTime.UtcNow.Ticks);
            }
        }

        private PendingOrderLookup ClaimNextOrderLookup()
        {
            return _pendingOrderLookups.Values
                .OrderBy(x => x.CreatedAtUtc)
                .FirstOrDefault(x => Interlocked.CompareExchange(ref x.Claimed, 1, 0) == 0);
        }

        private void HandleOrderLookupResult(HttpListenerContext ctx)
        {
            try
            {
                string body = ReadRequestBody(ctx, MaxOrderInfoBodyBytes);
                var response = JsonSerializer.Deserialize<OrderLookupResponse>(body, _jsonOptions)
                    ?? throw new InvalidDataException("请求内容无效");
                if (string.IsNullOrWhiteSpace(response.RequestId) ||
                    !_pendingOrderLookups.TryGetValue(response.RequestId, out var pending))
                {
                    SendJson(ctx, 404, new { error = "核验请求已过期" });
                    return;
                }

                if (!response.Success)
                {
                    pending.Completion.TrySetResult(new OrderLookupResult { Responded = false });
                    SendJson(ctx, 200, new { ok = true, responded = false, error = response.Error ?? "打印端查询失败" });
                    return;
                }

                if (response.Orders == null)
                    throw new InvalidDataException("订单快照不能为空");

                foreach (OrderInfo info in response.Orders)
                    info.TrackingNumber = info.TrackingNumber?.Trim().ToUpperInvariant() ?? "";

                ValidateOrderInfoItems(response.Orders);
                StoreOrderInfos(response.Orders, preserveConfirmedRefund: false);
                try { OrderInfoReceived?.Invoke(response.Orders); } catch { }

                pending.Completion.TrySetResult(new OrderLookupResult
                {
                    Responded = true,
                    Orders = response.Orders
                });
                SendJson(ctx, 200, new { ok = true, responded = true, count = response.Orders.Count, error = response.Error ?? "" });
            }
            catch (Exception ex)
            {
                Log($"HandleOrderLookupResult 异常: {ex.Message}");
                SendJson(ctx, 400, new { error = ex.Message });
            }
        }

        private void CleanupExpiredOrderLookups()
        {
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-30);
            foreach (var entry in _pendingOrderLookups)
            {
                if (entry.Value.CreatedAtUtc >= cutoff) continue;
                if (_pendingOrderLookups.TryRemove(entry.Key, out var expired))
                    expired.Completion.TrySetResult(new OrderLookupResult { Responded = false });
            }
        }

        // ───── 从唯一持久化来源 SQLite 恢复运行时缓存 ─────
        private void MigrateLegacyOrderInfoCache()
        {
            string path = AppPaths.OrderInfoCachePath;
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var items = JsonSerializer.Deserialize<List<OrderInfo>>(json, _jsonOptions) ?? new List<OrderInfo>();
                DateTime cutoff = DateTime.Now.Subtract(VideoDatabase.OrderInfoRetention);
                items = items
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.TrackingNumber) && x.PushTime >= cutoff)
                    .ToList();
                if (items.Count > 0)
                    _db.UpsertOrderInfos(items);
                _db.CleanupExpiredOrderInfos();

                File.Delete(path);
                Debug.WriteLine($"[WebServer] 已迁移并删除旧 JSON 订单缓存，共 {items.Count} 条");
            }
            catch (Exception ex)
            {
                // 迁移失败时保留旧文件，数据库仍可独立工作，避免启动失败或数据丢失。
                Debug.WriteLine($"[WebServer] 迁移旧 JSON 订单缓存失败: {ex.Message}");
            }
        }

        private void LoadOrderInfoCacheFromDatabase()
        {
            try
            {
                List<OrderInfo> items = _db.GetRecentOrderInfos();
                lock (_orderInfoLock)
                {
                    foreach (var item in items)
                    {
                        if (string.IsNullOrWhiteSpace(item.TrackingNumber)) continue;
                        string key = item.TrackingNumber.Trim().ToUpperInvariant();
                        _orderInfoCache[key] = item;
                    }
                }
                Debug.WriteLine($"[WebServer] 从数据库恢复 {_orderInfoCache.Count} 条订单信息缓存");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebServer] 从数据库加载订单缓存失败: {ex.Message}");
            }
        }

        // ───── API: 搜索视频 ─────
        private void HandleStorageOverview(HttpListenerContext ctx)
        {
            try
            {
                byte[] responseBytes = _storageOverviewCache.GetOrCreate(BuildStorageOverviewResponse);
                SendJsonBytes(ctx, 200, responseBytes);
            }
            catch (Exception ex)
            {
                Log($"HandleStorageOverview 异常: {ex.Message}");
                SendJson(ctx, 500, new { errorCode = "storage_unavailable", error = "存储信息暂不可用" });
            }
        }

        private byte[] BuildStorageOverviewResponse()
        {
            var config = LoadAppConfig();
            var locations = config.StorageLocations?
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .OrderBy(x => x.Priority)
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

            return JsonSerializer.SerializeToUtf8Bytes(new
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
            }, _jsonOptions);
        }

        private static AppConfig LoadAppConfig()
        {
            try
            {
                string configPath = AppPaths.ConfigPath;
                if (!File.Exists(configPath))
                {
                    var defaultConfig = new AppConfig();
                    AppConfig.NormalizeAfterLoad(defaultConfig);
                    return defaultConfig;
                }

                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath)) ?? new AppConfig();
                AppConfig.NormalizeAfterLoad(config);
                return config;
            }
            catch
            {
                var config = new AppConfig();
                AppConfig.NormalizeAfterLoad(config);
                return config;
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
            long capacityBytes = 0;
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
                            long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(loc, drive);
                            capacityBytes = Math.Max(0, drive.AvailableFreeSpace - reserveBytes)
                                + GetDirectoryVideoBytes(normalizedPath);
                        }
                    }
                }
            }
            catch { }

            return new StoragePathInfo
            {
                Path = normalizedPath,
                DisplayPath = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                TotalBytes = Math.Max(0, capacityBytes),
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
            string deviceId = qs["deviceId"] ?? "";

            var result = _db.QueryVideosPaged(startDate, endDate, string.IsNullOrWhiteSpace(keyword) ? null : keyword, page, pageSize);
            int deviceTotal = _db.CountVideosForDevice(startDate, endDate, string.IsNullOrWhiteSpace(keyword) ? null : keyword, deviceId);
            // SQL 层只取当前页，文件存在性仅对当前页记录检查。
            var paged = result.Records.Select(r => new
            {
                r.Id,
                r.OrderId,
                trackingNumber = r.TrackingNumber ?? "",
                sourceOrderId = r.SourceOrderId ?? "",
                buyerMessage = r.BuyerMessage ?? "",
                sellerMemo = r.SellerMemo ?? "",
                productInfo = r.ProductInfo ?? "",
                orderInfoPushTime = r.OrderInfoPushTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                orderInfo = DeserializeOrderInfoSnapshot(r.OrderInfoJson),
                r.Mode,
                r.FileName,
                filePath = r.FilePath ?? "",
                videoCodec = r.VideoCodec ?? "",
                sourceType = r.SourceType ?? "pc",
                sourceDeviceId = r.SourceDeviceId ?? "",
                sourceDeviceName = r.SourceDeviceName ?? "",
                sourceSessionId = r.SourceSessionId ?? "",
                contentSha256 = r.ContentSha256 ?? "",
                sizeMB = Math.Round(r.FileSizeBytes / 1048576.0, 1),
                startTime = r.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                durationSec = Math.Round(r.DurationSeconds, 0),
                duration = TimeSpan.FromSeconds(r.DurationSeconds).ToString(@"mm\:ss"),
                exists = File.Exists(r.FilePath),
                playUrl = $"/api/videos/{r.Id}/play?compat=0",
                thumbnailUrl = $"/api/videos/{r.Id}/thumbnail",
                remote = true
            });

            SendJson(ctx, 200, new { total = result.Total, deviceTotal, page, pageSize, data = paged });
        }

        private void HandleVideoStatuses(HttpListenerContext ctx)
        {
            long[] ids = (ctx.Request.QueryString["ids"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => long.TryParse(value, out long id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .Take(100)
                .ToArray();
            var records = _db.QueryVideoStatuses(ids);
            var data = ids.Select(id =>
            {
                records.TryGetValue(id, out VideoRecord record);
                bool exists = record != null && File.Exists(record.FilePath);
                string status = record == null || (!record.IsDeleted && !exists)
                    ? "missing"
                    : record.IsDeleted ? "deleted" : "available";
                string reason = record == null
                    ? "记录不存在"
                    : record.IsDeleted
                        ? (string.IsNullOrWhiteSpace(record.DeleteReason) ? "已清理" : record.DeleteReason)
                        : exists ? "" : "文件缺失";
                return new { id, status, exists, reason };
            });
            SendJson(ctx, 200, new { data });
        }

        // ───── API: 流式播放 (支持 Range) ─────
        private void HandlePlay(HttpListenerContext ctx, string path)
        {
            var record = FindRecordFromPath(path, "/play");
            Log($"HandlePlay: path={path}, record={(record != null ? $"Id={record.Id}, OrderId={record.OrderId}, VideoCodec='{record.VideoCodec}', FilePath='{record.FilePath}'" : "null")}");
            if (record == null || !File.Exists(record.FilePath))
            {
                Log($"HandlePlay: 文件不存在 filePath={record?.FilePath}");
                SendJson(ctx, 404, new { errorCode = "file_not_found", error = "文件不存在" });
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
            bool hasTranscodeCache = shouldTranscode && HasTranscodeCache(filePath);
            Log($"HandlePlay: codec='{codec}', compat={(compatMode ? "1" : "0")}, 判定={(shouldTranscode ? "转码" : "直传")}");

            if (shouldTranscode && recording && !allowTranscodeWhileRecording && !hasTranscodeCache)
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

            if (IsCurrentRecordingFile(filePath))
            {
                Log($"EnsureMp4ContainerForPlayback: 拦截录制中文件点播 Id={record.Id}, OrderId={record.OrderId}, file={Path.GetFileName(filePath)}");
                RuntimeLog.Warn("WebPlayback", $"Blocked current recording MKV playback id={record.Id}, file={Path.GetFileName(filePath)}");
                SendJson(ctx, 409, new
                {
                    recordingInProgress = true,
                    message = "视频正在录制，录制结束后可播放。"
                });
                return "";
            }

            string mp4Path = Path.ChangeExtension(filePath, ".mp4");
            if (File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0)
            {
                _db.UpdateVideoFilePath(filePath, mp4Path);
                return mp4Path;
            }

            if (_mkvConverter == null)
            {
                SendJson(ctx, 500, new { errorCode = "transcoder_unavailable", error = "服务器未配置 MKV 转 MP4 转换器" });
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

        private bool IsCurrentRecordingFile(string filePath)
        {
            if (!_isRecordingProvider())
                return false;

            string currentPath = _currentRecordingFileProvider();
            return IsSamePath(filePath, currentPath);
        }

        private static bool IsSamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            try
            {
                left = Path.GetFullPath(left);
                right = Path.GetFullPath(right);
            }
            catch { }

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildPlayUrl(long id, bool compatMode, bool allowTranscodeWhileRecording)
        {
            var url = $"/api/videos/{Uri.EscapeDataString(id.ToString())}/play?compat={(compatMode ? "1" : "0")}";
            if (allowTranscodeWhileRecording)
                url += "&allowTranscodeWhileRecording=1";
            return url;
        }

        private bool HasTranscodeCache(string filePath)
        {
            string cachePath = GetTranscodeCachePath(filePath);
            return File.Exists(cachePath) && new FileInfo(cachePath).Length > 0;
        }

        private string GetTranscodeCachePath(string filePath)
        {
            string cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(filePath))).Substring(0, 16);
            return Path.Combine(_transCacheDir, $"{cacheKey}.mp4");
        }

        // ───── FFmpeg 转码：命中缓存直接 Range 传输，否则边转码边推流 + 同时写缓存 ─────
        private void ServeTranscodedStream(HttpListenerContext ctx, string filePath)
        {
            string ffmpegPath = AppPaths.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                SendJson(ctx, 500, new { errorCode = "ffmpeg_not_found", error = "服务器未找到 ffmpeg.exe，无法转码播放" });
                return;
            }

            string cachePath = GetTranscodeCachePath(filePath);

            if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
            {
                // 命中缓存 → 标准 Range 传输（支持进度条拖拽、总时长正确）
                Log($"ServeTranscodedStream: 命中缓存 {cachePath}");
                ServeFileWithRange(ctx, cachePath, inline: true);
                return;
            }

            bool transcodeSlotEntered = false;
            IDisposable ffmpegSlot = null;
            try
            {
                _transcodeSlot.Wait(_cts.Token);
                transcodeSlotEntered = true;

                // 等待期间相同视频可能已经完成转码，复查缓存以避免重复启动 FFmpeg。
                if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
                {
                    _transcodeSlot.Release();
                    transcodeSlotEntered = false;
                    Log($"ServeTranscodedStream: 等待后命中缓存 {cachePath}");
                    ServeFileWithRange(ctx, cachePath, inline: true);
                    return;
                }

                ffmpegSlot = _ffmpegWorkLimiter.Enter(_cts.Token);

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
                Task.Run(CleanWebCache);
            }
            catch (OperationCanceledException)
            {
                try { ctx.Response.Abort(); } catch { }
            }
            finally
            {
                ffmpegSlot?.Dispose();
                if (transcodeSlotEntered)
                    _transcodeSlot.Release();
            }
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

        // ───── Web 临时缓存清理：超过上限时按最旧访问时间删除 ─────
        private void CleanWebCache()
        {
            try
            {
                var files = EnumerateWebCacheFiles()
                    .OrderBy(f => GetCacheSortTimeUtc(f))
                    .ToList();

                long totalSize = files.Sum(f => f.Length);
                if (totalSize <= _transCacheMaxBytes) return;

                Log($"CleanWebCache: 当前 {totalSize / 1048576}MB / 上限 {_transCacheMaxBytes / 1048576}MB，开始清理");
                foreach (var f in files)
                {
                    if (totalSize <= _transCacheMaxBytes * 0.8) break; // 清到 80% 水位
                    try
                    {
                        long size = f.Length;
                        f.Delete();
                        totalSize -= size;
                        Log($"CleanWebCache: 删除 {f.FullName} ({size / 1048576}MB)");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"CleanWebCache 异常: {ex.Message}");
            }
        }

        private static IEnumerable<FileInfo> EnumerateWebCacheFiles()
        {
            foreach (var file in EnumerateCacheFiles(AppPaths.TranscodeCacheDir, "*.mp4"))
                yield return file;
            foreach (var file in EnumerateCacheFiles(AppPaths.ClipPreviewDir, "*.jpg"))
                yield return file;
            foreach (var file in EnumerateCacheFiles(AppPaths.ClipsDir, "*.mp4"))
                yield return file;
        }

        private static IEnumerable<FileInfo> EnumerateCacheFiles(string directory, string pattern)
        {
            if (!Directory.Exists(directory))
                yield break;

            foreach (var file in new DirectoryInfo(directory).GetFiles(pattern, SearchOption.TopDirectoryOnly))
                yield return file;
        }

        private static DateTime GetCacheSortTimeUtc(FileInfo file)
        {
            return file.LastAccessTimeUtc > DateTime.MinValue ? file.LastAccessTimeUtc : file.LastWriteTimeUtc;
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

        // ───── API: 剪辑预览 / 剪辑任务 ─────
        private void HandleClipPreview(HttpListenerContext ctx, string path)
        {
            try
            {
                if (!TryFindVideoId(path, "/clip/preview", out long id))
                {
                    SendJson(ctx, 400, new { success = false, errorCode = "invalid_video_id", error = "视频 ID 无效" });
                    return;
                }

                var request = ReadJsonBody<ClipRangeRequest>(ctx);
                var result = _clipService.CreatePreview(id, request.StartSeconds, request.EndSeconds, request.PreviewSide);
                SendJson(ctx, 200, result);
            }
            catch (Exception ex)
            {
                Log($"HandleClipPreview 异常: {ex.Message}");
                SendJson(ctx, 400, new { success = false, error = ex.Message });
            }
        }

        private void HandleClipPrewarm(HttpListenerContext ctx, string path)
        {
            try
            {
                if (!TryFindVideoId(path, "/clip/prewarm", out long id))
                {
                    SendJson(ctx, 400, new { success = false, errorCode = "invalid_video_id", error = "视频 ID 无效" });
                    return;
                }

                var request = ReadJsonBody<ClipRangeRequest>(ctx);
                _clipService.PrewarmPreviewFrames(id, request.StartSeconds, request.EndSeconds, request.PreviewSide);
                SendJson(ctx, 200, new { success = true });
            }
            catch (Exception ex)
            {
                Log($"HandleClipPrewarm 异常: {ex.Message}");
                SendJson(ctx, 400, new { success = false, error = ex.Message });
            }
        }

        private void HandleClipTimeline(HttpListenerContext ctx, string path)
        {
            try
            {
                if (!TryFindVideoId(path, "/clip/timeline", out long id))
                {
                    SendJson(ctx, 400, new { success = false, errorCode = "invalid_video_id", error = "视频 ID 无效" });
                    return;
                }

                var request = ReadJsonBody<ClipRangeRequest>(ctx);
                var result = request.FrameIndex >= 0
                    ? _clipService.CreateTimelinePreviewFrame(id, request.FrameCount, request.FrameIndex)
                    : _clipService.CreateTimelinePreviews(id, request.FrameCount);
                SendJson(ctx, 200, result);
            }
            catch (Exception ex)
            {
                Log($"HandleClipTimeline 异常: {ex.Message}");
                SendJson(ctx, 400, new { success = false, error = ex.Message });
            }
        }

        private void HandleClipFrame(HttpListenerContext ctx, string path)
        {
            try
            {
                if (!TryFindVideoId(path, "/clip/frame", out long id))
                {
                    SendJson(ctx, 400, new { success = false, errorCode = "invalid_video_id", error = "视频 ID 无效" });
                    return;
                }

                var request = ReadJsonBody<ClipRangeRequest>(ctx);
                var result = _clipService.CreatePreviewFrame(id, request.Seconds);
                SendJson(ctx, 200, result);
            }
            catch (Exception ex)
            {
                Log($"HandleClipFrame 异常: {ex.Message}");
                SendJson(ctx, 400, new { success = false, error = ex.Message });
            }
        }

        private void HandleStartClip(HttpListenerContext ctx, string path)
        {
            try
            {
                if (!TryFindVideoId(path, "/clip", out long id))
                {
                    SendJson(ctx, 400, new { success = false, errorCode = "invalid_video_id", error = "视频 ID 无效" });
                    return;
                }

                var request = ReadJsonBody<ClipRangeRequest>(ctx);
                string taskId = _clipService.StartClip(id, request.StartSeconds, request.EndSeconds);
                SendJson(ctx, 200, new { success = true, taskId });
            }
            catch (Exception ex)
            {
                Log($"HandleStartClip 异常: {ex.Message}");
                SendJson(ctx, 400, new { success = false, error = ex.Message });
            }
        }

        private void HandleGetClipTask(HttpListenerContext ctx, string path)
        {
            string taskId = Path.GetFileName(path);
            var task = _clipService.GetTask(taskId);
            if (task == null)
            {
                SendJson(ctx, 404, new { success = false, errorCode = "clip_task_not_found", status = "not_found", message = "剪辑任务不存在", downloadUrl = "" });
                return;
            }

            SendJson(ctx, 200, task);
        }

        private void HandleCancelClipTask(HttpListenerContext ctx, string path)
        {
            string taskId = path.Replace("/api/clip-tasks/", "").Replace("/cancel", "").Trim('/');
            var task = _clipService.CancelTask(taskId);
            if (task == null)
            {
                SendJson(ctx, 404, new { success = false, errorCode = "clip_task_not_found", status = "not_found", message = "剪辑任务不存在", downloadUrl = "" });
                return;
            }

            SendJson(ctx, 200, task);
        }

        private void HandleServeClip(HttpListenerContext ctx, string path)
        {
            string fileName = Path.GetFileName(path);
            string filePath = _clipService.ResolveClipPath(fileName);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                SendJson(ctx, 404, new { errorCode = "clip_file_not_found", error = "剪辑文件不存在" });
                return;
            }

            ServeFileWithRange(ctx, filePath, inline: ShouldServeClipInline(ctx.Request.QueryString["inline"]));
        }

        internal static bool ShouldServeClipInline(string value)
        {
            return string.Equals(value, "1", StringComparison.Ordinal);
        }

        private void HandleServeClipPreview(HttpListenerContext ctx, string path)
        {
            string fileName = Path.GetFileName(path);
            string filePath = _clipService.ResolvePreviewPath(fileName);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                SendJson(ctx, 404, new { error = "预览图不存在" });
                return;
            }

            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.StatusCode = 200;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ctx.Response.ContentLength64 = fs.Length;
            fs.CopyTo(ctx.Response.OutputStream);
            ctx.Response.OutputStream.Close();
        }

        internal static OrderInfo DeserializeOrderInfoSnapshot(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<OrderInfo>(json, _jsonOptions); }
            catch (JsonException) { return null; }
        }

        private void HandleVideoThumbnail(HttpListenerContext ctx, string path)
        {
            try
            {
                if (!TryFindVideoId(path, "/thumbnail", out long id))
                {
                    SendJson(ctx, 400, new { errorCode = "invalid_video_id", error = "视频 ID 无效" });
                    return;
                }

                ClipPreviewFrameResult result = _clipService.CreateThumbnail(id);
                HandleServeClipPreview(ctx, result.Url);
            }
            catch (FileNotFoundException)
            {
                SendJson(ctx, 404, new { errorCode = "file_not_found", error = "录像文件不存在" });
            }
            catch (Exception ex)
            {
                Log($"HandleVideoThumbnail 异常: {ex.Message}");
                SendJson(ctx, 500, new { errorCode = "thumbnail_failed", error = "预览图生成失败" });
            }
        }

        private static T ReadJsonBody<T>(HttpListenerContext ctx)
        {
            string body = ReadRequestBody(ctx, MaxJsonBodyBytes);
            return JsonSerializer.Deserialize<T>(body, _jsonOptions) ?? throw new InvalidDataException("请求内容无效");
        }

        private static string ReadRequestBody(HttpListenerContext ctx, int maxBytes)
        {
            byte[] bytes = ReadRequestBytes(ctx, maxBytes);
            Encoding encoding = ctx.Request.ContentEncoding ?? Encoding.UTF8;
            return encoding.GetString(bytes);
        }

        private static byte[] ReadRequestBytes(HttpListenerContext ctx, int maxBytes)
        {
            long contentLength = ctx.Request.ContentLength64;
            if (contentLength > maxBytes)
                throw new InvalidDataException($"请求内容过大，最大允许 {maxBytes / 1024} KB");

            int capacity = contentLength > 0 ? (int)Math.Min(contentLength, maxBytes) : 0;
            using var buffer = new MemoryStream(capacity);
            byte[] chunk = new byte[8192];
            int totalBytes = 0;
            while (true)
            {
                int read = ctx.Request.InputStream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;
                totalBytes += read;
                if (totalBytes > maxBytes)
                    throw new InvalidDataException($"请求内容过大，最大允许 {maxBytes / 1024} KB");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        internal static void ValidateOrderInfoItems(List<OrderInfo> items)
        {
            if (items.Count > MaxOrderInfoItems)
                throw new InvalidDataException($"单次最多推送 {MaxOrderInfoItems} 条订单");

            foreach (OrderInfo item in items)
            {
                if (item == null)
                    throw new InvalidDataException("订单数据包含空项");
                ValidateFieldLength(item.TrackingNumber, 128, "快递单号");
                ValidateFieldLength(item.OrderId, 128, "订单号");
                ValidateFieldLength(item.BuyerMessage, 2000, "买家留言");
                ValidateFieldLength(item.SellerMemo, 2000, "卖家备注");
                ValidateFieldLength(item.ProductInfo, 4000, "商品信息");
                ValidateFieldLength(item.RefundStatus, 256, "退款状态");
                ValidateFieldLength(item.RefundProductInfo, 4000, "退款商品信息");
            }
        }

        private static void ValidateFieldLength(string value, int maxLength, string fieldName)
        {
            if ((value?.Length ?? 0) > maxLength)
                throw new InvalidDataException($"{fieldName}过长，最多允许 {maxLength} 个字符");
        }

        private static bool TryFindVideoId(string path, string suffix, out long id)
        {
            id = 0;
            string idStr = path.Replace("/api/videos/", "").Replace(suffix, "").Trim('/');
            return long.TryParse(idStr, out id);
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

        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

        // ───── JSON 响应 ─────
        private static void SendJson(HttpListenerContext ctx, int statusCode, object data)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, _jsonOptions);
            SendJsonBytes(ctx, statusCode, bytes);
        }

        private static void SendJsonBytes(HttpListenerContext ctx, int statusCode, byte[] bytes)
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
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

        private void ServeInstallGuidePage(HttpListenerContext ctx)
        {
            string authority = ctx.Request.Url?.Authority ?? $"127.0.0.1:{ctx.Request.LocalEndPoint?.Port ?? 5280}";
            List<Uri> monitorAddresses = GetUserscriptMonitorAddresses(ctx, authority);
            string encodedAddresses = Uri.EscapeDataString(string.Join(',', monitorAddresses.Select(uri => uri.Authority)));
            string scriptUrl = $"{ctx.Request.Url?.Scheme ?? "http"}://{authority}/kuaidizs-order-push.user.js?connect={encodedAddresses}";
            string displayedAuthority = monitorAddresses.FirstOrDefault()?.Authority ?? authority;
            string html = PrintToolInstallGuide.RenderForWeb(displayedAuthority, scriptUrl);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private void ServeUserscript(HttpListenerContext ctx)
        {
            string scriptPath = PrintToolInstallGuide.ResolveUserscriptPath();
            if (!File.Exists(scriptPath))
            {
                SendJson(ctx, 404, new { error = "userscript not found" });
                return;
            }

            string script = File.ReadAllText(scriptPath, Encoding.UTF8);
            string authority = ctx.Request.Url?.Authority ?? "";
            script = PrintToolInstallGuide.AddMonitorConnectPermissions(
                script,
                GetUserscriptMonitorAddresses(ctx, authority).Select(uri => uri.Authority));
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/javascript; charset=utf-8";
            ctx.Response.Headers["Content-Disposition"] = "inline; filename=\"kuaidizs-order-push.user.js\"";
            byte[] bytes = Encoding.UTF8.GetBytes(script);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private List<Uri> GetUserscriptMonitorAddresses(HttpListenerContext ctx, string currentAuthority)
        {
            string primaryAuthority = ResolveUserscriptPrimaryAuthority(currentAuthority);
            var requested = new List<string> { primaryAuthority };
            requested.AddRange(_mobileOrderReceivers.GetAuthorities());
            string[] values = ctx.Request.QueryString.GetValues("connect") ?? Array.Empty<string>();
            requested.AddRange(values);
            List<Uri> addresses = PrintToolInstallGuide.NormalizeMonitorAddresses(requested);
            bool hasLanPrimary = Uri.TryCreate("http://" + primaryAuthority, UriKind.Absolute, out Uri primary)
                && !primary.IsLoopback;
            if (hasLanPrimary)
                addresses.RemoveAll(address => address.IsLoopback);
            return addresses;
        }

        private string ResolveUserscriptPrimaryAuthority(string currentAuthority)
        {
            if (Uri.TryCreate("http://" + currentAuthority, UriKind.Absolute, out Uri current)
                && !current.IsLoopback)
                return current.Authority;

            try
            {
                string accessUrl = _mobileConnectionUrlProvider()?.Trim() ?? "";
                if (Uri.TryCreate(accessUrl, UriKind.Absolute, out Uri lanAddress)
                    && !lanAddress.IsLoopback
                    && string.Equals(lanAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                    return lanAddress.Authority;
            }
            catch { }

            return currentAuthority;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            foreach (var pending in _pendingOrderLookups.Values)
                pending.Completion.TrySetResult(new OrderLookupResult { Responded = false });
            _pendingOrderLookups.Clear();
            try { _clipService.Dispose(); } catch { }
            try { _connectedClients.Dispose(); } catch { }
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
