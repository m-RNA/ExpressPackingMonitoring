using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        private const int VK_CAPITAL = 0x14;
        private DispatcherTimer _capsCheckTimer;
        private bool _capsLockStateBeforeFocus;
        private bool _capsLockOverridden;

        private bool IsCapsLockOn() => (GetKeyState(VK_CAPITAL) & 1) != 0;

        private void EnsureCapsLockOn()
        {
            if (!IsCapsLockOn())
            {
                keybd_event((byte)VK_CAPITAL, 0x45, 0, UIntPtr.Zero);
                keybd_event((byte)VK_CAPITAL, 0x45, 2, UIntPtr.Zero);
                _capsLockOverridden = true;
            }
        }

        private void RestoreCapsLockState()
        {
            if (_capsLockOverridden && !_capsLockStateBeforeFocus && IsCapsLockOn())
            {
                keybd_event((byte)VK_CAPITAL, 0x45, 0, UIntPtr.Zero);
                keybd_event((byte)VK_CAPITAL, 0x45, 2, UIntPtr.Zero);
            }
            _capsLockOverridden = false;
        }

        public MainWindow()
        {
            InitializeComponent();
            _capsCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _capsCheckTimer.Tick += (s, e) =>
            {
                if (IsActive && ScanInputTextBox.IsFocused && string.IsNullOrEmpty(ScanInputTextBox.Text))
                    EnsureCapsLockOn();
                else
                    _capsCheckTimer.Stop();
            };
            Activated += (s, e) =>
            {
                _capsLockStateBeforeFocus = IsCapsLockOn();
                _capsLockOverridden = false;
                if (ScanInputTextBox.IsFocused)
                {
                    EnsureCapsLockOn();
                    if (string.IsNullOrEmpty(ScanInputTextBox.Text)) _capsCheckTimer.Start();
                }
            };
            Deactivated += (s, e) =>
            {
                _capsCheckTimer.Stop();
                RestoreCapsLockState();
            };
            Loaded += (s, e) => {
                ScanInputTextBox.Focus();

                // 【单文件打包兼容修复】：改用 AppContext.BaseDirectory 或 Process.GetCurrentProcess().MainModule.FileName 取代 Assembly.GetExecutingAssembly().Location
                try
                {
                    string processObjPath = System.Diagnostics.Process.GetCurrentProcess()?.MainModule?.FileName ?? AppContext.BaseDirectory;
                    var buildTime = System.IO.File.GetLastWriteTime(processObjPath);
                    this.Title = $"打包监控 (编译于: {buildTime:yyyy-MM-dd HH:mm})";
                }
                catch
                {
                    this.Title = "打包监控";
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
            _capsCheckTimer.Stop();
            // 延迟检查 IsActive，避免在 Deactivated 之前抢先 re-focus 导致 CapsLock 恢复失败
            Dispatcher.BeginInvoke(new System.Action(() => { if (this.IsActive) ScanInputTextBox.Focus(); }));
        }

        private void ScanInputTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (!_capsLockOverridden) _capsLockStateBeforeFocus = IsCapsLockOn();
            EnsureCapsLockOn();
            if (string.IsNullOrEmpty(ScanInputTextBox.Text)) _capsCheckTimer.Start();
            Dispatcher.BeginInvoke(new System.Action(() => ScanInputTextBox.SelectAll()));
        }

        private void ScanInputTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!ScanInputTextBox.IsKeyboardFocusWithin) { e.Handled = true; ScanInputTextBox.Focus(); }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var vm = DataContext as MainViewModel;

            // 1. 判断是否需要提示：只有正在录制时才提示
            if (vm != null && vm.IsRecording)
            {
                string msg = "当前正在录制，退出将自动保存当前视频。\n确定要退出程序吗？";
                var dialog = new ConfirmDialog(msg, "正在录制 - 退出确认") { Owner = this };
                
                // 如果用户在弹窗中点击了“取消”，则拦截退出事件
                if (dialog.ShowDialog() != true)
                {
                    e.Cancel = true;
                    return;
                }
            }

            // 2. 执行到这里说明：要么没在录制，要么用户点击了确定退出
            
            // 调用 Dispose 确保资源释放（内部会触发 StopRecording 保存视频）
            if (vm is System.IDisposable disposable) 
            {
                disposable.Dispose();
            }

            // 彻底杀掉进程，防止后台残留
            System.Environment.Exit(0);
        }
    }
}