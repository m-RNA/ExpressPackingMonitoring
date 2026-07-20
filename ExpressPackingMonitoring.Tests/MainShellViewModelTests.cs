using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public class MainShellViewModelTests
{
    [Fact]
    public void UnknownStartupModule_FallsBackToOverview()
    {
        var shell = new MainShellViewModel("unknown");

        Assert.Equal(AppModules.Overview, shell.CurrentModule);
        Assert.True(shell.IsOverviewActive);
    }

    [Theory]
    [InlineData(AppModules.Overview)]
    [InlineData(AppModules.PcRecording)]
    [InlineData(AppModules.MobileBackup)]
    [InlineData(AppModules.OrderIntegration)]
    [InlineData(AppModules.VideoLibrary)]
    [InlineData(AppModules.Settings)]
    public void Navigate_UpdatesSingleActiveModule(string module)
    {
        var shell = new MainShellViewModel();

        shell.Navigate(module);

        Assert.Equal(module, shell.CurrentModule);
        Assert.Equal(module == AppModules.Overview, shell.IsOverviewActive);
        Assert.Equal(module == AppModules.PcRecording, shell.IsPcRecordingActive);
        Assert.Equal(module == AppModules.MobileBackup, shell.IsMobileBackupActive);
        Assert.Equal(module == AppModules.OrderIntegration, shell.IsOrderIntegrationActive);
        Assert.Equal(module == AppModules.VideoLibrary, shell.IsVideoLibraryActive);
        Assert.Equal(module == AppModules.Settings, shell.IsSettingsActive);
    }
}
