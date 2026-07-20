using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class AppConfigResetTests
{
    [Fact]
    public void CreateDefaultsPreservingRuntimeIdentity_KeepsIdentityAndBusinessConfiguration()
    {
        var current = new AppConfig
        {
            WebAccessKey = "1234567890abcdef",
            RequireWebAccessKey = true,
            WebServerPort = 6123,
            MobileBackupComputerId = Guid.NewGuid().ToString("D"),
            EnablePcCameraRecording = true,
            EnableMobileBackup = true,
            EnableOrderIntegration = true,
            GlobalOnboardingVersion = 1,
            PcRecordingSetupVersion = 1,
            MobileBackupSetupVersion = 1,
            OrderIntegrationSetupVersion = 1,
            UnifiedModulesMigrationVersion = 1,
            FirstUseWizardCompleted = true,
            StorageLocations = [new StorageLocation { Path = "E:\\Videos", ReserveGB = 24, Priority = 2 }],
            OrderIntegrationTargets = [new OrderIntegrationTarget { Id = "target-1", DisplayName = "远程电脑", Address = "192.168.1.8:5280", Enabled = true }],
            Theme = "Dark",
            AutoStartOnBoot = true,
            CameraMonikerString = "camera-1",
            EnableCameraBarcodeRecognition = true
        };

        AppConfig reset = AppConfig.CreateDefaultsPreservingRuntimeIdentity(current);

        Assert.Equal(current.WebAccessKey, reset.WebAccessKey);
        Assert.Equal(current.RequireWebAccessKey, reset.RequireWebAccessKey);
        Assert.Equal(current.WebServerPort, reset.WebServerPort);
        Assert.Equal(current.MobileBackupComputerId, reset.MobileBackupComputerId);
        Assert.True(reset.EnablePcCameraRecording);
        Assert.True(reset.EnableMobileBackup);
        Assert.True(reset.EnableOrderIntegration);
        Assert.Equal(1, reset.GlobalOnboardingVersion);
        Assert.Equal("E:\\Videos", Assert.Single(reset.StorageLocations).Path);
        Assert.Equal("target-1", Assert.Single(reset.OrderIntegrationTargets).Id);
        Assert.NotSame(current.StorageLocations, reset.StorageLocations);
        Assert.NotSame(current.OrderIntegrationTargets, reset.OrderIntegrationTargets);
    }

    [Fact]
    public void CreateDefaultsPreservingRuntimeIdentity_ResetsUserTunableSettings()
    {
        var current = new AppConfig
        {
            WebAccessKey = "1234567890abcdef",
            MobileBackupComputerId = Guid.NewGuid().ToString("D"),
            Theme = "Dark",
            Language = "en-US",
            AutoStartOnBoot = true,
            CameraMonikerString = "camera-1",
            CameraIndex = 3,
            EnableCameraBarcodeRecognition = true,
            MaximizeVolumeForSpeech = false
        };

        AppConfig reset = AppConfig.CreateDefaultsPreservingRuntimeIdentity(current);
        var defaults = new AppConfig();

        Assert.Equal(defaults.Theme, reset.Theme);
        Assert.Equal(defaults.Language, reset.Language);
        Assert.Equal(defaults.AutoStartOnBoot, reset.AutoStartOnBoot);
        Assert.Equal(defaults.CameraMonikerString, reset.CameraMonikerString);
        Assert.Equal(defaults.CameraIndex, reset.CameraIndex);
        Assert.Equal(defaults.EnableCameraBarcodeRecognition, reset.EnableCameraBarcodeRecognition);
        Assert.Equal(defaults.MaximizeVolumeForSpeech, reset.MaximizeVolumeForSpeech);
    }
}
