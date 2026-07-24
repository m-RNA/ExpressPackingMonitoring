using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class NoCameraWorkstationTests
{
    private const string AccessKey = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void LegacyPrintStationRoleKeepsInternalValueAndUsesNewDisplayName()
    {
        var config = new AppConfig { WorkstationRole = "PrintStation" };

        Assert.Equal(WorkstationRoles.PrintStation, config.WorkstationRole);
        Assert.Equal("我没有电脑摄像头", WorkstationRoles.GetDisplayName(config.WorkstationRole));
    }

    [Fact]
    public void NoCameraWindowDoesNotOwnCameraOrRecordingViewModel()
    {
        Type[] fieldTypes = typeof(PrintWorkstationWindow)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.DoesNotContain(typeof(MainViewModel), fieldTypes);
        Assert.DoesNotContain(fieldTypes, type => type.Name is "VideoCapture" or "VideoWriter");
        Assert.DoesNotContain(fieldTypes, type => type.Name.Contains("KeyboardHook", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SettingsCapabilitiesAreDerivedFromWorkstationRole()
    {
        SettingsCapabilities noCamera = SettingsCapabilities.ForRole(WorkstationRoles.PrintStation);
        SettingsCapabilities camera = SettingsCapabilities.ForRole(WorkstationRoles.CameraMonitor);

        Assert.True(noCamera.IsNoCameraWorkstation);
        Assert.False(noCamera.SupportsCameraFeatures);
        Assert.False(noCamera.SupportsSpeechSettings);
        Assert.False(noCamera.SupportsScannerSettings);
        Assert.False(noCamera.SupportsOrderVoiceSettings);
        Assert.False(noCamera.SupportsCameraMaintenance);

        Assert.False(camera.IsNoCameraWorkstation);
        Assert.True(camera.SupportsCameraFeatures);
        Assert.True(camera.SupportsSpeechSettings);
        Assert.True(camera.SupportsScannerSettings);
        Assert.True(camera.SupportsOrderVoiceSettings);
        Assert.True(camera.SupportsCameraMaintenance);
    }

    [Fact]
    public void SharedSettingsWindowDoesNotRetainMainViewModel()
    {
        Type[] fieldTypes = typeof(SettingsWindow)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.DoesNotContain(typeof(MainViewModel), fieldTypes);
    }

    [Fact]
    public void SettingsPreviewDoesNotRunBeforeContextAndWindowAreReady()
    {
        Assert.False(SettingsWindow.ShouldPreviewZoomScale(isLoaded: false, context: null!));
        Assert.False(SettingsWindow.ShouldPreviewZoomScale(
            isLoaded: false,
            new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForRole(WorkstationRoles.CameraMonitor),
                ApplyAsync = _ => Task.FromResult(true)
            }));
        Assert.False(SettingsWindow.ShouldPreviewZoomScale(
            isLoaded: true,
            new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForRole(WorkstationRoles.PrintStation),
                ApplyAsync = _ => Task.FromResult(true)
            }));
        Assert.True(SettingsWindow.ShouldPreviewZoomScale(
            isLoaded: true,
            new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForRole(WorkstationRoles.CameraMonitor),
                ApplyAsync = _ => Task.FromResult(true)
            }));
    }

    [Fact]
    public void StorageResolverUsesConfiguredPriorityAndDoesNotFallBackInStrictMode()
    {
        string directory = CreateTempDirectory();
        try
        {
            string first = Path.Combine(directory, "first");
            string second = Path.Combine(directory, "second");
            var config = new AppConfig
            {
                StorageLocations =
                [
                    new StorageLocation { Path = second, Priority = 2 },
                    new StorageLocation { Path = first, Priority = 1 }
                ]
            };

            Assert.Equal(Path.GetFullPath(first), StorageLocationResolver.Resolve(config, allowDefaultFallback: false));

            config.StorageLocations = [];
            IOException exception = Assert.Throws<IOException>(
                () => StorageLocationResolver.Resolve(config, allowDefaultFallback: false));
            Assert.Contains("未配置录像存储位置", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task HostStartsLocalPlaybackMobileBackupAndLocalOrderReceiver()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        var config = new AppConfig
        {
            WebServerPort = port,
            WebAccessKey = AccessKey,
            MobileBackupComputerId = Guid.NewGuid().ToString(),
            StorageLocations = [new StorageLocation { Path = Path.Combine(directory, "recordings"), Priority = 1 }]
        };

        try
        {
            using var host = new NoCameraWorkstationHost(
                config,
                Path.Combine(directory, "videos.db"),
                Path.Combine(directory, "state"));

            await host.StartAsync(
                requestLanAccess: false,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(host.IsRunning);
            Assert.NotNull(host.Database);
            Assert.StartsWith($"http://127.0.0.1:{port}", host.LocalPlaybackUrl, StringComparison.Ordinal);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/mobile-backup/capabilities");
            request.Headers.Add("X-EPM-Access-Key", AccessKey);
            using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            WorkstationNetwork.TestOrderSendResult order =
                await WorkstationNetwork.SendTestOrderAsync($"127.0.0.1:{port}");
            Assert.True(order.Sent, order.ErrorMessage);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task CorruptDatabaseIsReportedAndServiceDoesNotStart()
    {
        string directory = CreateTempDirectory();
        string databasePath = Path.Combine(directory, "videos.db");
        await File.WriteAllTextAsync(databasePath, "not a sqlite database", TestContext.Current.CancellationToken);
        var config = new AppConfig
        {
            WebServerPort = GetFreeTcpPort(),
            WebAccessKey = AccessKey,
            StorageLocations = [new StorageLocation { Path = Path.Combine(directory, "recordings"), Priority = 1 }]
        };

        try
        {
            using var host = new NoCameraWorkstationHost(config, databasePath, Path.Combine(directory, "state"));
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.StartAsync(
                    requestLanAccess: false,
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.False(host.IsRunning);
            Assert.Contains("录像数据库无法打开", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task OccupiedPortIsReportedAndServiceDoesNotStart()
    {
        string directory = CreateTempDirectory();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var config = new AppConfig
        {
            WebServerPort = port,
            WebAccessKey = AccessKey,
            StorageLocations = [new StorageLocation { Path = Path.Combine(directory, "recordings"), Priority = 1 }]
        };

        try
        {
            using var host = new NoCameraWorkstationHost(
                config,
                Path.Combine(directory, "videos.db"),
                Path.Combine(directory, "state"));
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.StartAsync(
                    requestLanAccess: false,
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.False(host.IsRunning);
            Assert.Contains("端口", exception.Message, StringComparison.Ordinal);
            Assert.Contains("占用", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("缺少监听权限", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            DeleteTempDirectory(directory);
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ExpressPackingMonitoring.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
