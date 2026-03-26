using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
                (DataContext as MainViewModel)?.NotifyUserActivity();
            };
            Deactivated += (s, e) =>
            {
                _capsCheckTimer.Stop();
                RestoreCapsLockState();
            };
            // 全局鼠标/键盘活跃检测，用于摄像头空闲休眠唤醒
            PreviewMouseMove += (s, e) => (DataContext as MainViewModel)?.NotifyUserActivity();
            PreviewKeyDown += (s, e) => (DataContext as MainViewModel)?.NotifyUserActivity();
            Loaded += (s, e) => {
                ScanInputTextBox.Focus();
                if (DataContext is MainViewModel vm)
                {
                    vm.PropertyChanged += (sender, args) =>
                    {
                        if (args.PropertyName == nameof(MainViewModel.LastZoomRect) ||
                            args.PropertyName == nameof(MainViewModel.VideoFrame))
                        {
                            Dispatcher.BeginInvoke(new Action(() => UpdateZoomBorder(vm.LastZoomRect)));
                        }
                    };
                    // 窗口/视频区域大小变化时重新计算边框位置
                    VideoImage.SizeChanged += (_, __) =>
                    {
                        UpdateZoomBorder(vm.LastZoomRect);
                    };
                }

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

        private void UpdateZoomBorder(Rect zoomRect)
        {
            var vm = DataContext as MainViewModel;
            if (zoomRect == Rect.Empty || vm == null || vm.CameraFrameSize.Width <= 0 || vm.CameraFrameSize.Height <= 0)
            {
                ZoomPreviewBorder.Visibility = Visibility.Collapsed;
                return;
            }

            double actualW = VideoImage.ActualWidth;
            double actualH = VideoImage.ActualHeight;
            // 始终基于摄像头原始帧尺寸计算，而非 VideoImage.Source（放大时 Source 会变）
            double sourceW = vm.CameraFrameSize.Width;
            double sourceH = vm.CameraFrameSize.Height;

            if (actualW <= 0 || actualH <= 0) return;

            // Uniform 缩放比例
            double scale = Math.Min(actualW / sourceW, actualH / sourceH);

            ZoomPreviewBorder.Width = zoomRect.Width * scale;
            ZoomPreviewBorder.Height = zoomRect.Height * scale;
            ZoomPreviewBorder.Visibility = Visibility.Visible;
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