using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ExpressPackingMonitoring.UI.Pages;

public partial class PcRecordingPage : UserControl, IDisposable
{
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const int VkCapital = 0x14;
    private readonly MainViewModel _runtime;
    private readonly DispatcherTimer _capsCheckTimer;
    private readonly DispatcherTimer _scanAutoSubmitTimer;
    private readonly List<double> _scanInputIntervalsMs = new();
    private Window? _hostWindow;
    private bool _capsLockStateBeforeFocus;
    private bool _capsLockOverridden;
    private bool _capsLockSuspended;
    private bool _attached;
    private bool _disposed;
    private DateTime _lastMouseActivityNotifyAt = DateTime.MinValue;
    private DateTime _lastScanInputCharAt = DateTime.MinValue;
    private int _lastScanInputLength;

    public PcRecordingPage(MainViewModel runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        InitializeComponent();
        DataContext = runtime;
        BtnMobileConnection.Click += BtnMobileConnection_Click;
        BtnMobileConnection.PreviewMouseLeftButtonUp += BtnMobileConnection_PreviewMouseLeftButtonUp;
        _capsCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _capsCheckTimer.Tick += CapsCheckTimer_Tick;
        _scanAutoSubmitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _scanAutoSubmitTimer.Tick += ScanAutoSubmitTimer_Tick;
        Loaded += PcRecordingPage_Loaded;
        SizeChanged += PcRecordingPage_SizeChanged;
        VideoImage.SizeChanged += (_, _) => UpdateCameraOverlays();
        _runtime.PropertyChanged += Runtime_PropertyChanged;
        RefreshState();
    }

    public event EventHandler<string>? ModuleNavigationRequested;

    public void FocusScanInput()
    {
        if (_disposed) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsVisible) return;
            ScanInputTextBox.Focus();
            ApplyCapsLockForScanInput();
            UpdateCameraOverlays();
        }));
    }

    public void DeactivateScanInput()
    {
        _capsCheckTimer.Stop();
        RestoreCapsLockState();
    }

    public void SuspendCapsLockForModalWindow()
    {
        _capsLockSuspended = true;
        DeactivateScanInput();
    }

    public void ResumeCapsLockAfterModalWindow()
    {
        _capsLockSuspended = false;
        FocusScanInput();
    }

    public void OnWindowMoveStarted() => _runtime.SuppressVideoPreviewUpdates = true;

    public void OnWindowMoveEnded()
    {
        _runtime.ResumeVideoPreviewUpdatesAfterWindowMove();
        UpdateCameraOverlays();
    }

    public void RefreshState()
    {
        if (CameraNoDeviceOverlay == null || PcRecordingControlOverlay == null) return;

        CameraNoDeviceOverlay.Visibility = _runtime.HasNoCameraDevice ? Visibility.Visible : Visibility.Collapsed;
        if (_runtime.HasNoCameraDevice)
        {
            PcRecordingControlOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        bool enabled = _runtime.Config.EnablePcCameraRecording;
        PcRecordingControlIcon.Data = FindResource(enabled ? "FluentPauseIcon" : "FluentPlayIcon") as System.Windows.Media.Geometry;
        PcRecordingControlText.Text = enabled
            ? _runtime.IsRecording ? "录像中，停止后可停用" : "点击停用电脑录像"
            : "点击启用电脑录像";
        BtnPreviewTogglePcRecording.IsEnabled = !enabled || !_runtime.IsRecording;
        PcRecordingControlOverlay.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PcRecordingPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true;
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow != null)
        {
            _hostWindow.Activated += HostWindow_Activated;
            _hostWindow.Deactivated += HostWindow_Deactivated;
            _hostWindow.StateChanged += HostWindow_StateChanged;
            _hostWindow.PreviewMouseMove += HostWindow_PreviewMouseMove;
            _hostWindow.PreviewKeyDown += HostWindow_PreviewKeyDown;
        }
        FocusScanInput();
    }

    private void Runtime_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.LastZoomRect)
            or nameof(MainViewModel.CameraFrameSize)
            or nameof(MainViewModel.IsCameraBarcodeRecognitionEnabled))
        {
            Dispatcher.BeginInvoke(new Action(UpdateCameraOverlays));
        }
        else if (e.PropertyName is nameof(MainViewModel.Config)
            or nameof(MainViewModel.IsRecording)
            or nameof(MainViewModel.CameraAvailability))
        {
            RefreshState();
        }
    }

    private void HostWindow_Activated(object? sender, EventArgs e)
    {
        _capsLockStateBeforeFocus = IsCapsLockOn();
        _capsLockOverridden = false;
        if (IsVisible) ApplyCapsLockForScanInput();
        _runtime.NotifyUserActivity();
    }

    private void HostWindow_Deactivated(object? sender, EventArgs e) => DeactivateScanInput();

    private void HostWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_hostWindow?.WindowState == WindowState.Minimized)
            DeactivateScanInput();
        else if (IsVisible)
            ApplyCapsLockForScanInput();
    }

    private void HostWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastMouseActivityNotifyAt < TimeSpan.FromSeconds(1)) return;
        _lastMouseActivityNotifyAt = now;
        _runtime.NotifyUserActivity();
    }

    private void HostWindow_PreviewKeyDown(object sender, KeyEventArgs e) => _runtime.NotifyUserActivity();

    private static bool IsCapsLockOn() => (GetKeyState(VkCapital) & 1) != 0;

    private static void ToggleCapsLock()
    {
        keybd_event((byte)VkCapital, 0x45, 0, UIntPtr.Zero);
        keybd_event((byte)VkCapital, 0x45, 2, UIntPtr.Zero);
    }

    private void EnsureCapsLockOn()
    {
        if (IsCapsLockOn()) return;
        ToggleCapsLock();
        _capsLockOverridden = true;
    }

    private void RestoreCapsLockState()
    {
        if (_capsLockOverridden && !_capsLockStateBeforeFocus && IsCapsLockOn()) ToggleCapsLock();
        _capsLockOverridden = false;
    }

    private bool ShouldForceCapsLock() =>
        !_capsLockSuspended
        && IsVisible
        && _hostWindow?.IsActive == true
        && _hostWindow.WindowState != WindowState.Minimized
        && ScanInputTextBox.IsFocused;

    private void ApplyCapsLockForScanInput()
    {
        if (!ShouldForceCapsLock())
        {
            _capsCheckTimer.Stop();
            return;
        }
        if (!_capsLockOverridden) _capsLockStateBeforeFocus = IsCapsLockOn();
        EnsureCapsLockOn();
        if (string.IsNullOrEmpty(ScanInputTextBox.Text)) _capsCheckTimer.Start();
    }

    private void CapsCheckTimer_Tick(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(ScanInputTextBox.Text)) ApplyCapsLockForScanInput();
        else _capsCheckTimer.Stop();
    }

    private void UpdateCameraOverlays()
    {
        UpdateZoomBorder(_runtime.LastZoomRect);
        UpdateCameraBarcodeGuide();
    }

    private void UpdateZoomBorder(Rect zoomRect)
    {
        if (zoomRect == Rect.Empty || _runtime.CameraFrameSize.Width <= 0 || _runtime.CameraFrameSize.Height <= 0)
        {
            ZoomPreviewBorder.Visibility = Visibility.Collapsed;
            return;
        }
        double actualWidth = VideoImage.ActualWidth;
        double actualHeight = VideoImage.ActualHeight;
        if (actualWidth <= 0 || actualHeight <= 0) return;
        double scale = Math.Min(actualWidth / _runtime.CameraFrameSize.Width, actualHeight / _runtime.CameraFrameSize.Height);
        ZoomPreviewBorder.Width = zoomRect.Width * scale;
        ZoomPreviewBorder.Height = zoomRect.Height * scale;
        ZoomPreviewBorder.Visibility = Visibility.Visible;
    }

    private void UpdateCameraBarcodeGuide()
    {
        double sourceWidth = _runtime.CameraFrameSize.Width;
        double sourceHeight = _runtime.CameraFrameSize.Height;
        double actualWidth = VideoImage.ActualWidth;
        double actualHeight = VideoImage.ActualHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0 || actualWidth <= 0 || actualHeight <= 0)
        {
            CameraBarcodeGuide.Width = 0;
            CameraBarcodeGuide.Height = 0;
            return;
        }
        double scale = Math.Min(actualWidth / sourceWidth, actualHeight / sourceHeight);
        CameraBarcodeGuide.Width = sourceWidth * CameraBarcodeFrameDecoder.GuideWidthRatio * scale;
        CameraBarcodeGuide.Height = sourceHeight * CameraBarcodeFrameDecoder.GuideHeightRatio * scale;
    }

    private void PcRecordingPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 920;
        RecordingSidebar.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RecordingSidebarColumn.Width = compact ? new GridLength(0) : new GridLength(360);
        RecordingMainColumn.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 24, 0);
        RecordingLayout.Margin = compact ? new Thickness(14) : new Thickness(24);
    }

    private void BtnMobileConnection_Click(object sender, RoutedEventArgs e)
    {
        RequestModule(AppModules.MobileBackup);
        e.Handled = true;
    }

    private void BtnMobileConnection_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        RequestModule(AppModules.MobileBackup);
        e.Handled = true;
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string module }) RequestModule(module);
        e.Handled = true;
    }

    private void RequestModule(string module) => ModuleNavigationRequested?.Invoke(this, module);

    private void BtnTogglePcRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime.IsRecording && _runtime.Config.EnablePcCameraRecording)
        {
            MessageBox.Show(Window.GetWindow(this), "请先安全完成当前录像，再暂停电脑录像", "正在录像", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AppConfig next = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(_runtime.Config)) ?? new AppConfig();
        next.EnablePcCameraRecording = !next.EnablePcCameraRecording;
        if (_runtime.ApplyModuleConfiguration(next)) RefreshState();
    }

    private void BtnRefreshCamera_Click(object sender, RoutedEventArgs e)
    {
        if (!_runtime.RefreshCameraDeviceAvailability())
        {
            _runtime.ShowToast("仍未检测到摄像头，请检查连接");
            RefreshState();
            return;
        }

        if (_runtime.Config.PcRecordingSetupVersion < AppConfig.CurrentPcRecordingSetupVersion)
        {
            RequestModule(AppModules.PcRecording);
            return;
        }

        if (_runtime.Config.EnablePcCameraRecording)
            _runtime.ManualRestartCamera();
        RefreshState();
    }

    private void CameraPreviewBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_runtime.HasNoCameraDevice || !_runtime.Config.EnablePcCameraRecording) return;
        PcRecordingControlOverlay.Visibility = Visibility.Visible;
    }

    private void CameraPreviewBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_runtime.Config.EnablePcCameraRecording)
            PcRecordingControlOverlay.Visibility = Visibility.Collapsed;
    }

    private void ScanInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ResetScanAutoSubmitState();
        string scanResult = ScanInputTextBox.Text.Trim();
        if (_runtime.ScanCommand.CanExecute(scanResult)) _runtime.ScanCommand.Execute(scanResult);
        e.Handled = true;
    }

    private void ScanInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_runtime.Config.EnableScannerAutoSubmit)
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

        DateTime now = DateTime.Now;
        int sequenceBreakMs = Math.Max(100, _runtime.Config.ScannerAutoSubmitMaxKeyIntervalMs);
        for (int index = 0; index < addedCount; index++)
        {
            if (_lastScanInputCharAt != DateTime.MinValue)
            {
                double elapsed = (now - _lastScanInputCharAt).TotalMilliseconds;
                if (elapsed > sequenceBreakMs) _scanInputIntervalsMs.Clear();
                else _scanInputIntervalsMs.Add(elapsed);
            }
            _lastScanInputCharAt = now;
        }
        _lastScanInputLength = text.Length;
        ScheduleScanAutoSubmitCheck(_runtime.Config.ScannerAutoSubmitQuietMs);
    }

    private void ScheduleScanAutoSubmitCheck(int quietMilliseconds)
    {
        _scanAutoSubmitTimer.Stop();
        _scanAutoSubmitTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(quietMilliseconds, 120, 600));
        _scanAutoSubmitTimer.Start();
    }

    private void ScanAutoSubmitTimer_Tick(object? sender, EventArgs e)
    {
        _scanAutoSubmitTimer.Stop();
        if (!_runtime.Config.EnableScannerAutoSubmit) return;
        if ((DateTime.Now - _lastScanInputCharAt).TotalMilliseconds < _runtime.Config.ScannerAutoSubmitQuietMs)
        {
            ScheduleScanAutoSubmitCheck(_runtime.Config.ScannerAutoSubmitQuietMs);
            return;
        }

        string scanResult = ScanInputTextBox.Text.Trim();
        if (scanResult.Length < _runtime.Config.ScannerAutoSubmitMinLength
            || !_runtime.IsAutoSubmitScanCandidate(scanResult)
            || !ScannerAutoSubmitPolicy.IsFastSequence(
                _scanInputIntervalsMs,
                scanResult.Length,
                _runtime.Config.ScannerAutoSubmitMaxAverageIntervalMs,
                _runtime.Config.ScannerAutoSubmitMaxKeyIntervalMs))
        {
            return;
        }

        ResetScanAutoSubmitState();
        if (_runtime.ScanCommand.CanExecute(scanResult)) _runtime.ScanCommand.Execute(scanResult);
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
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_capsLockSuspended && IsVisible && _hostWindow?.IsActive == true) ScanInputTextBox.Focus();
        }));
    }

    private void ScanInputTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        ApplyCapsLockForScanInput();
        Dispatcher.BeginInvoke(new Action(ScanInputTextBox.SelectAll));
    }

    private void ScanInputTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ScanInputTextBox.IsKeyboardFocusWithin) return;
        e.Handled = true;
        ScanInputTextBox.Focus();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeactivateScanInput();
        _scanAutoSubmitTimer.Stop();
        _runtime.PropertyChanged -= Runtime_PropertyChanged;
        if (_hostWindow != null)
        {
            _hostWindow.Activated -= HostWindow_Activated;
            _hostWindow.Deactivated -= HostWindow_Deactivated;
            _hostWindow.StateChanged -= HostWindow_StateChanged;
            _hostWindow.PreviewMouseMove -= HostWindow_PreviewMouseMove;
            _hostWindow.PreviewKeyDown -= HostWindow_PreviewKeyDown;
        }
    }
}
