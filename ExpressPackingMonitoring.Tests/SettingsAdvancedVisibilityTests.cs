using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class SettingsAdvancedVisibilityTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AdvancedSettingsToggle_IsPersistedAndControlsProfessionalRows()
    {
        XDocument document = LoadSettingsXaml();
        XElement toggle = Assert.Single(
            document.Descendants(Presentation + "CheckBox"),
            element => (string?)element.Attribute(Xaml + "Name") == "ShowAdvancedSettingsCheckBox");

        Assert.Contains(
            "Config.ShowAdvancedSettings",
            (string?)toggle.Attribute("IsChecked") ?? string.Empty);

        string[] hiddenLabels =
        [
            "视频编码格式", "硬件加速", "画面流畅度", "画质与文件大小",
            "放大前等待", "放大停留时间", "平滑过渡", "过渡时长",
            "静止超时", "提前提醒时间", "最大时长", "太短的视频自动丢弃", "空闲超时", "高峰时段不休眠",
            "最小文件大小", "显示已清理记录",
            "语音引擎", "语速", "普通播报声线", "警告播报声线", "在线普通声音", "在线警告声音", "语音预览", "断句关键词",
            "网页访问端口", "网页临时缓存上限", "调试日志",
            "同码消失时间", "同码确认时间", "单号判断规则", "扫码间隔保护",
            "扫码最小长度", "自动提交停顿", "平均输入间隔", "单字符间隔上限",
            "声音同步微调"
        ];

        foreach (string label in hiddenLabels)
        {
            XElement labelElement = FindLabel(document, label);
            Assert.True(IsControlledByAdvancedToggle(labelElement), $"{label} 未接入高级设置开关");
        }
    }

    [Theory]
    [InlineData("分辨率")]
    [InlineData("放大倍数")]
    [InlineData("录像网页访问密钥")]
    public void CommonSettings_RemainVisibleWhenAdvancedSettingsAreHidden(string label)
    {
        XElement labelElement = FindLabel(LoadSettingsXaml(), label);

        Assert.False(IsControlledByAdvancedToggle(labelElement), $"{label} 不应受高级设置开关控制");
    }

    private static bool IsControlledByAdvancedToggle(XElement labelElement)
    {
        XElement? row = labelElement.Ancestors(Presentation + "Grid").FirstOrDefault();
        if (row?.ToString(SaveOptions.DisableFormatting).Contains(
                "AdvancedSetting",
                StringComparison.Ordinal) == true)
        {
            return true;
        }

        return labelElement
            .Ancestors(Presentation + "Border")
            .Select(border => (string?)border.Attribute("Visibility"))
            .Any(visibility => visibility?.Contains(
                "ShowAdvancedSettingsCheckBox",
                StringComparison.Ordinal) == true);
    }

    private static XElement FindLabel(XDocument document, string label)
    {
        return Assert.Single(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == label);
    }

    private static XDocument LoadSettingsXaml()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "ExpressPackingMonitoring",
                "UI",
                "SettingsWindow.xaml");
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("找不到 SettingsWindow.xaml");
    }
}
