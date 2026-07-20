using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Logging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.ViewModels;
using ExpressPackingMonitoring.Services;
using System.Text.Json;
using ExpressPackingMonitoring.UI.Pages;

namespace ExpressPackingMonitoring.UI
{
    public partial class MainWindow : Window
    {
        private readonly AppRuntimeHost _runtimeHost;

        public MainShellViewModel Shell { get; }

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        private const int VK_CAPITAL = 0x14;
        private DispatcherTimer _capsCheckTimer;
        private bool _capsLockStateBeforeFocus;
        private bool _capsLockOverridden;
        private bool _capsLockSuspended;
        private DateTime _lastMouseActivityNotifyAt = DateTime.MinValue;
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private bool _shutdownConfirmed;
        private bool _shutdownInProgress;
        private bool _resourceCleanupInProgress;
        private readonly DispatcherTimer _scanAutoSubmitTimer;
        private readonly List<double> _scanInputIntervalsMs = new();
        private DateTime _lastScanInputCharAt = DateTime.MinValue;
        private int _lastScanInputLength;

        private bool IsCapsLockOn() => (GetKeyState(VK_CAPITAL) & 1) != 0;

        private void ToggleCapsLock()
        {
            keybd_event((byte)VK_CAPITAL, 0x45, 0, UIntPtr.Zero);
            keybd_event((byte)VK_CAPITAL, 0x45, 2, UIntPtr.Zero);
        }

        private void EnsureCapsLockOn()
        {
            if (!IsCapsLockOn())
            {
                ToggleCapsLock();
                _capsLockOverridden = true;
            }
        }

        private void RestoreCapsLockState()
        {
            if (_capsLockOverridden && !_capsLockStateBeforeFocus && IsCapsLockOn())
            {
                ToggleCapsLock();
            }
            _capsLockOverridden = false;
        }

        private bool ShouldForceCapsLock()
        {
            return !_capsLockSuspended &&
                   IsActive &&
                   WindowState != WindowState.Minimized &&
                   ScanInputTextBox?.IsFocused == true;
        }

        private void ApplyCapsLockForScanInput()
        {
            if (!ShouldForceCapsLock())
            {
                _capsCheckTimer.Stop();
                return;
            }

            if (!_capsLockOverridden)
            {
                _capsLockStateBeforeFocus = IsCapsLockOn();
            }

            EnsureCapsLockOn();
            if (string.IsNullOrEmpty(ScanInputTextBox.Text))
                _capsCheckTimer.Start();
        }

        public void SuspendCapsLockForModalWindow()
        {
            _capsLockSuspended = true;
            _capsCheckTimer.Stop();
            RestoreCapsLockState();
        }

        public void ResumeCapsLockAfterModalWindow()
        {
            _capsLockSuspended = false;
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (IsActive && WindowState != WindowState.Minimized)
                {
                    ScanInputTextBox.Focus();
                    ApplyCapsLockForScanInput();
                }
            }));
        }

        public MainWindow()
            : this(new AppRuntimeHost(), AppModules.Overview)
        {
        }

        public MainWindow(AppRuntimeHost runtimeHost, string initialModule)
        {
            _runtimeHost = runtimeHost ?? throw new ArgumentNullException(nameof(runtimeHost));
            Shell = new MainShellViewModel(initialModule);
            InitializeComponent();
            DataContext = _runtimeHost.Main;
            var mobileBackupPage = new MobileBackupPage { DataContext = _runtimeHost.MobileBackup };
            mobileBackupPage.SetupRequested += (_, _) =>
            {
                if (ConfigureMobileBackup(_runtimeHost.Main)) _runtimeHost.MobileBackup.Refresh();
            };
            mobileBackupPage.SettingsRequested += (_, _) => ShowModule(AppModules.Settings);
            mobileBackupPage.VideoLibraryRequested += (_, _) => ShowModule(AppModules.VideoLibrary);
            MobileBackupContentHost.Content = mobileBackupPage;

            var orderIntegrationPage = new OrderIntegrationPage { DataContext = _runtimeHost.OrderIntegration };
            orderIntegrationPage.SetupRequested += (_, _) =>
            {
                if (ConfigureOrderIntegration(_runtimeHost.Main)) _runtimeHost.OrderIntegration.ReloadTargets();
            };
            OrderIntegrationContentHost.Content = orderIntegrationPage;
            if (CameraBarcodeRuntimeOptions.ShadowMode)
            {
                RuntimeLog.Warn(
                    "CameraBarcodeCompare",
                    "摄像头对照调试模式已启用：摄像头仅记录判定，不会触发录制；扫码枪保持真实执行");
            }
            BtnMobileConnection.Click += BtnMobileConnection_Click;
            BtnMobileConnection.PreviewMouseLeftButtonUp += BtnMobileConnection_PreviewMouseLeftButtonUp;
            _capsCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _capsCheckTimer.Tick += (s, e) =>
            {
                if (string.IsNullOrEmpty(ScanInputTextBox.Text))
                    ApplyCapsLockForScanInput();
                else
                    _capsCheckTimer.Stop();
            };
            _scanAutoSubmitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _scanAutoSubmitTimer.Tick += ScanAutoSubmitTimer_Tick;
            Activated += (s, e) =>
            {
                _capsLockStateBeforeFocus = IsCapsLockOn();
                _capsLockOverridden = false;
                ApplyCapsLockForScanInput();
                (DataContext as MainViewModel)?.NotifyUserActivity();
            };
            Deactivated += (s, e) =>
            {
                _capsCheckTimer.Stop();
                RestoreCapsLockState();
            };
            StateChanged += (s, e) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    _capsCheckTimer.Stop();
                    RestoreCapsLockState();
                }
                else
                {
                    ApplyCapsLockForScanInput();
                }
            };
            // 全局鼠标/键盘活跃检测，用于摄像头空闲休眠唤醒
            PreviewMouseMove += (s, e) =>
            {
                var now = DateTime.UtcNow;
                if (now - _lastMouseActivityNotifyAt < TimeSpan.FromSeconds(1)) return;
                _lastMouseActivityNotifyAt = now;
                (DataContext as MainViewModel)?.NotifyUserActivity();
            };
            PreviewKeyDown += (s, e) => (DataContext as MainViewModel)?.NotifyUserActivity();
            Loaded += (s, e) => {
                ScanInputTextBox.Focus();
                if (DataContext is MainViewModel vm)
                {
                    vm.PropertyChanged += (sender, args) =>
                    {
                        if (args.PropertyName == nameof(MainViewModel.LastZoomRect) ||
                            args.PropertyName == nameof(MainViewModel.CameraFrameSize) ||
                            args.PropertyName == nameof(MainViewModel.IsCameraBarcodeRecognitionEnabled))
                        {
                            Dispatcher.BeginInvoke(new Action(() => UpdateCameraOverlays(vm)));
                        }
                    };
                    // 窗口/视频区域大小变化时重新计算边框位置
                    VideoImage.SizeChanged += (_, __) =>
                    {
                        UpdateCameraOverlays(vm);
                    };
                }

                Title = AppLanguage.Format("Main.Title", AppVersion.Current);
#if DEBUG
                Title += " [摄像头对照调试：摄像头不触发录制]";
#endif

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    (DataContext as MainViewModel)?.RunStartupSetupFlowsIfNeeded(this);
                    string startupModule = Application.Current.Properties["StartupModule"] as string ?? Shell.CurrentModule;
                    ShowModule(startupModule);
                }), DispatcherPriority.ContextIdle);
            };
            SourceInitialized += (_, __) =>
            {
                if (PresentationSource.FromVisual(this) is HwndSource source)
                    source.AddHook(WndProc);
            };
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (DataContext is MainViewModel vm)
            {
                if (msg == WM_ENTERSIZEMOVE)
                {
                    vm.SuppressVideoPreviewUpdates = true;
                }
                else if (msg == WM_EXITSIZEMOVE)
                {
                    vm.ResumeVideoPreviewUpdatesAfterWindowMove();
                    UpdateCameraOverlays(vm);
                }
            }

            return IntPtr.Zero;
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

        private void UpdateCameraOverlays(MainViewModel vm)
        {
            UpdateZoomBorder(vm.LastZoomRect);
            UpdateCameraBarcodeGuide(vm);
        }

        private void UpdateCameraBarcodeGuide(MainViewModel vm)
        {
            double sourceW = vm.CameraFrameSize.Width;
            double sourceH = vm.CameraFrameSize.Height;
            double actualW = VideoImage.ActualWidth;
            double actualH = VideoImage.ActualHeight;
            if (sourceW <= 0 || sourceH <= 0 || actualW <= 0 || actualH <= 0)
            {
                CameraBarcodeGuide.Width = 0;
                CameraBarcodeGuide.Height = 0;
                return;
            }

            double scale = Math.Min(actualW / sourceW, actualH / sourceH);
            CameraBarcodeGuide.Width = sourceW * CameraBarcodeFrameDecoder.GuideWidthRatio * scale;
            CameraBarcodeGuide.Height = sourceH * CameraBarcodeFrameDecoder.GuideHeightRatio * scale;
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel) viewModel.OpenSettings();
        }

        private void BtnMobileConnection_Click(object sender, RoutedEventArgs e)
        {
            ExecuteMobileConnection();
            e.Handled = true;
        }

        private void BtnMobileConnection_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ExecuteMobileConnection();
            e.Handled = true;
        }

        private void ExecuteMobileConnection()
        {
            ShowModule(AppModules.MobileBackup);
        }

        private void NavigationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string module)
                ShowModule(module);
            e.Handled = true;
        }

        private void ShowModule(string module)
        {
            if (DataContext is not MainViewModel viewModel) return;
            if (module == AppModules.Settings)
            {
                viewModel.OpenSettings();
                RefreshModuleStates();
                return;
            }

            if (module == AppModules.PcRecording
                && viewModel.Config.PcRecordingSetupVersion < AppConfig.CurrentPcRecordingSetupVersion
                && !ConfigurePcRecording(viewModel))
                module = AppModules.Overview;
            if (module == AppModules.MobileBackup
                && viewModel.Config.MobileBackupSetupVersion < AppConfig.CurrentMobileBackupSetupVersion
                && !ConfigureMobileBackup(viewModel))
                module = AppModules.Overview;
            if (module == AppModules.OrderIntegration
                && viewModel.Config.OrderIntegrationSetupVersion < AppConfig.CurrentOrderIntegrationSetupVersion
                && !ConfigureOrderIntegration(viewModel))
                module = AppModules.Overview;

            Shell.Navigate(module);
            OverviewModule.Visibility = module == AppModules.Overview ? Visibility.Visible : Visibility.Collapsed;
            PcRecordingModule.Visibility = module == AppModules.PcRecording ? Visibility.Visible : Visibility.Collapsed;
            MobileBackupModule.Visibility = module == AppModules.MobileBackup ? Visibility.Visible : Visibility.Collapsed;
            OrderIntegrationModule.Visibility = module == AppModules.OrderIntegration ? Visibility.Visible : Visibility.Collapsed;
            VideoLibraryModule.Visibility = module == AppModules.VideoLibrary ? Visibility.Visible : Visibility.Collapsed;

            if (module == AppModules.PcRecording)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ScanInputTextBox.Focus();
                    ApplyCapsLockForScanInput();
                }));
            }
            else
            {
                _capsCheckTimer.Stop();
                RestoreCapsLockState();
            }
            RefreshModuleStates();
        }

        private bool ConfigurePcRecording(MainViewModel viewModel)
        {
            if (!viewModel.BeginPcRecordingSetup()) return false;
            try
            {
                var clone = CloneConfig(viewModel.Config);
                var wizard = new FirstUseSetupWizardWindow(clone) { Owner = this };
                SuspendCapsLockForModalWindow();
                bool accepted;
                try { accepted = wizard.ShowDialog() == true && !wizard.WasSkipped; }
                finally { ResumeCapsLockAfterModalWindow(); }
                if (!accepted) return false;

                AppConfig result = wizard.ResultConfig;
                AppConfig.ApplyFirstUseDefaults(result);
                result.EnablePcCameraRecording = true;
                result.PcRecordingSetupVersion = AppConfig.CurrentPcRecordingSetupVersion;
                return viewModel.ApplyModuleConfiguration(result);
            }
            finally
            {
                viewModel.EndPcRecordingSetup();
            }
        }

        private bool ConfigureMobileBackup(MainViewModel viewModel)
        {
            var wizard = new MobileBackupSetupWindow(viewModel.Config, viewModel.MonitorAccessAddress) { Owner = this };
            SuspendCapsLockForModalWindow();
            bool accepted;
            try { accepted = wizard.ShowDialog() == true; }
            finally { ResumeCapsLockAfterModalWindow(); }
            if (!accepted) return false;

            AppConfig result = CloneConfig(viewModel.Config);
            result.EnableMobileBackup = true;
            result.MobileBackupSetupVersion = AppConfig.CurrentMobileBackupSetupVersion;
            return viewModel.ApplyModuleConfiguration(result);
        }

        private bool ConfigureOrderIntegration(MainViewModel viewModel)
        {
            var wizard = new OrderIntegrationSetupWindow(viewModel.Config) { Owner = this };
            SuspendCapsLockForModalWindow();
            bool accepted;
            try { accepted = wizard.ShowDialog() == true; }
            finally { ResumeCapsLockAfterModalWindow(); }
            if (!accepted) return false;

            AppConfig result = CloneConfig(viewModel.Config);
            result.OrderIntegrationTargets = wizard.ResultTargets.ToList();
            result.EnableOrderIntegration = true;
            result.OrderIntegrationSetupVersion = AppConfig.CurrentOrderIntegrationSetupVersion;
            return viewModel.ApplyModuleConfiguration(result);
        }

        private void BtnTogglePcRecording_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel) return;
            if (viewModel.IsRecording && viewModel.Config.EnablePcCameraRecording)
            {
                MessageBox.Show(this, "请先安全完成当前录像，再暂停电脑录像", "正在录像", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ToggleModule(viewModel, AppModules.PcRecording, !viewModel.Config.EnablePcCameraRecording);
        }

        private void ToggleModule(MainViewModel viewModel, string module, bool enabled)
        {
            AppConfig result = CloneConfig(viewModel.Config);
            if (module == AppModules.PcRecording) result.EnablePcCameraRecording = enabled;
            if (module == AppModules.MobileBackup) result.EnableMobileBackup = enabled;
            if (module == AppModules.OrderIntegration) result.EnableOrderIntegration = enabled;
            if (viewModel.ApplyModuleConfiguration(result)) RefreshModuleStates();
        }

        private void BtnOpenVideoLibrary_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel && viewModel.OpenPlaybackCommand.CanExecute(null))
                viewModel.OpenPlaybackCommand.Execute(null);
        }

        private void RefreshModuleStates()
        {
            if (DataContext is not MainViewModel viewModel) return;
            OverviewPcStatus.Text = GetModuleStatus(viewModel.Config.PcRecordingSetupVersion, viewModel.Config.EnablePcCameraRecording, AppConfig.CurrentPcRecordingSetupVersion);
            OverviewMobileStatus.Text = GetModuleStatus(viewModel.Config.MobileBackupSetupVersion, viewModel.Config.EnableMobileBackup, AppConfig.CurrentMobileBackupSetupVersion);
            OverviewOrderStatus.Text = GetModuleStatus(viewModel.Config.OrderIntegrationSetupVersion, viewModel.Config.EnableOrderIntegration, AppConfig.CurrentOrderIntegrationSetupVersion);
            ShellLanStatusText.Text = string.IsNullOrWhiteSpace(viewModel.MonitorAccessAddress)
                ? "局域网服务准备中"
                : $"局域网  {viewModel.MonitorAccessAddress}\n{viewModel.ConnectedDeviceText}";
            ShellVersionText.Text = $"版本 {AppVersion.Current}";
            BtnTogglePcRecording.Content = viewModel.Config.EnablePcCameraRecording ? "暂停电脑录像" : "恢复电脑录像";
            _runtimeHost.MobileBackup.Refresh();
        }

        private static string GetModuleStatus(int setupVersion, bool enabled, int currentVersion) =>
            setupVersion < currentVersion ? "未配置" : enabled ? "运行正常" : "已暂停";

        private static AppConfig CloneConfig(AppConfig config) =>
            JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config)) ?? new AppConfig();

        private void ScanInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ResetScanAutoSubmitState();
                string scanResult = ScanInputTextBox.Text.Trim();
                if (DataContext is MainViewModel viewModel)
                {
                    if (viewModel.ScanCommand.CanExecute(scanResult)) viewModel.ScanCommand.Execute(scanResult);
                }
                // 彻底交由 ViewModel 接管清空逻辑
                e.Handled = true;
            }
        }

        private void ScanInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel || !viewModel.Config.EnableScannerAutoSubmit)
            {
                ResetScanAutoSubmitState();
                _lastScanInputLength = ScanInputTextBox.Text?.Length ?? 0;
                return;
            }

            string text = ScanInputTextBox.Text ?? "";
            if (text.Length == 0)
            {
                ResetScanAutoSubmitState();
                return;
            }

            int addedCount = text.Length - _lastScanInputLength;
            if (addedCount <= 0)
            {
                ResetScanAutoSubmitState();
                _lastScanInputLength = text.Length;
                return;
            }

            var now = DateTime.Now;
            int sequenceBreakMs = Math.Max(100, viewModel.Config.ScannerAutoSubmitMaxKeyIntervalMs);
            for (int i = 0; i < addedCount; i++)
            {
                if (_lastScanInputCharAt != DateTime.MinValue)
                {
                    double elapsed = (now - _lastScanInputCharAt).TotalMilliseconds;
                    if (elapsed > sequenceBreakMs)
                    {
                        _scanInputIntervalsMs.Clear();
                    }
                    else
                    {
                        _scanInputIntervalsMs.Add(elapsed);
                    }
                }
                _lastScanInputCharAt = now;
            }

            _lastScanInputLength = text.Length;
            ScheduleScanAutoSubmitCheck(viewModel.Config.ScannerAutoSubmitQuietMs);
        }

        private void ScheduleScanAutoSubmitCheck(int quietMs)
        {
            _scanAutoSubmitTimer.Stop();
            _scanAutoSubmitTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(quietMs, 120, 600));
            _scanAutoSubmitTimer.Start();
        }

        private void ScanAutoSubmitTimer_Tick(object? sender, EventArgs e)
        {
            _scanAutoSubmitTimer.Stop();

            if (DataContext is not MainViewModel viewModel || !viewModel.Config.EnableScannerAutoSubmit)
                return;

            if ((DateTime.Now - _lastScanInputCharAt).TotalMilliseconds < viewModel.Config.ScannerAutoSubmitQuietMs)
            {
                ScheduleScanAutoSubmitCheck(viewModel.Config.ScannerAutoSubmitQuietMs);
                return;
            }

            string scanResult = ScanInputTextBox.Text.Trim();
            if (scanResult.Length < viewModel.Config.ScannerAutoSubmitMinLength)
                return;

            if (!viewModel.IsAutoSubmitScanCandidate(scanResult))
                return;

            if (!ScannerAutoSubmitPolicy.IsFastSequence(
                    _scanInputIntervalsMs,
                    scanResult.Length,
                    viewModel.Config.ScannerAutoSubmitMaxAverageIntervalMs,
                    viewModel.Config.ScannerAutoSubmitMaxKeyIntervalMs))
                return;

            ResetScanAutoSubmitState();
            if (viewModel.ScanCommand.CanExecute(scanResult))
                viewModel.ScanCommand.Execute(scanResult);
        }

        private void ResetScanAutoSubmitState()
        {
            _scanAutoSubmitTimer.Stop();
            _scanInputIntervalsMs.Clear();
            _lastScanInputCharAt = DateTime.MinValue;
            _lastScanInputLength = ScanInputTextBox?.Text?.Length ?? 0;
        }

        private void ScanInputTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            _capsCheckTimer.Stop();
            // 延迟检查 IsActive，避免在 Deactivated 之前抢先 re-focus 导致 CapsLock 恢复失败
            Dispatcher.BeginInvoke(new System.Action(() => { if (!_capsLockSuspended && this.IsActive) ScanInputTextBox.Focus(); }));
        }

        private void ScanInputTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ApplyCapsLockForScanInput();
            Dispatcher.BeginInvoke(new System.Action(() => ScanInputTextBox.SelectAll()));
        }

        private void ScanInputTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!ScanInputTextBox.IsKeyboardFocusWithin) { e.Handled = true; ScanInputTextBox.Focus(); }
        }

        private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            var vm = DataContext as MainViewModel;

            if (_shutdownConfirmed)
            {
                e.Cancel = true;
                await FinishShutdownAsync(vm);
                return;
            }

            e.Cancel = true;
            if (_shutdownInProgress) return;

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

            _shutdownInProgress = true;
            _capsCheckTimer.Stop();
            RestoreCapsLockState();
            (string shutdownSource, string shutdownDetail) = RuntimeLog.GetShutdownRequest();
            if (string.Equals(shutdownSource, "not-recorded", StringComparison.Ordinal))
            {
                RuntimeLog.RecordShutdownRequest(
                    "WpfWindowClosing",
                    $"isActive={IsActive}, windowState={WindowState}, isVisible={IsVisible}");
                (shutdownSource, shutdownDetail) = RuntimeLog.GetShutdownRequest();
            }
            RuntimeLog.Info("Shutdown", $"Main window closing requested session={RuntimeLog.CurrentSessionId}, source={shutdownSource}, detail={shutdownDetail}");

            bool saved = true;
            if (vm != null)
            {
                var progress = new Progress<string>(msg =>
                {
                    vm.BusyText = "正在关闭程序...";
                    vm.IsBusy = true;
                    if (!IsRoutineShutdownProgressMessage(msg))
                        vm.ShowToast(msg);
                });
                saved = await vm.SaveRecordingsBeforeShutdownAsync(progress);
            }

            if (!saved)
            {
                _shutdownInProgress = false;
                MessageBox.Show("录像保存失败，请检查日志", "退出已取消", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _shutdownConfirmed = true;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Close();
                }
                catch (InvalidOperationException ex)
                {
                    RuntimeLog.Warn("Shutdown", $"Confirmed close failed, force shutdown: {ex.Message}");
                    _ = FinishShutdownAsync(vm);
                }
            }), DispatcherPriority.Background);
        }

        private static bool IsRoutineShutdownProgressMessage(string message)
        {
            return !string.IsNullOrWhiteSpace(message)
                && message.Contains("文件不存在，跳过", StringComparison.Ordinal);
        }

        private async Task FinishShutdownAsync(MainViewModel? vm)
        {
            if (_resourceCleanupInProgress) return;
            _resourceCleanupInProgress = true;
            _capsCheckTimer.Stop();
            RestoreCapsLockState();

            if (vm != null)
            {
                vm.BusyText = "正在关闭程序...";
                vm.IsBusy = true;
            }

            try
            {
                await Task.Run(_runtimeHost.Dispose);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Shutdown", "Background resource cleanup failed", ex);
            }
            finally
            {
                // 录像收尾已经完成；解除 Closing 处理器后显式退出，避免后台资源让进程残留。
                Closing -= Window_Closing;
                (string source, string detail) = RuntimeLog.GetShutdownRequest();
                RuntimeLog.Info("Shutdown", $"Process exit requested session={RuntimeLog.CurrentSessionId}, pid={Environment.ProcessId}, source={source}, detail={detail}");
                try { Application.Current?.Shutdown(0); } catch { }
                Environment.Exit(0);
            }
        }
    }
}
