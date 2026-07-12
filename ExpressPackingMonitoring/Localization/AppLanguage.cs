using ExpressPackingMonitoring.Config;
using System.Globalization;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Data;

namespace ExpressPackingMonitoring.Localization;

public static class AppLanguage
{
    public const string Auto = "Auto";
    public const string Chinese = "zh-Hans";
    public const string English = "en-US";

    private static readonly ResourceManager Resources =
        new("ExpressPackingMonitoring.Resources.Strings", typeof(AppLanguage).Assembly);
    private static readonly ConditionalWeakTable<DependencyObject, HashSet<DependencyProperty>> WatchedProperties = new();

    public static string Current { get; private set; } = Chinese;
    public static bool IsChinese => Current == Chinese;
    public static string StartRecordingText => Get("开始录制");
    public static string StopRecordingText => Get("停止录制");

    public static string NormalizePreference(string? value) => value switch
    {
        Chinese => Chinese,
        English => English,
        Auto => Auto,
        _ => Auto
    };

    public static string Resolve(string? preference, CultureInfo? systemCulture = null)
    {
        string normalized = NormalizePreference(preference);
        if (normalized != Auto) return normalized;
        string language = (systemCulture ?? CultureInfo.InstalledUICulture).TwoLetterISOLanguageName;
        return string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? Chinese : English;
    }

    public static void Initialize(string? preference)
    {
        Current = Resolve(preference);
        var culture = CultureInfo.GetCultureInfo(Current);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    public static string Get(string key) => Get(key, CultureInfo.CurrentUICulture);

    internal static string Get(string key, CultureInfo culture) => Resources.GetString(key, culture) ?? key;

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static string Translate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        string? exact = Resources.GetString(value, CultureInfo.CurrentUICulture);
        if (exact != null) return exact;
        if (IsChinese) return value;

        string translated = value;
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?<=\d)\s*分钟$", Get("分钟"));
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?<=\d)\s*秒$", Get("秒"));
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?<=\d)\s*倍$", Get("倍"));
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?<=\d)\s*位$", Get("位"));
        if (translated.StartsWith("版本 ", StringComparison.Ordinal))
            translated = Get("版本") + translated[2..];
        return translated;
    }

    public static void EnableAutomaticWpfLocalization()
    {
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => LocalizeElement(sender as FrameworkElement)));
        EventManager.RegisterClassHandler(typeof(FrameworkContentElement), FrameworkContentElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => LocalizeContentElement(sender as FrameworkContentElement)));
    }

    private static void LocalizeElement(FrameworkElement? element)
    {
        if (IsChinese || element == null) return;
        if (element is Window windowRoot)
        {
            windowRoot.Dispatcher.BeginInvoke(
                () => LocalizeTree(windowRoot),
                DispatcherPriority.Loaded);
        }

        LocalizeSingleElement(element);
    }

    private static void LocalizeTree(DependencyObject root)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        LocalizeTreeCore(root, visited);
    }

    private static void LocalizeTreeCore(DependencyObject current, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(current)) return;

        // Snapshot both trees before changing any text. Updating Run.Text or a
        // ContentControl can invalidate WPF's live logical-tree enumerator.
        DependencyObject[] logicalChildren = LogicalTreeHelper.GetChildren(current)
            .OfType<DependencyObject>()
            .ToArray();
        int visualChildrenCount = current is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetChildrenCount(current)
            : 0;
        var visualChildren = new DependencyObject[visualChildrenCount];
        for (int index = 0; index < visualChildrenCount; index++)
            visualChildren[index] = VisualTreeHelper.GetChild(current, index);

        if (current is FrameworkElement element)
            LocalizeSingleElement(element);
        else if (current is FrameworkContentElement contentElement)
            LocalizeContentElement(contentElement);

        foreach (DependencyObject child in logicalChildren)
            LocalizeTreeCore(child, visited);
        foreach (DependencyObject child in visualChildren)
            LocalizeTreeCore(child, visited);
    }

    private static void LocalizeSingleElement(FrameworkElement element)
    {
        switch (element)
        {
            case Window window:
                window.SetCurrentValue(Window.TitleProperty, Translate(window.Title));
                Watch(window, Window.TitleProperty);
                break;
            case TextBlock textBlock when ShouldLocalizeTextProperty(textBlock):
                textBlock.SetCurrentValue(TextBlock.TextProperty, Translate(textBlock.Text));
                Watch(textBlock, TextBlock.TextProperty);
                break;
            case ContentControl control when control.Content is string text:
                control.SetCurrentValue(ContentControl.ContentProperty, Translate(text));
                Watch(control, ContentControl.ContentProperty);
                break;
            case HeaderedContentControl control when control.Header is string header:
                control.SetCurrentValue(HeaderedContentControl.HeaderProperty, Translate(header));
                Watch(control, HeaderedContentControl.HeaderProperty);
                break;
        }

        if (element.ToolTip is string tooltip)
            element.SetCurrentValue(FrameworkElement.ToolTipProperty, Translate(tooltip));
    }

    internal static bool ShouldLocalizeTextProperty(TextBlock textBlock) =>
        textBlock.ReadLocalValue(TextBlock.TextProperty) != DependencyProperty.UnsetValue;

    private static void LocalizeContentElement(FrameworkContentElement? element)
    {
        if (IsChinese || element == null) return;
        if (element is Run run)
        {
            run.SetCurrentValue(Run.TextProperty, Translate(run.Text));
            Watch(run, Run.TextProperty);
        }
    }

    private static void Watch(DependencyObject element, DependencyProperty property)
    {
        HashSet<DependencyProperty> properties = WatchedProperties.GetOrCreateValue(element);
        if (!properties.Add(property)) return;
        var descriptor = DependencyPropertyDescriptor.FromProperty(property, element.GetType());
        if (descriptor == null) return;
        descriptor.AddValueChanged(element, (_, _) =>
        {
            object value = element.GetValue(property);
            if (value is string text)
            {
                string translated = Translate(text);
                if (translated != text) element.SetCurrentValue(property, translated);
            }
        });
    }
}

public sealed class LocalizedTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        AppLanguage.Translate(value?.ToString() ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
