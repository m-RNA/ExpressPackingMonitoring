using System.Windows;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI;

public partial class MobileConnectionWindow : Window
{
    private readonly string _url;

    public bool OpenSettingsRequested { get; private set; }

    public MobileConnectionWindow(
        string url,
        bool accessProtected,
        string unavailableMessage = "",
        bool canOpenSettings = true)
    {
        InitializeComponent();
        _url = url?.Trim() ?? "";

        bool isReady = !string.IsNullOrWhiteSpace(_url)
            && string.IsNullOrWhiteSpace(unavailableMessage);
        ReadyPanel.Visibility = isReady ? Visibility.Visible : Visibility.Collapsed;
        UnavailablePanel.Visibility = isReady ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.Visibility = isReady ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.Visibility = isReady ? Visibility.Visible : Visibility.Collapsed;
        OpenSettingsButton.Visibility = !isReady && canOpenSettings ? Visibility.Visible : Visibility.Collapsed;

        if (isReady)
        {
            AccessUrlTextBox.Text = _url;
            QrCodeImage.Source = MobileConnectionService.CreateQrBitmap(_url);
            SecurityNotice.Visibility = accessProtected ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            UnavailableText.Text = string.IsNullOrWhiteSpace(unavailableMessage)
                ? "局域网服务尚未准备完成，请稍后重试"
                : unavailableMessage;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetDataObject(_url, true);
            CopyButton.Content = "已复制";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"复制网址失败：{ex.Message}", "手机/电脑连接", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (WorkstationNetwork.TryOpenUrl(_url, out string error))
            return;

        MessageBox.Show(this, $"打开网页失败：{error}", "手机/电脑连接", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
