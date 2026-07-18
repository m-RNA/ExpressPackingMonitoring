using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Documents;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("zh-CN", AppLanguage.Chinese)]
    [InlineData("zh-TW", AppLanguage.Chinese)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("fr-FR", AppLanguage.English)]
    public void Resolve_AutoUsesChineseFamilyAndFallsBackToEnglish(string culture, string expected)
    {
        Assert.Equal(expected, AppLanguage.Resolve(AppLanguage.Auto, CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void NormalizeAfterLoad_MigratesLegacyVoicesAndInvalidLanguage()
    {
        var config = new AppConfig
        {
            Language = "invalid",
            EdgeTtsVoice = "zh-CN-XiaoyiNeural",
            EdgeTtsWarningVoice = "zh-CN-YunxiNeural",
            EdgeTtsVoiceZhHans = "",
            EdgeTtsWarningVoiceZhHans = ""
        };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.Equal(AppLanguage.Auto, config.Language);
        Assert.Equal("zh-CN-XiaoyiNeural", config.EdgeTtsVoiceZhHans);
        Assert.Equal("zh-CN-YunxiNeural", config.EdgeTtsWarningVoiceZhHans);
        Assert.Equal("en-US-JennyNeural", config.EdgeTtsVoiceEnUs);
    }

    [Fact]
    public void Resources_ContainEnglishDefaultAndChineseSatelliteValues()
    {
        Assert.Equal("Settings", AppLanguage.Get("设置", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("设置", AppLanguage.Get("设置", CultureInfo.GetCultureInfo("zh-Hans")));
        Assert.Equal("Recording started", AppLanguage.Get("Speech.StartRecording", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("开始录制", AppLanguage.Get("Speech.StartRecording", CultureInfo.GetCultureInfo("zh-Hans")));
    }

    [Fact]
    public void WpfViews_AllStaticChineseTextHasEnglishResource()
    {
        string projectPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "ExpressPackingMonitoring"));
        string[] views = Directory.GetFiles(projectPath, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}App.xaml", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var values = views
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "(?:Text|Content|Header|Title|ToolTip)=\"([^\"]*[\\p{IsCJKUnifiedIdeographs}][^\"]*)\"")
                .Select(match => match.Groups[1].Value))
            .Where(value => !value.StartsWith("{Binding ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var english = CultureInfo.GetCultureInfo("en-US");
        string[] missing = values
            .Where(value => Regex.IsMatch(AppLanguage.Get(value, english), "[\\p{IsCJKUnifiedIdeographs}]"))
            .ToArray();

        Assert.True(missing.Length == 0, "Missing English resources: " + string.Join(" | ", missing));
    }

    [Fact]
    public void ToastLiterals_AllHaveEnglishResources()
    {
        string projectPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "ExpressPackingMonitoring"));
        string[] sourceFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] values = sourceFiles
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "ShowToast\\(\\s*\"([^\"\\r\\n]*[\\p{IsCJKUnifiedIdeographs}][^\"\\r\\n]*)\"\\s*\\)")
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var english = CultureInfo.GetCultureInfo("en-US");
        string[] missing = values
            .Where(value => Regex.IsMatch(AppLanguage.Get(value, english), "[\\p{IsCJKUnifiedIdeographs}]"))
            .ToArray();

        Assert.True(missing.Length == 0, "Missing English toast resources: " + string.Join(" | ", missing));
    }

    [Fact]
    public void TextBlockLocalization_DistinguishesTextPropertyFromExplicitInlines()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var ordinaryText = new TextBlock { Text = "摄像头" };
                var inlineText = new TextBlock();
                inlineText.Inlines.Add(new Run("今日:"));
                inlineText.Inlines.Add(new Run("0"));
                var businessDataContainer = new StackPanel();
                AppLanguage.SetAutoLocalize(businessDataContainer, false);
                var businessDataText = new TextBlock { Text = "开始录制" };
                businessDataContainer.Children.Add(businessDataText);

                Assert.True(AppLanguage.ShouldLocalizeTextProperty(ordinaryText));
                Assert.False(AppLanguage.ShouldLocalizeTextProperty(inlineText));
                Assert.False(AppLanguage.ShouldLocalizeTextProperty(businessDataText));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

}
