using Xunit;

namespace ExpressPackingMonitoring.Tests;

public class UiCompositionTests
{
    [Fact]
    public void MainModules_AreEmbeddedInsteadOfOpeningLegacyWindows()
    {
        string root = FindRepositoryRoot();
        string project = Path.Combine(root, "ExpressPackingMonitoring");
        string mainXaml = File.ReadAllText(Path.Combine(project, "UI", "MainWindow.xaml"));
        string mainViewModel = File.ReadAllText(Path.Combine(project, "ViewModels", "MainViewModel.cs"));

        Assert.Contains("MobileBackupContentHost", mainXaml);
        Assert.Contains("OrderIntegrationContentHost", mainXaml);
        Assert.Contains("VideoLibraryContentHost", mainXaml);
        Assert.Contains("SettingsContentHost", mainXaml);
        Assert.DoesNotContain("new SettingsWindow", mainViewModel);
        Assert.DoesNotContain("new PlaybackWindow", mainViewModel);
        Assert.False(File.Exists(Path.Combine(project, "UI", "SettingsWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(project, "UI", "PlaybackWindow.xaml")));
    }

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("找不到解决方案根目录");
    }
}
