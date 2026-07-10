using ExpressPackingMonitoring.Config;
using System.Windows;
using System.Windows.Media;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring;

public partial class PrintWorkstationWindow : Window
{
    private enum StatusVisual
    {
        Neutral,
        Success,
        Error
    }

    private readonly AppConfig _config;
    private readonly string _activeWorkstationRole = WorkstationRoles.PrintStation;
    private CancellationTokenSource? _findCts;

    public PrintWorkstationWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        AddressTextBox.Text = WorkstationNetwork.NormalizeAddress(_config.PrintStationMonitorAddress);
        Loaded += async (_, __) => await AutoConnectAndOpenAsync();
    }

    private async Task<bool> ReconnectAsync(bool save, bool openWhenConnected = false)
    {
        string address = WorkstationNetwork.NormalizeAddress(AddressTextBox.Text);
        AddressTextBox.Text = address;
        if (string.IsNullOrWhiteSpace(address))
        {
            SetStatus("未连接摄像头监控工位", "请自动查找，或输入摄像头监控工位底部状态栏显示的地址。", StatusVisual.Error);
            return false;
        }

        SetStatus("正在连接摄像头监控工位...", address);
        bool ok = await WorkstationNetwork.CanConnectAsync(address);
        if (ok)
        {
            if (save)
            {
                if (!WorkstationConfigStore.TryUpdate(
                        config => config.PrintStationMonitorAddress = address,
                        out AppConfig savedConfig,
                        out string saveError))
                {
                    SetStatus("监控工位地址保存失败", $"请检查磁盘空间或配置目录权限：{saveError}", StatusVisual.Error);
                    return false;
                }
                _config.PrintStationMonitorAddress = savedConfig.PrintStationMonitorAddress;
            }
            SetStatus("已连接到摄像头监控工位", $"已记住地址：{address}", StatusVisual.Success);
            if (openWhenConnected)
                WorkstationNetwork.OpenUrl(WorkstationNetwork.ToUrl(address));
            return true;
        }
        else
        {
            SetStatus("未找到摄像头监控工位", "请确认摄像头监控工位已打开，并且两台电脑在同一局域网。", StatusVisual.Error);
            return false;
        }
    }

    private async Task AutoConnectAndOpenAsync()
    {
        string savedAddress = WorkstationNetwork.NormalizeAddress(_config.PrintStationMonitorAddress);
        if (!string.IsNullOrWhiteSpace(savedAddress))
        {
            AddressTextBox.Text = savedAddress;
            if (await ReconnectAsync(true, openWhenConnected: true))
                return;
        }

        await FindMonitorAsync(openWhenFound: true);
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

    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await ReconnectAsync(true, openWhenConnected: true);

    private async void FindMonitor_Click(object sender, RoutedEventArgs e)
    {
        await FindMonitorAsync(openWhenFound: true);
    }

    private async Task FindMonitorAsync(bool openWhenFound)
    {
        _findCts?.Cancel();
        _findCts = new CancellationTokenSource();
        SetStatus("正在自动查找摄像头监控工位...", "查找过程可能需要几十秒。");
        var progress = new Progress<string>(msg => StatusHintTextBlock.Text = msg);

        try
        {
            string? address = await WorkstationNetwork.FindMonitorAsync(_config.WebServerPort, progress, _findCts.Token);
            if (address == null)
            {
                SetStatus("未找到摄像头监控工位", "请手动输入摄像头监控工位底部状态栏显示的地址。", StatusVisual.Error);
                return;
            }

            AddressTextBox.Text = address;
            await ReconnectAsync(true, openWhenConnected: openWhenFound);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消查找", "可以重新点击自动查找。", StatusVisual.Error);
        }
    }

    private void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        string address = WorkstationNetwork.NormalizeAddress(AddressTextBox.Text);
        if (string.IsNullOrWhiteSpace(address))
        {
            SetStatus("未连接摄像头监控工位", "请先连接后再打开视频页面。", StatusVisual.Error);
            return;
        }
        WorkstationNetwork.OpenUrl(WorkstationNetwork.ToUrl(address));
    }

    private void InstallTool_Click(object sender, RoutedEventArgs e)
    {
        string address = WorkstationNetwork.NormalizeAddress(AddressTextBox.Text);
        string guidePath = PrintToolInstallGuide.CreateLocalGuide(address);
        if (!string.IsNullOrWhiteSpace(address))
        {
            try { Clipboard.SetDataObject(address, true); } catch { }
        }

        WorkstationNetwork.OpenUrl(new Uri(guidePath).AbsoluteUri);
        bool hasAddress = !string.IsNullOrWhiteSpace(address);
        SetStatus("已打开安装向导",
            hasAddress ? $"已复制监控工位地址：{address}" : "请先连接摄像头监控工位，再按向导安装订单备注插件。",
            hasAddress ? StatusVisual.Success : StatusVisual.Error);
    }

    private async void TestSend_Click(object sender, RoutedEventArgs e)
    {
        string address = WorkstationNetwork.NormalizeAddress(AddressTextBox.Text);
        SetStatus("正在测试发送订单...", address);
        var result = await WorkstationNetwork.SendTestOrderAsync(address);
        if (result.MonitorConfirmed)
        {
            SetStatus("监控端已收到测试订单", "测试订单已发送，监控端会播报“收到测试订单”。", StatusVisual.Success);
        }
        else if (result.Sent)
        {
            SetStatus("测试订单已发送", "接口已返回成功，请在监控端确认是否播报。", StatusVisual.Success);
        }
        else
        {
            SetStatus("测试发送失败，请检查监控工位地址",
                "请确认监控端地址正确，并且两台电脑在同一局域网。",
                StatusVisual.Error);
        }
    }

    private void SwitchWorkstation_Click(object sender, RoutedEventArgs e)
    {
        var win = new WorkstationSelectionWindow { Owner = this };
        if (win.ShowDialog() == true && !string.IsNullOrWhiteSpace(win.SelectedRole))
        {
            if (string.Equals(_activeWorkstationRole, win.SelectedRole, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(_config.WorkstationRole, _activeWorkstationRole, StringComparison.OrdinalIgnoreCase))
                {
                    if (!WorkstationConfigStore.TryUpdate(
                            config => config.WorkstationRole = _activeWorkstationRole,
                            out AppConfig savedConfig,
                            out string saveError))
                    {
                        SetStatus("工位配置保存失败", saveError, StatusVisual.Error);
                        return;
                    }
                    _config.WorkstationRole = savedConfig.WorkstationRole;
                }
                SetStatus($"当前已经是{WorkstationRoles.GetDisplayName(_activeWorkstationRole)}", "无需重启或切换。", StatusVisual.Success);
                return;
            }

            string selectedRole = win.SelectedRole;
            if (!WorkstationConfigStore.TryUpdate(
                    config => config.WorkstationRole = selectedRole,
                    out AppConfig savedRoleConfig,
                    out string error))
            {
                SetStatus("工位配置保存失败", error, StatusVisual.Error);
                return;
            }
            _config.WorkstationRole = savedRoleConfig.WorkstationRole;
            WorkstationNetwork.AskRestart(this);
        }
    }
}
