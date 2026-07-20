using System.Windows;

namespace ExpressPackingMonitoring.UI;

public partial class GlobalOnboardingWindow : Window
{
    public const string MobileDownloadUrl = "https://pan.baidu.com/s/1B9L9l19ZkjtNpK_9rVZxbw?pwd=6666";
    private int _page;

    public GlobalOnboardingWindow()
    {
        InitializeComponent();
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 0) _page--;
        RefreshPage();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page == 3)
        {
            DialogResult = true;
            return;
        }
        _page++;
        RefreshPage();
    }

    private void Download_Click(object sender, RoutedEventArgs e) => WorkstationNetwork.OpenUrl(MobileDownloadUrl);

    private void RefreshPage()
    {
        WelcomePage.Visibility = _page == 0 ? Visibility.Visible : Visibility.Collapsed;
        FeaturesPage.Visibility = _page == 1 ? Visibility.Visible : Visibility.Collapsed;
        MobilePage.Visibility = _page == 2 ? Visibility.Visible : Visibility.Collapsed;
        DonePage.Visibility = _page == 3 ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.Visibility = _page == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _page == 3 ? "进入主界面" : "下一步";
        ProgressText.Text = $"{_page + 1} / 4";
    }
}
