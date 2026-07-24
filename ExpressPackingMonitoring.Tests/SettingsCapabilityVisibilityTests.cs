using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class SettingsCapabilityVisibilityTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("StatisticsButton", "数据统计")]
    [InlineData("PlaybackButton", "录像回放")]
    [InlineData("SettingsButton", "设置")]
    [InlineData("OpenWebButton", "网页回放")]
    public void NoCameraWindowExposesSharedWindowAndWebPlaybackEntries(string name, string content)
    {
        XElement button = Assert.Single(
            LoadXaml("Workstations", "PrintWorkstationWindow.xaml")
                .Descendants(Presentation + "Button"),
            element => (string?)element.Attribute(Xaml + "Name") == name);

        Assert.Equal(content, (string?)button.Attribute("Content"));
    }

    [Theory]
    [InlineData("面单放大", "Capabilities.SupportsCameraFeatures")]
    [InlineData("录制控制", "Capabilities.SupportsCameraFeatures")]
    [InlineData("AI 语音", "Capabilities.SupportsSpeechSettings")]
    [InlineData("扫码设置", "Capabilities.SupportsScannerSettings")]
    public void CameraOnlyTabsAreControlledByCapabilities(string header, string capability)
    {
        XElement tab = Assert.Single(
            LoadSettingsXaml().Descendants(Presentation + "TabItem"),
            element => (string?)element.Attribute("Header") == header);

        Assert.Contains(capability, (string?)tab.Attribute("Visibility") ?? string.Empty);
    }

    [Theory]
    [InlineData("摄像头", "Capabilities.SupportsCameraFeatures")]
    [InlineData("麦克风", "Capabilities.SupportsCameraFeatures")]
    [InlineData("视频编码格式", "Capabilities.SupportsCameraFeatures")]
    [InlineData("启用录像水印", "Capabilities.SupportsCameraFeatures")]
    [InlineData("配置向导", "Capabilities.SupportsCameraMaintenance")]
    [InlineData("订单备注播报", "Capabilities.SupportsOrderVoiceSettings")]
    public void CameraOnlyRowsOrCardsAreControlledByCapabilities(string label, string capability)
    {
        XElement labelElement = Assert.Single(
            LoadSettingsXaml().Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == label);

        Assert.Contains(
            labelElement.AncestorsAndSelf()
                .Select(element => (string?)element.Attribute("Visibility"))
                .Where(value => value != null),
            value => value!.Contains(capability, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("录像方式")]
    [InlineData("关闭窗口时")]
    [InlineData("界面语言")]
    [InlineData("外观主题")]
    [InlineData("网页访问端口")]
    [InlineData("录像网页访问密钥")]
    [InlineData("调试日志")]
    [InlineData("显示高级设置")]
    [InlineData("开机自启动")]
    [InlineData("自动检查更新")]
    public void SharedSettingsAreNotHiddenByWorkstationCapabilities(string label)
    {
        XElement labelElement = Assert.Single(
            LoadSettingsXaml().Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == label);

        Assert.DoesNotContain(
            labelElement.AncestorsAndSelf()
                .Select(element => (string?)element.Attribute("Visibility"))
                .Where(value => value != null),
            value => value!.Contains("Capabilities.", StringComparison.Ordinal));
    }

    private static XDocument LoadSettingsXaml()
        => LoadXaml("UI", "SettingsWindow.xaml");

    private static XDocument LoadXaml(string directoryName, string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "ExpressPackingMonitoring",
                directoryName,
                fileName);
            if (File.Exists(candidate))
                return XDocument.Load(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"找不到 {fileName}");
    }
}
