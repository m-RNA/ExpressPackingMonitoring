using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class DefaultConfigurationTests
{
    [Fact]
    public void AppConfig_EnablesAutoStartForNewConfiguration()
    {
        Assert.True(new AppConfig().AutoStartOnBoot);
        Assert.True(JsonSerializer.Deserialize<AppConfig>("{}")!.AutoStartOnBoot);
    }

    [Fact]
    public void AppConfig_PreservesExplicitlyDisabledAutoStart()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>("{\"AutoStartOnBoot\":false}")!;

        AppConfig.NormalizeAfterLoad(config);

        Assert.False(config.AutoStartOnBoot);
    }

    [Fact]
    public void CreateDefaultStorageLocations_UsesEveryReadyNonSystemFixedDrive()
    {
        var drives = new[]
        {
            new StorageDriveCandidate(@"E:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"C:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"D:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"F:\", true, DriveType.Removable),
            new StorageDriveCandidate(@"G:\", false, DriveType.Fixed)
        };

        List<StorageLocation> locations = AppConfig.CreateDefaultStorageLocations(drives);

        Assert.Equal([@"D:\快递打包视频", @"E:\快递打包视频"], locations.Select(location => location.Path));
        Assert.Equal([0, 1], locations.Select(location => location.Priority));
    }

    [Fact]
    public void CreateDefaultStorageLocations_FallsBackToSystemDrive()
    {
        var drives = new[]
        {
            new StorageDriveCandidate(@"C:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"D:\", false, DriveType.Fixed)
        };

        StorageLocation location = Assert.Single(AppConfig.CreateDefaultStorageLocations(drives));

        Assert.Equal(@"C:\快递打包视频", location.Path);
        Assert.Equal(0, location.Priority);
    }

    [Fact]
    public void NormalizeAfterLoad_PreservesExistingStorageLocations()
    {
        var config = new AppConfig
        {
            StorageLocations =
            [
                new StorageLocation { Path = @"Z:\自定义录像", ReserveGB = 25, Priority = 0 }
            ]
        };

        AppConfig.NormalizeAfterLoad(config);

        StorageLocation location = Assert.Single(config.StorageLocations);
        Assert.Equal(@"Z:\自定义录像", location.Path);
    }

    [Fact]
    public void ResolveStartupExecutable_PrefersRootLauncherForCleanPackage()
    {
        string processPath = @"D:\Package\app\ExpressPackingMonitoring.exe";
        string launcherPath = @"D:\Package\ExpressPackingMonitoring.exe";

        string result = AutoStartService.ResolveStartupExecutable(
            processPath,
            @"D:\Package\app\",
            path => string.Equals(path, launcherPath, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(launcherPath, result);
    }

    [Fact]
    public void ResolveStartupExecutable_FallsBackToCurrentProcess()
    {
        string processPath = @"D:\Source\bin\ExpressPackingMonitoring.exe";

        string result = AutoStartService.ResolveStartupExecutable(
            processPath,
            @"D:\Source\bin\",
            path => string.Equals(path, processPath, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(processPath, result);
    }
}
