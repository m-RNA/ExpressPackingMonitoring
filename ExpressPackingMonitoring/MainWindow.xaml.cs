using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => {
                ScanInputTextBox.Focus();

                // 【单文件打包兼容修复】：改用 AppContext.BaseDirectory 或 Process.GetCurrentProcess().MainModule.FileName 取代 Assembly.GetExecutingAssembly().Location
                try
                {
                    string processObjPath = System.Diagnostics.Process.GetCurrentProcess()?.MainModule?.FileName ?? AppContext.BaseDirectory;
                    var buildTime = System.IO.File.GetLastWriteTime(processObjPath);
                    this.Title = $"📦 极简打包监控终端 v6.2 (编译于: {buildTime:yyyy-MM-dd HH:mm})";
                }
                catch
                {
                    this.Title = "📦 极简打包监控终端 v6.2";
                }
            };
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel) viewModel.OpenSettings();
        }
private void ScanInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string scanResult = ScanInputTextBox.Text.Trim();
                if (DataContext is MainViewModel viewModel)
                {
                    if (viewModel.ScanCommand.CanExecute(scanResult)) viewModel.ScanCommand.Execute(scanResult);
                }
                // 彻底交由 ViewModel 接管清空逻辑
                e.Handled = true;
            }
        }

        private void ScanInputTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (this.IsActive) Dispatcher.BeginInvoke(new System.Action(() => ScanInputTextBox.Focus()));
        }

        private void ScanInputTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new System.Action(() => ScanInputTextBox.SelectAll()));
        }

        private void ScanInputTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!ScanInputTextBox.IsKeyboardFocusWithin) { e.Handled = true; ScanInputTextBox.Focus(); }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var vm = DataContext as MainViewModel;
            string msg = vm != null && vm.IsRecording
                ? "当前正在录制，退出将自动保存视频。\n当前正在录制，退出将自动保存视频。\n当前正在录制，退出将自动保存视频。\n确定要退出吗？"
                : "确定要退出程序吗？";

            var result = MessageBox.Show(this, msg, "退出确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            // 正常退出：Dispose 会先 StopRecording（保存视频）再停相机
            if (vm is System.IDisposable disposable) disposable.Dispose();
            System.Environment.Exit(0);
        }
    }
}