using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public class UnifiedModulesTests
{
    [Fact]
    public void NewConfig_LeavesBusinessModulesUnconfigured()
    {
        var config = new AppConfig();

        Assert.True(AppConfig.NormalizeAfterLoad(config));

        Assert.Equal(AppConfig.CurrentUnifiedModulesMigrationVersion, config.UnifiedModulesMigrationVersion);
        Assert.False(config.EnablePcCameraRecording);
        Assert.False(config.EnableMobileBackup);
        Assert.False(config.EnableOrderIntegration);
        Assert.True(config.EnableWebServer);
    }

    [Fact]
    public void LegacyCameraMonitor_MigratesRecordingWithoutLosingScannerSettings()
    {
        var config = new AppConfig
        {
            WorkstationRole = "CameraMonitor",
            FirstUseWizardCompleted = true,
            EnableCameraBarcodeRecognition = true,
            EnableGlobalKeyboard = false,
            EnableScannerAutoSubmit = true
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.True(config.EnablePcCameraRecording);
        Assert.True(config.EnableMobileBackup);
        Assert.Equal(AppConfig.CurrentPcRecordingSetupVersion, config.PcRecordingSetupVersion);
        Assert.True(config.EnableCameraBarcodeRecognition);
        Assert.False(config.EnableGlobalKeyboard);
        Assert.True(config.EnableScannerAutoSubmit);
    }

    [Fact]
    public void LegacyPrintStation_MigratesRemoteOrderTarget()
    {
        var config = new AppConfig
        {
            WorkstationRole = "PrintStation",
            PrintStationMonitorAddress = "192.168.1.10:5280"
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.True(config.EnableOrderIntegration);
        OrderIntegrationTarget target = Assert.Single(config.OrderIntegrationTargets);
        Assert.Equal("192.168.1.10:5280", target.Address);
        Assert.False(target.IsLocal);
    }

    [Theory]
    [InlineData("--monitor", AppModules.PcRecording)]
    [InlineData("--print-station", AppModules.OrderIntegration)]
    [InlineData("--role=PrintStation", AppModules.OrderIntegration)]
    [InlineData("--temporary-role", "PrintStation", AppModules.OrderIntegration)]
    [InlineData("--role", "CameraMonitor", AppModules.PcRecording)]
    public void LegacyArguments_SelectModuleWithoutChangingConfiguration(params string[] values)
    {
        string expected = values[^1];
        string[] args = values[..^1];

        Assert.Equal(expected, StartupModulePolicy.Resolve(args));
    }

    [Theory]
    [InlineData("/api/orderinfo", true)]
    [InlineData("/api/order-lookup/pending", true)]
    [InlineData("/api/videos", false)]
    public void OrderIntegrationPaths_AreSeparatedFromLanViewing(string path, bool expected)
    {
        Assert.Equal(expected, ExpressPackingMonitoring.Services.WebServer.IsOrderIntegrationPath(path));
    }
}
