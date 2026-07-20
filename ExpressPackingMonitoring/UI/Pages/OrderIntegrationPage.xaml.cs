using System.Windows;
using System.Windows.Controls;

namespace ExpressPackingMonitoring.UI.Pages;

public partial class OrderIntegrationPage : UserControl
{
    public OrderIntegrationPage()
    {
        InitializeComponent();
    }

    public event EventHandler? SetupRequested;

    private void Setup_Click(object sender, RoutedEventArgs e) => SetupRequested?.Invoke(this, EventArgs.Empty);
}
