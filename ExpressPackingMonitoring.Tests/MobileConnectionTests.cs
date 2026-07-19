using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ZXing;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MobileConnectionTests
{
    [Theory]
    [InlineData(false, "", "http://192.168.1.20:5280")]
    [InlineData(false, "abc 123", "http://192.168.1.20:5280/?key=abc%20123")]
    [InlineData(true, "abc 123", "http://192.168.1.20:5280/?key=abc%20123")]
    public void AccessUrlMatchesProtectionSettings(bool requireAccessKey, string accessKey, string expected)
    {
        bool result = MobileConnectionService.TryBuildUsableAccessUrl(
            "192.168.1.20:5280",
            requireAccessKey,
            accessKey,
            out string url);

        Assert.True(result);
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:5280")]
    [InlineData("localhost:5280")]
    [InlineData("0.0.0.0:5280")]
    public void LoopbackOrMissingAddressCannotProduceQrUrl(string address)
    {
        Assert.False(MobileConnectionService.TryBuildUsableAccessUrl(address, false, "", out string url));
        Assert.Equal("", url);
    }

    [Fact]
    public void GeneratedQrDecodesToExactAccessUrl()
    {
        const string expected = "http://192.168.1.20:5280/?key=0123456789abcdef";
        var bitmap = MobileConnectionService.CreateQrBitmap(expected, 320);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var luminance = new RGBLuminanceSource(
            pixels,
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            RGBLuminanceSource.BitmapFormat.BGRA32);
        var decoded = new BarcodeReaderGeneric().Decode(luminance);

        Assert.NotNull(decoded);
        Assert.Equal(expected, decoded.Text);
    }

    [Fact]
    public void ExistingUserIsPromptedOnceWithoutChangingOtherSettings()
    {
        var config = new AppConfig
        {
            FirstUseWizardCompleted = true,
            EnableWebServer = true,
            MobileConnectionSetupVersion = 0,
            EnableGlobalKeyboard = false,
            RequireWebAccessKey = true
        };

        Assert.True(AppConfig.ShouldPromptMobileConnection(config));

        AppConfig.MarkMobileConnectionSetupCompleted(config);

        Assert.False(AppConfig.ShouldPromptMobileConnection(config));
        Assert.Equal(AppConfig.CurrentMobileConnectionSetupVersion, config.MobileConnectionSetupVersion);
        Assert.False(config.EnableGlobalKeyboard);
        Assert.True(config.RequireWebAccessKey);
    }

    [Fact]
    public void FirstUseDefaultsLeaveMobilePromptPendingUntilQrWasShown()
    {
        var config = new AppConfig { EnableWebServer = true };

        AppConfig.ApplyFirstUseDefaults(config);

        Assert.True(config.FirstUseWizardCompleted);
        Assert.Equal(0, config.MobileConnectionSetupVersion);
        Assert.True(AppConfig.ShouldPromptMobileConnection(config));
    }

    [Fact]
    public void FailedSaveCanKeepCurrentSetupVersionPending()
    {
        var current = new AppConfig
        {
            FirstUseWizardCompleted = true,
            EnableWebServer = true,
            MobileConnectionSetupVersion = 0
        };
        var saveCandidate = new AppConfig
        {
            FirstUseWizardCompleted = current.FirstUseWizardCompleted,
            EnableWebServer = current.EnableWebServer,
            MobileConnectionSetupVersion = current.MobileConnectionSetupVersion
        };

        AppConfig.MarkMobileConnectionSetupCompleted(saveCandidate);

        Assert.Equal(0, current.MobileConnectionSetupVersion);
        Assert.True(AppConfig.ShouldPromptMobileConnection(current));
        Assert.Equal(AppConfig.CurrentMobileConnectionSetupVersion, saveCandidate.MobileConnectionSetupVersion);
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    public void PromptRequiresCompletedWizardEnabledServerAndOldVersion(
        bool firstUseCompleted,
        bool webServerEnabled,
        int setupVersion)
    {
        var config = new AppConfig
        {
            FirstUseWizardCompleted = firstUseCompleted,
            EnableWebServer = webServerEnabled,
            MobileConnectionSetupVersion = setupVersion
        };

        Assert.False(AppConfig.ShouldPromptMobileConnection(config));
    }

    [Fact]
    public async Task ProtectedEndpointRejectsUnauthorizedAndAcceptsQueryThenCookie()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"epm-mobile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        int port = GetFreeTcpPort();
        const string accessKey = "0123456789abcdef0123456789abcdef";
        string expectedUrl = $"http://192.168.1.20:{port}/?key={accessKey}";

        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: accessKey,
                listenerHost: "127.0.0.1",
                mobileConnectionUrlProvider: () => expectedUrl);
            server.Start();

            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            using HttpResponseMessage unauthorized = await client.GetAsync("/api/mobile-connection", cancellationToken);
            string unauthorizedBody = await unauthorized.Content.ReadAsStringAsync(cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.DoesNotContain(accessKey, unauthorizedBody, StringComparison.Ordinal);
            Assert.DoesNotContain(expectedUrl, unauthorizedBody, StringComparison.Ordinal);

            using HttpResponseMessage queryAuthorized = await client.GetAsync($"/api/mobile-connection?key={accessKey}", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, queryAuthorized.StatusCode);
            using JsonDocument payload = JsonDocument.Parse(await queryAuthorized.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(expectedUrl, payload.RootElement.GetProperty("url").GetString());
            Assert.StartsWith("data:image/png;base64,", payload.RootElement.GetProperty("qrCode").GetString());
            Assert.True(payload.RootElement.GetProperty("accessProtected").GetBoolean());

            using HttpResponseMessage cookieAuthorized = await client.GetAsync("/api/mobile-connection", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, cookieAuthorized.StatusCode);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MobileConnectionEndpointAlwaysRequiresAccessKeyWhenProtectionIsEnabled()
    {
        Assert.True(WebServer.RequiresAccessKey("/api/mobile-connection"));
        Assert.True(WebServer.RequiresAccessKey("/API/MOBILE-CONNECTION"));
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
