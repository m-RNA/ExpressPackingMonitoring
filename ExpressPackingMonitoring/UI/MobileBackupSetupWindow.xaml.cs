using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System.Windows;

namespace ExpressPackingMonitoring.UI;

public partial class MobileBackupSetupWindow : Window
{
    private readonly AppConfig _config;
    private readonly string _accessAddress;

    public MobileBackupSetupWindow(AppConfig config, string accessAddress)
    {
        _config = config;
        _accessAddress = accessAddress;
        InitializeComponent();
        string path = config.StorageLocations.FirstOrDefault()?.Path ?? "尚未设置";
        StorageText.Text = $"录像将保存到：{path}";
        LanText.Text = string.IsNullOrWhiteSpace(accessAddress)
            ? "局域网服务尚未取得有效地址。可以先完成配置，稍后在此页面重试二维码"
            : $"电脑地址：http://{accessAddress}  访问密钥会安全写入二维码";
        if (MobileConnectionService.TryBuildUsableAccessUrl(
                _accessAddress,
                _config.RequireWebAccessKey,
                _config.WebAccessKey,
                out string url))
        {
            AccessUrlTextBox.Text = url;
            QrCodeImage.Source = MobileConnectionService.CreateQrBitmap(url, 180);
            QrUnavailableText.Visibility = Visibility.Collapsed;
        }
        else
        {
            AccessUrlTextBox.Text = "局域网服务尚未准备完成";
            QrCodeImage.Source = null;
            QrUnavailableText.Visibility = Visibility.Visible;
        }
    }

    private void Download_Click(object sender, RoutedEventArgs e) => WorkstationNetwork.OpenUrl(GlobalOnboardingWindow.MobileDownloadUrl);

    private void Finish_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
