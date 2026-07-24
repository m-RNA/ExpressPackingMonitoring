using System.Windows;

namespace ExpressPackingMonitoring.UI;

public enum WindowCloseChoice
{
    None,
    MinimizeToTray,
    Exit
}

public partial class CloseBehaviorDialog : Window
{
    public CloseBehaviorDialog()
    {
        InitializeComponent();
    }

    public WindowCloseChoice Choice { get; private set; }
    public bool RememberChoice => RememberChoiceCheckBox.IsChecked == true;

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        Choice = WindowCloseChoice.MinimizeToTray;
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Choice = WindowCloseChoice.Exit;
        DialogResult = true;
    }
}
