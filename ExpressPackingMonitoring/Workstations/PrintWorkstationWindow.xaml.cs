using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Themes;
using ExpressPackingMonitoring.UI;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace ExpressPackingMonitoring;

public partial class PrintWorkstationWindow : Window
{
    private enum StatusVisual
    {
        Neutral,
        Success,
        Error
    }

    private AppConfig _config;
    private readonly bool _openPlaybackOnStartup;
    private readonly bool _requestLanAccessOnStartup;
    private readonly NoCameraWorkstationHost _host;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly string _activeWorkstationRole = WorkstationRoles.PrintStation;
    private readonly WindowCloseBehaviorController _closeBehaviorController;
    private StatisticsWindow? _statisticsWindow;
    private PlaybackWindow? _playbackWindow;
    private bool _loaded;
    private bool _exitRequestedFromTray;

    public PrintWorkstationWindow(
        AppConfig config,
        bool openPlaybackOnStartup = true,
        bool requestLanAccessOnStartup = true,
        bool enableCloseBehaviorPrompt = true)
    {
        InitializeComponent();
        _config = config;
        _openPlaybackOnStartup = openPlaybackOnStartup;
        _requestLanAccessOnStartup = requestLanAccessOnStartup;
        _host = new NoCameraWorkstationHost(config);
        _closeBehaviorController = new WindowCloseBehaviorController(
            this,
            RequestExitFromTray,
            enableCloseBehaviorPrompt);
        Loaded += Window_Loaded;
        Closing += Window_Closing;
        Closed += (_, _) =>
        {
            _closeBehaviorController.Dispose();
            _lifetimeCts.Cancel();
            _host.Dispose();
            _lifetimeCts.Dispose();
        };
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        WindowCloseChoice closeChoice = _closeBehaviorController.HandleClose(
            _config,
            bypassPreference: WorkstationNetwork.IsRestartPending || _exitRequestedFromTray);
        _exitRequestedFromTray = false;
        if (closeChoice != WindowCloseChoice.Exit)
        {
            e.Cancel = true;
            return;
        }

        CloseChildWindows();
    }

    private void RequestExitFromTray()
    {
        _exitRequestedFromTray = true;
        Close();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await StartServiceAsync();
    }

    private async Task StartServiceAsync()
    {
        SetControlsEnabled(false);
        SetStatus("正在启动手机录像备份服务", "正在打开录像数据库和本机回放服务");
        try
        {
            await _host.StartAsync(
                requestLanAccess: _requestLanAccessOnStartup,
                cancellationToken: _lifetimeCts.Token);
            RefreshServiceDisplay();
            if (_openPlaybackOnStartup)
                OpenLocalPlayback();
        }
        catch (Exception ex)
        {
            LanAddressTextBlock.Text = ex.Message;
            SetStatus("服务启动失败", ex.Message, StatusVisual.Error);
        }
        finally
        {
            SetControlsEnabled(_host.IsRunning);
            RepairLanButton.IsEnabled = true;
        }
    }

    private void RefreshServiceDisplay()
    {
        if (_host.IsLanAvailable)
        {
            LanAddressTextBlock.Text = _host.LanAccessUrl;
            SetStatus("手机录像备份服务已启动", "手机可备份录像到本机，本机和局域网设备均可回放", StatusVisual.Success);
        }
        else
        {
            LanAddressTextBlock.Text = string.IsNullOrWhiteSpace(_host.ErrorMessage)
                ? "仅本机可用"
                : _host.ErrorMessage;
            SetStatus("手机录像备份服务已启动 · 仅本机可用",
                "本机回放不受影响；需要手机备份或局域网回放时，请点击“修复局域网”",
                StatusVisual.Error);
        }

        ConnectPhoneButton.IsEnabled = _host.IsLanAvailable;
        CopyLanAddressButton.IsEnabled = _host.IsLanAvailable;
        RepairLanButton.Visibility = _host.IsLanAvailable ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetControlsEnabled(bool enabled)
    {
        OpenWebButton.IsEnabled = enabled;
        StatisticsButton.IsEnabled = _host.HasDatabase;
        PlaybackButton.IsEnabled = _host.HasDatabase;
        SettingsButton.IsEnabled = true;
        ConnectPhoneButton.IsEnabled = enabled && _host.IsLanAvailable;
        CopyLanAddressButton.IsEnabled = enabled && _host.IsLanAvailable;
    }

    private void SetStatus(string title, string hint, StatusVisual visual = StatusVisual.Neutral)
    {
        StatusTextBlock.Text = title;
        StatusHintTextBlock.Text = hint;

        string iconKey = visual switch
        {
            StatusVisual.Success => "FluentCheckIcon",
            StatusVisual.Error => "FluentDismissIcon",
            _ => "FluentHourglassIcon"
        };
        string brushKey = visual switch
        {
            StatusVisual.Success => "AccentGreen",
            StatusVisual.Error => "AccentRed",
            _ => "AccentBlue"
        };
        if (TryFindResource(iconKey) is Geometry icon)
            StatusIconPath.Data = icon;
        if (TryFindResource(brushKey) is Brush brush)
            StatusIconPath.Fill = brush;
    }

    private void OpenLocalPlayback()
    {
        if (!WorkstationNetwork.TryOpenUrl(_host.LocalPlaybackUrl, out string error))
            SetStatus("打开本机回放失败", error, StatusVisual.Error);
    }

    private void OpenWeb_Click(object sender, RoutedEventArgs e) => OpenLocalPlayback();

    private void OpenStatistics_Click(object sender, RoutedEventArgs e)
    {
        if (_statisticsWindow is { IsLoaded: true })
        {
            _statisticsWindow.Activate();
            return;
        }

        _statisticsWindow = new StatisticsWindow(_host.Database) { Owner = this };
        _statisticsWindow.Closed += (_, _) => _statisticsWindow = null;
        _statisticsWindow.Show();
    }

    private void OpenPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackWindow is { IsLoaded: true })
        {
            _playbackWindow.Activate();
            return;
        }

        _playbackWindow = new PlaybackWindow(_host.StoragePath, _host.Database, showDeletedVideos: true)
        {
            Owner = this
        };
        _playbackWindow.Closed += (_, _) => _playbackWindow = null;
        _playbackWindow.Show();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        AppConfig clonedConfig =
            JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(_config)) ?? new AppConfig();
        var context = new SettingsContext
        {
            Capabilities = SettingsCapabilities.ForRole(WorkstationRoles.PrintStation),
            ApplyAsync = ApplySettingsAsync,
            ConnectionAddressProvider = () => _host.IsLanAvailable ? _host.LanAccessUrl : _host.LocalPlaybackUrl,
            ShowMobileConnection = ShowMobileConnection,
            CopyMobileConnectionUrl = CopyMobileConnectionUrl
        };
        (double diskUsagePercent, string diskUsageText) = GetDiskUsage(_host.StoragePath);
        var window = new SettingsWindow(
            context,
            clonedConfig,
            diskUsagePercent,
            diskUsageText)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void ConnectPhone_Click(object sender, RoutedEventArgs e)
    {
        ShowMobileConnection(this);
    }

    private void ShowMobileConnection(Window owner)
    {
        var dialog = new MobileConnectionWindow(
            _host.LanAccessUrl,
            _config.RequireWebAccessKey,
            _host.IsLanAvailable ? "" : "当前仅本机可用，请先修复局域网",
            canOpenSettings: false)
        {
            Owner = owner
        };
        dialog.ShowDialog();
    }

    private void CopyLanAddress_Click(object sender, RoutedEventArgs e)
    {
        CopyMobileConnectionUrl();
    }

    private void CopyMobileConnectionUrl()
    {
        string address = _host.IsLanAvailable ? _host.LanAccessUrl : _host.LocalPlaybackUrl;
        if (string.IsNullOrWhiteSpace(address)) return;
        try
        {
            Clipboard.SetDataObject(address, true);
            SetStatus(
                _host.IsLanAvailable ? "已复制局域网地址" : "已复制本机回放地址",
                "访问地址包含手机配对密钥，请勿转发给无关人员",
                StatusVisual.Success);
        }
        catch (Exception ex)
        {
            SetStatus("复制局域网地址失败", ex.Message, StatusVisual.Error);
        }
    }

    private async void RepairLan_Click(object sender, RoutedEventArgs e)
    {
        RepairLanButton.IsEnabled = false;
        SetStatus("正在修复局域网", "Windows 可能会请求管理员授权");
        bool repaired = await _host.RepairLanAccessAsync(_lifetimeCts.Token);
        RefreshServiceDisplay();
        RepairLanButton.IsEnabled = true;
        if (!repaired && !_host.IsRunning)
            SetStatus("局域网修复失败", _host.ErrorMessage, StatusVisual.Error);
    }

    private string LocalOrderAddress => $"127.0.0.1:{_config.WebServerPort}";

    private async Task<bool> ApplySettingsAsync(AppConfig nextConfig)
    {
        AppConfig previousConfig = _config;
        AppConfig.NormalizeAfterLoad(nextConfig);

        if (!string.Equals(previousConfig.WorkstationRole, nextConfig.WorkstationRole, StringComparison.OrdinalIgnoreCase))
        {
            if (!TrySaveAndActivateConfig(previousConfig, nextConfig, out string error))
            {
                SetStatus("设置保存失败", error, StatusVisual.Error);
                MessageBox.Show(this, $"配置保存失败：{error}", "设置", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            WorkstationNetwork.AskRestart(this);
            return true;
        }

        _host.UpdateConfig(nextConfig);
        SetControlsEnabled(false);
        SetStatus("正在应用设置", "正在重启本机回放和手机备份服务");
        try
        {
            _playbackWindow?.Close();
            await _host.StartAsync(_requestLanAccessOnStartup, _lifetimeCts.Token);
            if (!TrySaveAndActivateConfig(previousConfig, nextConfig, out string error))
                throw new InvalidOperationException($"配置保存失败：{error}");

            RefreshServiceDisplay();
            SetControlsEnabled(true);
            return true;
        }
        catch (Exception ex)
        {
            _host.UpdateConfig(previousConfig);
            try
            {
                await _host.StartAsync(_requestLanAccessOnStartup, _lifetimeCts.Token);
                RefreshServiceDisplay();
                SetControlsEnabled(true);
            }
            catch
            {
                SetControlsEnabled(false);
            }

            RepairLanButton.IsEnabled = true;
            SetStatus("服务重启失败", ex.Message, StatusVisual.Error);
            MessageBox.Show(this, ex.Message, "服务重启失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool TrySaveAndActivateConfig(
        AppConfig previousConfig,
        AppConfig nextConfig,
        out string error)
    {
        if (!WorkstationConfigStore.TrySave(nextConfig, out error))
            return false;

        _config = nextConfig;
        _host.UpdateConfig(nextConfig);
        AutoStartService.Apply(nextConfig.AutoStartOnBoot);
        if (!string.Equals(previousConfig.Theme, nextConfig.Theme, StringComparison.Ordinal) &&
            Enum.TryParse(nextConfig.Theme, out AppTheme theme))
        {
            ThemeManager.ApplyTheme(theme);
        }

        return true;
    }

    private static (double Percent, string Text) GetDiskUsage(string storagePath)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(storagePath));
            if (string.IsNullOrWhiteSpace(root)) return (0, "暂不可用");
            var drive = new DriveInfo(root);
            if (drive.TotalSize <= 0) return (0, "暂不可用");
            double percent = Math.Clamp(
                (drive.TotalSize - drive.AvailableFreeSpace) * 100d / drive.TotalSize,
                0,
                100);
            double usedGB = (drive.TotalSize - drive.AvailableFreeSpace) / 1024d / 1024d / 1024d;
            double totalGB = drive.TotalSize / 1024d / 1024d / 1024d;
            return (percent, $"{usedGB:F1} / {totalGB:F1} GB");
        }
        catch
        {
            return (0, "暂不可用");
        }
    }

    private void CloseChildWindows()
    {
        try { _statisticsWindow?.Close(); } catch { }
        try { _playbackWindow?.Close(); } catch { }
        _statisticsWindow = null;
        _playbackWindow = null;
    }

    private void InstallTool_Click(object sender, RoutedEventArgs e)
    {
        string guidePath = PrintToolInstallGuide.CreateLocalGuide(LocalOrderAddress);
        try { Clipboard.SetDataObject(LocalOrderAddress, true); } catch { }
        WorkstationNetwork.OpenUrl(new Uri(guidePath).AbsoluteUri);
        SetStatus("已打开订单插件安装向导", "已复制本机接收地址，按向导完成安装即可", StatusVisual.Success);
    }

    private async void TestReceive_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("正在测试本机订单接收", LocalOrderAddress);
        WorkstationNetwork.TestOrderSendResult result =
            await WorkstationNetwork.SendTestOrderAsync(LocalOrderAddress);
        if (result.Sent)
        {
            SetStatus("本机已收到测试订单", "测试订单已写入录像数据库，可供手机录像关联", StatusVisual.Success);
        }
        else
        {
            SetStatus("测试接收失败",
                string.IsNullOrWhiteSpace(result.ErrorMessage) ? "请确认本机服务已正常启动" : result.ErrorMessage,
                StatusVisual.Error);
        }
    }

    private void SwitchWorkstation_Click(object sender, RoutedEventArgs e)
    {
        var window = new WorkstationSelectionWindow { Owner = this };
        if (window.ShowDialog() != true || string.IsNullOrWhiteSpace(window.SelectedRole))
            return;

        if (string.Equals(_activeWorkstationRole, window.SelectedRole, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("当前已选择不使用电脑摄像头", "无需重启或更改", StatusVisual.Success);
            return;
        }

        if (!WorkstationConfigStore.TryUpdate(
                config => config.WorkstationRole = window.SelectedRole,
                out AppConfig savedConfig,
                out string error))
        {
            SetStatus("录像方式保存失败", error, StatusVisual.Error);
            return;
        }

        _config.WorkstationRole = savedConfig.WorkstationRole;
        WorkstationNetwork.AskRestart(this);
    }
}
