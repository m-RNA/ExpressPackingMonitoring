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
    }

    private void Download_Click(object sender, RoutedEventArgs e) => WorkstationNetwork.OpenUrl(GlobalOnboardingWindow.MobileDownloadUrl);

    private void Qr_Click(object sender, RoutedEventArgs e)
    {
        if (!MobileConnectionService.TryBuildUsableAccessUrl(_accessAddress, _config.RequireWebAccessKey, _config.WebAccessKey, out string url))
        {
            MessageBox.Show(this, "暂时没有可用的局域网地址，请检查网络、端口和防火墙后重试", "无法显示二维码", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new MobileConnectionWindow(url, _config.RequireWebAccessKey) { Owner = this }.ShowDialog();
    }

    private void Finish_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
