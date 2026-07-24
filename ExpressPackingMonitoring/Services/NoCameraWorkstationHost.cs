using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal sealed class NoCameraWorkstationHost : IDisposable
{
    private AppConfig _config;
    private readonly string _databasePath;
    private readonly string _stateDirectory;
    private VideoDatabase? _database;
    private WebServer? _server;
    private bool _disposed;

    public NoCameraWorkstationHost(
        AppConfig config,
        string? databasePath = null,
        string? stateDirectory = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _databasePath = databasePath ?? AppPaths.VideoDatabasePath;
        _stateDirectory = stateDirectory ?? Path.Combine(AppPaths.CacheDir, "mobile-backup");
    }

    public bool IsRunning => _server != null;
    public bool HasDatabase => _database != null;
    public bool IsLanAvailable { get; private set; }
    public string StoragePath { get; private set; } = "";
    public string LocalPlaybackUrl { get; private set; } = "";
    public string LanAccessUrl { get; private set; } = "";
    public string ErrorMessage { get; private set; } = "";
    public VideoDatabase Database =>
        _database ?? throw new InvalidOperationException("录像数据库尚未打开");

    public void UpdateConfig(AppConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task StartAsync(bool requestLanAccess = true, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopServer();
        ErrorMessage = "";
        IsLanAvailable = false;
        LanAccessUrl = "";

        try
        {
            StoragePath = StorageLocationResolver.Resolve(_config, allowDefaultFallback: false);
            _database ??= new VideoDatabase(_databasePath);
            LocalPlaybackUrl = MobileConnectionService.BuildAccessUrl(
                $"127.0.0.1:{_config.WebServerPort}",
                _config.RequireWebAccessKey,
                _config.WebAccessKey);

            WebServer lanServer = CreateServer("+");
            try
            {
                lanServer.Start(allowAccessSetup: requestLanAccess);
                _server = lanServer;
                string address = await WorkstationNetwork.GetVerifiedLocalAccessAddressAsync(
                    _config.WebServerPort,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                bool isLoopback = address.StartsWith("127.", StringComparison.Ordinal);
                bool addressResponds = !isLoopback && await WorkstationNetwork.CanConnectAsync(address);
                IsLanAvailable = addressResponds && WebServer.HasExpectedFirewallRule(_config.WebServerPort);
                if (IsLanAvailable)
                {
                    LanAccessUrl = MobileConnectionService.BuildAccessUrl(
                        address,
                        _config.RequireWebAccessKey,
                        _config.WebAccessKey);
                }
                else
                {
                    ErrorMessage = "局域网访问尚未配置，当前仅本机可用";
                }
            }
            catch (Exception lanException)
            {
                lanServer.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (WebServer.IsListenerConflict(lanException))
                {
                    throw new InvalidOperationException(
                        $"Web 服务端口 {_config.WebServerPort} 已被其他程序或尚未退出的旧版本占用，请关闭旧程序后重试。",
                        lanException);
                }

                RuntimeLog.Warn("NoCamera", $"LAN listener unavailable, fallback loopback: {lanException.Message}");
                WebServer localServer = CreateServer("127.0.0.1");
                try
                {
                    localServer.Start();
                    _server = localServer;
                    ErrorMessage = $"局域网服务启动失败，当前仅本机可用：{lanException.Message}";
                }
                catch (Exception localException)
                {
                    localServer.Dispose();
                    throw new InvalidOperationException(
                        $"Web 服务启动失败。局域网监听错误：{lanException.Message} 本机监听错误：{localException.Message}",
                        localException);
                }
            }
        }
        catch (Exception ex)
        {
            StopServer();
            ErrorMessage = GetFriendlyError(ex);
            RuntimeLog.Error("NoCamera", "No-camera workstation startup failed", ex);
            throw new InvalidOperationException(ErrorMessage, ex);
        }
    }

    public async Task<bool> RepairLanAccessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await StartAsync(requestLanAccess: true, cancellationToken);
            return IsLanAvailable;
        }
        catch
        {
            return false;
        }
    }

    private WebServer CreateServer(string listenerHost)
    {
        return new WebServer(
            _database!,
            _config.WebServerPort,
            _config.TranscodeCacheMaxMB,
            requireAccessKey: _config.RequireWebAccessKey,
            accessKey: _config.WebAccessKey,
            listenerHost: listenerHost,
            mobileConnectionUrlProvider: () => LanAccessUrl,
            mobileBackupComputerId: _config.MobileBackupComputerId,
            mobileBackupComputerName: Environment.MachineName,
            mobileBackupStateDirectory: _stateDirectory,
            mobileBackupRecordingRootResolver: () => StorageLocationResolver.Resolve(_config, allowDefaultFallback: false))
        {
            EnableOrderInfoLog = _config.EnableOrderInfoLog
        };
    }

    private void StopServer()
    {
        try { _server?.Dispose(); } catch { }
        _server = null;
        IsLanAvailable = false;
    }

    private static string GetFriendlyError(Exception exception)
    {
        Exception root = exception;
        while (root.InnerException != null)
            root = root.InnerException;

        if (root is Microsoft.Data.Sqlite.SqliteException)
            return $"录像数据库无法打开：{root.Message}";
        if (root is IOException or UnauthorizedAccessException)
            return $"录像存储不可用：{root.Message}";
        return exception.Message;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
        try { _database?.Dispose(); } catch { }
        _database = null;
    }
}
