using Xunit;
using System.Text.RegularExpressions;

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
        Assert.Contains("PcRecordingContentHost", mainXaml);
        Assert.Contains("VideoLibraryContentHost", mainXaml);
        Assert.Contains("SettingsContentHost", mainXaml);
        Assert.DoesNotContain("new SettingsWindow", mainViewModel);
        Assert.DoesNotContain("new PlaybackWindow", mainViewModel);
        Assert.DoesNotContain("new MobileConnectionWindow", mainViewModel);
        Assert.DoesNotContain("new StatisticsWindow", mainViewModel);
        Assert.DoesNotContain("new WorkstationSelectionWindow", mainViewModel);
        Assert.False(File.Exists(Path.Combine(project, "UI", "SettingsWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(project, "UI", "PlaybackWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(project, "UI", "MobileConnectionWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(project, "UI", "StatisticsWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(project, "Workstations", "PrintWorkstationWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(project, "Workstations", "WorkstationSelectionWindow.xaml")));
        Assert.DoesNotContain("ScanInputTextBox", mainXaml);
        Assert.True(File.Exists(Path.Combine(project, "UI", "Pages", "PcRecordingPage.xaml")));
        Assert.True(File.Exists(Path.Combine(project, "UI", "Components", "StatisticsPanel.xaml")));
    }

    [Fact]
    public void WpfColors_AreDeclaredOnlyInColorTokens()
    {
        string root = FindRepositoryRoot();
        string project = Path.Combine(root, "ExpressPackingMonitoring");
        string tokenPath = Path.Combine(project, "Themes", "ColorTokens.xaml");
        string appXaml = File.ReadAllText(Path.Combine(project, "App.xaml"));
        Assert.True(File.Exists(tokenPath));
        Assert.True(
            appXaml.IndexOf("Themes/ColorTokens.xaml", StringComparison.Ordinal) <
            appXaml.IndexOf("Themes/LightTheme.xaml", StringComparison.Ordinal));

        string[] xamlFiles = Directory.GetFiles(project, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, tokenPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] codeFiles = Directory.GetFiles(Path.Combine(project, "UI"), "*.cs", SearchOption.AllDirectories)
            .Append(Path.Combine(project, "Helpers", "BarcodeHelper.cs"))
            .ToArray();
        var forbidden = new Regex(
            "#[0-9A-Fa-f]{3,8}\\b|" +
            "(?:Background|Foreground|BorderBrush|Fill|Stroke|Color)=\\\"(?:White|Black|Transparent|Gray|Red|Green|Blue|Orange|Yellow|Purple|Cyan)\\\"|" +
            "(?:Brushes\\.[A-Za-z]+|Colors\\.[A-Za-z]+|ColorConverter\\.ConvertFromString)",
            RegexOptions.CultureInvariant);

        string[] violations = xamlFiles.Concat(codeFiles)
            .SelectMany(path => forbidden.Matches(File.ReadAllText(path))
                .Select(match => $"{Path.GetRelativePath(project, path)}: {match.Value}"))
            .ToArray();

        Assert.True(violations.Length == 0, "Hard-coded WPF colors: " + string.Join(" | ", violations));
    }

    [Fact]
    public void OverviewModule_IsPresentedAsStatisticsWithoutSetupCards()
    {
        string project = Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring");
        string mainXaml = File.ReadAllText(Path.Combine(project, "UI", "MainWindow.xaml"));

        Assert.Contains("Content=\"统计\" Tag=\"overview\"", mainXaml);
        Assert.Contains("OverviewStatisticsContentHost", mainXaml);
        Assert.Contains("今日概览", mainXaml);
        Assert.Contains("ConnectedMobileDeviceCountText", mainXaml);
        Assert.DoesNotContain("OverviewCardsGrid", mainXaml);
        Assert.DoesNotContain("OverviewPcAction", mainXaml);
        Assert.DoesNotContain("打包数据分析", mainXaml);
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
