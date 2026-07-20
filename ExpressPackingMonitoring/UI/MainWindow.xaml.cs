using ExpressPackingMonitoring.Logging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.ViewModels;
using ExpressPackingMonitoring.Services;
using System.Text.Json;
using ExpressPackingMonitoring.UI.Pages;
using ExpressPackingMonitoring.UI.Components;

namespace ExpressPackingMonitoring.UI
{
    public partial class MainWindow : Window
    {
        private readonly AppRuntimeHost _runtimeHost;
        private readonly PcRecordingPage _pcRecordingPage;
        private readonly VideoLibraryPage _videoLibraryPage;
        private readonly SettingsPage _settingsPage;
        private readonly StatisticsPanel _statisticsPanel;

        public MainShellViewModel Shell { get; }

        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private bool _shutdownConfirmed;
        private bool _shutdownInProgress;
        private bool _resourceCleanupInProgress;
        public void SuspendCapsLockForModalWindow()
            => _pcRecordingPage.SuspendCapsLockForModalWindow();

        public void ResumeCapsLockAfterModalWindow()
            => _pcRecordingPage.ResumeCapsLockAfterModalWindow();

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
            _statisticsPanel = new StatisticsPanel(_runtimeHost.Main.Database);
            OverviewStatisticsContentHost.Content = _statisticsPanel;
            _pcRecordingPage = new PcRecordingPage(_runtimeHost.Main);
            _pcRecordingPage.ModuleNavigationRequested += (_, module) => ShowModule(module);
            PcRecordingContentHost.Content = _pcRecordingPage;
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

            string videoFolder;
            try
            {
                videoFolder = _runtimeHost.Main.ResolveVideoLibraryFolderPath();
            }
            catch (Exception ex)
            {
                videoFolder = AppPaths.UserDataDir;
                _runtimeHost.Main.ShowToast($"录像存储路径暂不可用：{ex.Message}");
            }
            _videoLibraryPage = new VideoLibraryPage(videoFolder, _runtimeHost.Main.Database, _runtimeHost.Main.Config.ShowDeletedVideos);
            VideoLibraryContentHost.Content = _videoLibraryPage;

            _settingsPage = new SettingsPage(
                _runtimeHost.Main,
                CloneConfig(_runtimeHost.Main.Config),
                _runtimeHost.Main.DiskUsagePercent,
                _runtimeHost.Main.DiskUsageText,
                _runtimeHost.Main.IsRecording);
            SettingsContentHost.Content = _settingsPage;
            _runtimeHost.Main.ModuleNavigationRequested += Runtime_ModuleNavigationRequested;
            if (CameraBarcodeRuntimeOptions.ShadowMode)
            {
                RuntimeLog.Warn(
                    "CameraBarcodeCompare",
                    "摄像头对照调试模式已启用：摄像头仅记录判定，不会触发录制；扫码枪保持真实执行");
            }
            Loaded += (s, e) => {
                Title = AppLanguage.Format("Main.Title", AppVersion.Current);
#if DEBUG
                Title += " [摄像头对照调试：摄像头不触发录制]";
#endif

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    (DataContext as MainViewModel)?.RunStartupSetupFlowsIfNeeded(this);
                    string startupModule = Application.Current.Properties["StartupModule"] as string ?? Shell.CurrentModule;
                    ShowModule(startupModule);
                    UpdateResponsiveLayout(ActualWidth);
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
            if (msg == WM_ENTERSIZEMOVE) _pcRecordingPage.OnWindowMoveStarted();
            else if (msg == WM_EXITSIZEMOVE) _pcRecordingPage.OnWindowMoveEnded();

            return IntPtr.Zero;
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

            if (module == AppModules.PcRecording)
            {
                bool hasCamera = viewModel.RefreshCameraDeviceAvailability();
                if (hasCamera
                    && viewModel.Config.PcRecordingSetupVersion < AppConfig.CurrentPcRecordingSetupVersion
                    && !ConfigurePcRecording(viewModel))
                    module = AppModules.Overview;
            }
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
            SettingsModule.Visibility = module == AppModules.Settings ? Visibility.Visible : Visibility.Collapsed;
            UpdateNavigationState(module);

            if (module == AppModules.Overview)
                _statisticsPanel.Refresh();
            if (module == AppModules.VideoLibrary)
                _ = _videoLibraryPage.RefreshAsync();
            if (module == AppModules.Settings)
                _settingsPage.ReloadFromRuntime();

            if (module == AppModules.PcRecording)
                _pcRecordingPage.FocusScanInput();
            else
                _pcRecordingPage.DeactivateScanInput();
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

        private void Runtime_ModuleNavigationRequested(string module) =>
            Dispatcher.BeginInvoke(new Action(() => ShowModule(module)));

        private void RefreshModuleStates()
        {
            if (DataContext is not MainViewModel viewModel) return;
            OverviewPcStatus.Text = GetModuleStatus(viewModel.Config.PcRecordingSetupVersion, viewModel.Config.EnablePcCameraRecording, AppConfig.CurrentPcRecordingSetupVersion);
            OverviewMobileStatus.Text = GetModuleStatus(viewModel.Config.MobileBackupSetupVersion, viewModel.Config.EnableMobileBackup, AppConfig.CurrentMobileBackupSetupVersion);
            OverviewOrderStatus.Text = GetModuleStatus(viewModel.Config.OrderIntegrationSetupVersion, viewModel.Config.EnableOrderIntegration, AppConfig.CurrentOrderIntegrationSetupVersion);
            OverviewPcAction.Content = GetModuleActionText(viewModel.Config.PcRecordingSetupVersion, AppConfig.CurrentPcRecordingSetupVersion);
            OverviewMobileAction.Content = GetModuleActionText(viewModel.Config.MobileBackupSetupVersion, AppConfig.CurrentMobileBackupSetupVersion);
            OverviewOrderAction.Content = GetModuleActionText(viewModel.Config.OrderIntegrationSetupVersion, AppConfig.CurrentOrderIntegrationSetupVersion);
            ShellLanStatusText.Text = string.IsNullOrWhiteSpace(viewModel.MonitorAccessAddress)
                ? "局域网服务准备中"
                : $"局域网  {viewModel.MonitorAccessAddress}\n{viewModel.ConnectedDeviceText}";
            ShellVersionText.Text = $"版本 {AppVersion.Current}";
            _pcRecordingPage.RefreshState();
            _runtimeHost.MobileBackup.Refresh();
        }

        private static string GetModuleStatus(int setupVersion, bool enabled, int currentVersion) =>
            setupVersion < currentVersion ? "未配置" : enabled ? "运行正常" : "已暂停";

        private static string GetModuleActionText(int setupVersion, int currentVersion) =>
            setupVersion < currentVersion ? "开始配置" : "打开";

        private void UpdateNavigationState(string activeModule)
        {
            foreach (Button button in new[] { NavOverview, NavPcRecording, NavMobileBackup, NavOrderIntegration, NavVideoLibrary, NavSettings })
            {
                bool active = string.Equals(button.Tag as string, activeModule, StringComparison.Ordinal);
                button.Style = FindResource(active ? "PrimaryButtonStyle" : "SecondaryButtonStyle") as Style;
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Padding = new Thickness(16, 0, 16, 0);
            }
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout(e.NewSize.Width);

        private void UpdateResponsiveLayout(double width)
        {
            if (NavigationColumn == null) return;
            bool compact = width < 1080;
            NavigationColumn.Width = new GridLength(width < 860 ? 148 : compact ? 176 : 224);
            ShellBrandSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ShellBrandPanel.Margin = compact ? new Thickness(4, 0, 4, 16) : new Thickness(8, 0, 8, 24);
            OverviewCardsGrid.Columns = width < 1040 ? 1 : 3;
        }

        private static AppConfig CloneConfig(AppConfig config) =>
            JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config)) ?? new AppConfig();

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
            _pcRecordingPage.DeactivateScanInput();
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
            _pcRecordingPage.DeactivateScanInput();

            if (vm != null)
            {
                vm.BusyText = "正在关闭程序...";
                vm.IsBusy = true;
            }

            try
            {
                _runtimeHost.Main.ModuleNavigationRequested -= Runtime_ModuleNavigationRequested;
                _pcRecordingPage.Dispose();
                _videoLibraryPage.Dispose();
                _settingsPage.Dispose();
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
