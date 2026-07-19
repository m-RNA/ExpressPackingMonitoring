using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ConnectedClientTests
{
    [Fact]
    public void RegistryDeduplicatesSameClientButCountsDifferentClientTypes()
    {
        var clock = new MutableTimeProvider();
        using var registry = new ConnectedClientRegistry(clock, startCleanupTimer: false);
        var changedCounts = new List<int>();
        registry.Changed += clients => changedCounts.Add(clients.Count);

        registry.Heartbeat(Heartbeat("shared-client", "web-desktop", "电脑网页"), "192.168.1.20");
        registry.Heartbeat(Heartbeat("shared-client", "web-desktop", "电脑网页"), "192.168.1.20");
        registry.Heartbeat(Heartbeat("shared-client", "userscript", "快递端油猴脚本"), "192.168.1.20");

        Assert.Equal(2, registry.GetSnapshot().Count);
        Assert.Equal(new[] { 1, 2 }, changedCounts);
    }

    [Fact]
    public void ConnectedDeviceCountDeduplicatesClientsByRemoteAddress()
    {
        using var registry = new ConnectedClientRegistry(startCleanupTimer: false);
        registry.Heartbeat(Heartbeat("desktop-001", "web-desktop", "电脑网页"), "::ffff:192.168.1.20");
        registry.Heartbeat(Heartbeat("script-001", "userscript", "快递端油猴脚本"), "192.168.1.20");
        registry.Heartbeat(Heartbeat("mobile-001", "web-mobile", "手机网页"), "192.168.1.21");

        Assert.Equal(3, registry.GetSnapshot().Count);
        Assert.Equal(2, ConnectedClientRegistry.CountDistinctAddresses(registry.GetSnapshot()));
    }

    [Fact]
    public void RegistryExpiresAndActivelyDisconnectsClients()
    {
        var clock = new MutableTimeProvider();
        using var registry = new ConnectedClientRegistry(clock, startCleanupTimer: false);
        registry.Heartbeat(Heartbeat("desktop-001", "web-desktop", "电脑网页"), "192.168.1.20");
        registry.Heartbeat(Heartbeat("station-001", "print-station", "打印工位程序"), "192.168.1.21");

        registry.Heartbeat(Heartbeat("station-001", "print-station", "打印工位程序", connected: false), "192.168.1.21");
        Assert.Single(registry.GetSnapshot());

        clock.Advance(TimeSpan.FromSeconds(ConnectedClientRegistry.ExpirationSeconds + 1));
        Assert.Empty(registry.GetSnapshot());
        using var restarted = new ConnectedClientRegistry(clock, startCleanupTimer: false);
        Assert.Empty(restarted.GetSnapshot());
    }

    [Fact]
    public void RegistryEnforcesPerAddressAndGlobalCapacity()
    {
        using var perAddress = new ConnectedClientRegistry(startCleanupTimer: false);
        for (int index = 0; index < ConnectedClientRegistry.MaxClientsPerAddress; index++)
            perAddress.Heartbeat(Heartbeat($"client-{index:000}", "web-desktop", "电脑网页"), "192.168.1.20");
        ConnectedClientValidationException addressError = Assert.Throws<ConnectedClientValidationException>(() =>
            perAddress.Heartbeat(Heartbeat("client-overflow", "web-desktop", "电脑网页"), "192.168.1.20"));
        Assert.Equal("too_many_clients", addressError.ErrorCode);

        using var global = new ConnectedClientRegistry(startCleanupTimer: false);
        for (int index = 0; index < ConnectedClientRegistry.MaxClients; index++)
        {
            global.Heartbeat(
                Heartbeat($"global-{index:000}", "web-mobile", "手机网页"),
                $"192.168.{index / 16}.{index % 16 + 1}");
        }
        ConnectedClientValidationException globalError = Assert.Throws<ConnectedClientValidationException>(() =>
            global.Heartbeat(Heartbeat("global-overflow", "web-mobile", "手机网页"), "10.0.0.1"));
        Assert.Equal("connection_registry_full", globalError.ErrorCode);
    }

    [Theory]
    [InlineData("short", "web-desktop", "电脑网页", "invalid_client_id")]
    [InlineData("valid-client", "unknown", "未知", "invalid_client_type")]
    [InlineData("valid-client", "web-desktop", "", "invalid_display_name")]
    public void RegistryRejectsInvalidHeartbeat(string clientId, string type, string name, string errorCode)
    {
        using var registry = new ConnectedClientRegistry(startCleanupTimer: false);
        ConnectedClientValidationException error = Assert.Throws<ConnectedClientValidationException>(() =>
            registry.Heartbeat(Heartbeat(clientId, type, name), "192.168.1.20"));
        Assert.Equal(errorCode, error.ErrorCode);
    }

    [Fact]
    public async Task HeartbeatApiRegistersWithoutExposingConnectedClientDetails()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-connected-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(directory, "uploads"),
                mobileBackupRecordingDirectory: Path.Combine(directory, "recordings"));
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            CancellationToken token = TestContext.Current.CancellationToken;

            var heartbeat = Heartbeat("browser-client-001", "web-desktop", "电脑网页");
            using HttpResponseMessage first = await client.PostAsJsonAsync("/api/connections/heartbeat", heartbeat, token);
            using HttpResponseMessage repeated = await client.PostAsJsonAsync("/api/connections/heartbeat", heartbeat, token);
            string body = await first.Content.ReadAsStringAsync(token);
            using JsonDocument payload = JsonDocument.Parse(body);

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(ConnectedClientRegistry.ExpirationSeconds, payload.RootElement.GetProperty("expiresInSeconds").GetInt32());
            Assert.False(payload.RootElement.TryGetProperty("clients", out _));
            Assert.False(payload.RootElement.TryGetProperty("count", out _));
            ConnectedClientInfo registered = Assert.Single(server.GetConnectedClients());
            Assert.Equal("browser-client-001", registered.ClientId);

            using HttpResponseMessage invalid = await client.PostAsJsonAsync(
                "/api/connections/heartbeat",
                Heartbeat("browser-client-002", "invalid", "非法设备"),
                token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static ConnectedClientHeartbeat Heartbeat(
        string clientId,
        string type,
        string name,
        bool? connected = null) =>
        new() { ClientId = clientId, ClientType = type, DisplayName = name, Connected = connected };

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
