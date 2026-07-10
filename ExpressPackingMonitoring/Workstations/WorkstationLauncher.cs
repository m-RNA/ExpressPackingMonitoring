using ExpressPackingMonitoring.Config;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring;

public static class WorkstationRoles
{
    public const string CameraMonitor = "CameraMonitor";
    public const string PrintStation = "PrintStation";

    public static bool IsKnown(string? role) =>
        string.Equals(role, CameraMonitor, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, PrintStation, StringComparison.OrdinalIgnoreCase);

    public static string GetDisplayName(string? role) =>
        string.Equals(role, PrintStation, StringComparison.OrdinalIgnoreCase) ? "快递单打印工位" : "摄像头监控工位";

    public static string GetOtherRole(string role) =>
        string.Equals(role, PrintStation, StringComparison.OrdinalIgnoreCase) ? CameraMonitor : PrintStation;
}

public static class WorkstationConfigStore
{
    private const string ConfigMutexName = @"Local\ExpressPackingMonitoring.Config";
    private static readonly object SaveLock = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppConfig Load()
    {
        string backupPath = AppPaths.ConfigPath + ".bak";
        foreach (string path in new[] { AppPaths.ConfigPath, backupPath })
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path, Encoding.UTF8)) ?? new AppConfig();
                if (AppConfig.NormalizeAfterLoad(config))
                {
                    try { Save(config); }
                    catch (Exception ex) { RuntimeLog.Warn("Config", $"Normalized config save failed: {ex.Message}"); }
                }

                if (!string.Equals(path, AppPaths.ConfigPath, StringComparison.OrdinalIgnoreCase))
                {
                    RuntimeLog.Warn("Config", "Primary config invalid, loaded backup config");
                    try { Save(config); }
                    catch (Exception ex) { RuntimeLog.Warn("Config", $"Backup config restore failed: {ex.Message}"); }
                }
                return config;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Config", $"Config load failed file={Path.GetFileName(path)}, error={ex.Message}");
            }
        }

        var defaultConfig = new AppConfig();
        AppConfig.NormalizeAfterLoad(defaultConfig);
        return defaultConfig;
    }

    public static void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        ExecuteWithSaveLock(() =>
        {
            if (TryReadConfig(AppPaths.ConfigPath, out AppConfig latest))
                config.PrintStationMonitorAddress = latest.PrintStationMonitorAddress;
            SaveCore(config);
        });
    }

    public static bool TrySave(AppConfig config, out string error)
    {
        try
        {
            Save(config);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            RuntimeLog.Error("Config", "Config save failed", ex);
            return false;
        }
    }

    public static bool TryUpdate(Action<AppConfig> update, out AppConfig savedConfig, out string error)
    {
        ArgumentNullException.ThrowIfNull(update);
        try
        {
            AppConfig result = new();
            ExecuteWithSaveLock(() =>
            {
                result = ReadCurrentConfig();
                update(result);
                AppConfig.NormalizeAfterLoad(result);
                SaveCore(result);
            });
            savedConfig = result;
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            savedConfig = new AppConfig();
            error = ex.Message;
            RuntimeLog.Error("Config", "Config update failed", ex);
            return false;
        }
    }

    private static AppConfig ReadCurrentConfig()
    {
        if (TryReadConfig(AppPaths.ConfigPath, out AppConfig config))
            return config;
        if (TryReadConfig(AppPaths.ConfigPath + ".bak", out config))
            return config;

        config = new AppConfig();
        AppConfig.NormalizeAfterLoad(config);
        return config;
    }

    private static bool TryReadConfig(string path, out AppConfig config)
    {
        config = new AppConfig();
        if (!File.Exists(path)) return false;
        try
        {
            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path, Encoding.UTF8));
            if (loaded == null) return false;
            config = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveCore(AppConfig config)
    {
        string configPath = AppPaths.ConfigPath;
        string directory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        string tempPath = $"{configPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        string backupPath = configPath + ".bak";
        Directory.CreateDirectory(directory);

        try
        {
            string json = JsonSerializer.Serialize(config, Options);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(configPath))
                File.Replace(tempPath, configPath, backupPath, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, configPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static void ExecuteWithSaveLock(Action action)
    {
        lock (SaveLock)
        {
            using var mutex = new Mutex(false, ConfigMutexName);
            bool ownsMutex = false;
            try
            {
                try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex)
                    throw new TimeoutException("等待其他工位保存配置超时");
                action();
            }
            finally
            {
                if (ownsMutex)
                    mutex.ReleaseMutex();
            }
        }
    }
}

public static class WorkstationNetwork
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMilliseconds(800) };

    public static string NormalizeAddress(string input, int defaultPort = 5280)
    {
        input = (input ?? "").Trim();
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            input = input[7..];
        if (input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            input = input[8..];
        input = input.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(input)) return "";
        return input.Contains(':') ? input : $"{input}:{defaultPort}";
    }

    public static string ToUrl(string address) => $"http://{NormalizeAddress(address)}";

    public static async Task<bool> CanConnectAsync(string address)
    {
        address = NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(address)) return false;

        try
        {
            using var response = await Client.GetAsync($"{ToUrl(address)}/api/storage");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public sealed class TestOrderSendResult
    {
        public bool Sent { get; init; }
        public bool MonitorConfirmed { get; init; }
        public string ErrorMessage { get; init; } = "";
    }

    public static async Task<TestOrderSendResult> SendTestOrderAsync(string address)
    {
        address = NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(address))
            return new TestOrderSendResult { ErrorMessage = "监控工位地址为空" };

        var order = new[]
        {
            new
            {
                trackingNumber = $"TEST{DateTime.Now:HHmmss}",
                orderId = "测试订单",
                buyerMessage = "这是一条测试买家留言",
                sellerMemo = "这是一条测试卖家备注",
                productInfo = "测试商品",
                isTest = true
            }
        };

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(order), Encoding.UTF8, "application/json");
            using var response = await Client.PostAsync($"{ToUrl(address)}/api/orderinfo", content);
            if (!response.IsSuccessStatusCode)
                return new TestOrderSendResult { ErrorMessage = $"HTTP {(int)response.StatusCode}" };

            string body = await response.Content.ReadAsStringAsync();
            bool confirmed = false;
            try
            {
                using var doc = JsonDocument.Parse(body);
                confirmed = doc.RootElement.TryGetProperty("testCount", out var testCount)
                    && testCount.TryGetInt32(out int value)
                    && value > 0;
            }
            catch
            {
                confirmed = false;
            }

            return new TestOrderSendResult { Sent = true, MonitorConfirmed = confirmed };
        }
        catch (Exception ex)
        {
            return new TestOrderSendResult { ErrorMessage = ex.Message };
        }
    }

    public static async Task<string?> FindMonitorAsync(int port, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var prefixes = GetLocalIpv4Prefixes().Distinct().ToList();
        foreach (string prefix in prefixes)
        {
            for (int start = 1; start <= 254; start += 32)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"正在查找 {prefix}.x");
                var batch = Enumerable.Range(start, Math.Min(32, 255 - start))
                    .Select(i => $"{prefix}.{i}:{port}")
                    .Select(async address => new { address, ok = await CanConnectAsync(address) })
                    .ToArray();
                var results = await Task.WhenAll(batch);
                string? found = results.FirstOrDefault(r => r.ok)?.address;
                if (found != null) return found;
            }
        }

        return null;
    }

    public static string GetBestLocalAccessAddress(int port)
    {
        string ip = GetLocalNetworkCandidates().FirstOrDefault()?.Address.ToString() ?? "127.0.0.1";
        return $"{ip}:{port}";
    }

    public static async Task<string> GetVerifiedLocalAccessAddressAsync(int port, CancellationToken token = default)
    {
        string fallback = GetBestLocalAccessAddress(port);
        foreach (var candidate in GetLocalNetworkCandidates())
        {
            token.ThrowIfCancellationRequested();
            string address = $"{candidate.Address}:{port}";
            if (await CanConnectAsync(address))
                return address;
        }

        return fallback;
    }

    public static bool TryOpenUrl(string url, out string error)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void OpenUrl(string url)
    {
        TryOpenUrl(url, out _);
    }

    public static bool TryRestartApplication()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return false;

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            });
            if (process == null)
                return false;

            try { Application.Current.Shutdown(); } catch { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void AskRestart(Window? owner = null)
    {
        var result = MessageBox.Show(owner,
            "工位用途已保存，需要重启程序后生效。\n\n是否立即重启？",
            "切换工位",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes && !TryRestartApplication())
        {
            MessageBox.Show(owner, "自动重启失败，请手动关闭后重新打开程序。", "切换工位", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static IEnumerable<string> GetLocalIpv4Prefixes()
    {
        foreach (var candidate in GetLocalNetworkCandidates())
        {
            var parts = candidate.Address.ToString().Split('.');
            if (parts.Length == 4)
                yield return $"{parts[0]}.{parts[1]}.{parts[2]}";
        }
    }

    private sealed record LocalNetworkCandidate(IPAddress Address, NetworkInterface Interface, bool HasGateway, int Score);

    private static IEnumerable<LocalNetworkCandidate> GetLocalNetworkCandidates()
    {
        var candidates = new List<LocalNetworkCandidate>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;

            var properties = nic.GetIPProperties();
            bool hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                                   !IPAddress.Equals(g.Address, IPAddress.Any));
            foreach (var addr in properties.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (!IsUsableLanAddress(addr.Address)) continue;

                int score = 0;
                if (hasGateway) score += 100;
                if (IsPrivateLanAddress(addr.Address)) score += 60;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    score += 25;
                if (addr.IPv4Mask != null && addr.IPv4Mask.ToString() == "255.255.255.0")
                    score += 5;

                candidates.Add(new LocalNetworkCandidate(addr.Address, nic, hasGateway, score));
            }
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Address.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUsableLanAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;

        // 0.0.0.0, APIPA, multicast, broadcast, and the RFC 2544 benchmark block are not useful here.
        if (bytes[0] == 0) return false;
        if (bytes[0] == 169 && bytes[1] == 254) return false;
        if (bytes[0] >= 224) return false;
        if (bytes[0] == 255) return false;
        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return false;

        return true;
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;

        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}
