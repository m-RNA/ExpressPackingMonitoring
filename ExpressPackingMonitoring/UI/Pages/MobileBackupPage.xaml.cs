using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace ExpressPackingMonitoring.UI.Pages;

public partial class MobileBackupPage : UserControl
{
    public MobileBackupPage()
    {
        InitializeComponent();
    }

    public event EventHandler? SetupRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? VideoLibraryRequested;

    private MobileBackupViewModel? ViewModel => DataContext as MobileBackupViewModel;

    private void Setup_Click(object sender, RoutedEventArgs e) => SetupRequested?.Invoke(this, EventArgs.Empty);
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void VideoLibrary_Click(object sender, RoutedEventArgs e) => VideoLibraryRequested?.Invoke(this, EventArgs.Empty);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { IsConnectionReady: true } vm) return;
        try
        {
            Clipboard.SetDataObject(vm.AccessUrl, true);
            vm.OperationMessage = vm.ContainsAccessKey ? "网址已复制，请勿转发给无关人员" : "网址已复制";
        }
        catch (Exception ex)
        {
            vm.OperationMessage = $"复制失败：{ex.Message}";
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { IsConnectionReady: true } vm) return;
        if (!WorkstationNetwork.TryOpenUrl(vm.AccessUrl, out string error))
            vm.OperationMessage = $"打开失败：{error}";
    }

    private void Download_Click(object sender, RoutedEventArgs e) =>
        WorkstationNetwork.OpenUrl(GlobalOnboardingWindow.MobileDownloadUrl);
}
