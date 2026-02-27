using System.Windows;
using System.Windows.Input;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => ScanInputTextBox.Focus();
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.OpenSettings();
            }
        }

        private void ScanInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (DataContext is MainViewModel viewModel)
                {
                    string scanResult = ScanInputTextBox.Text;
                    if (viewModel.ScanCommand.CanExecute(scanResult))
                    {
                        viewModel.ScanCommand.Execute(scanResult);
                    }
                }
                ScanInputTextBox.Clear();
                e.Handled = true;
            }
        }

        private void ScanInputTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // 【核心修复】防止焦点死循环崩溃！
            // 只有在主窗口是激活状态（没有弹出设置框）时，才允许抢回焦点
            if (this.IsActive)
            {
                Dispatcher.BeginInvoke(new System.Action(() => ScanInputTextBox.Focus()));
            }
        }

        private void Window_Closed(object sender, System.EventArgs e)
        {
            if (DataContext is System.IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}