using System.IO;
using System.Windows;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring;

public partial class PrintWorkstationWindow : Window
{
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
            SetStatus("未连接摄像头监控工位", "请自动查找，或输入摄像头监控工位底部状态栏显示的地址。");
            return false;
        }

        SetStatus("正在连接摄像头监控工位...", address);
        bool ok = await WorkstationNetwork.CanConnectAsync(address);
        if (ok)
        {
            if (save)
            {
                _config.PrintStationMonitorAddress = address;
                WorkstationConfigStore.Save(_config);
            }
            SetStatus("已连接到摄像头监控工位", $"已记住地址：{address}");
            if (openWhenConnected)
                WorkstationNetwork.OpenUrl(WorkstationNetwork.ToUrl(address));
            return true;
        }
        else
        {
            SetStatus("未找到摄像头监控工位", "请确认摄像头监控工位已打开，并且两台电脑在同一局域网。");
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

    private void SetStatus(string title, string hint)
    {
        StatusTextBlock.Text = title;
        StatusHintTextBlock.Text = hint;
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
                SetStatus("未找到摄像头监控工位", "请手动输入摄像头监控工位底部状态栏显示的地址。");
                return;
            }

            AddressTextBox.Text = address;
            await ReconnectAsync(true, openWhenConnected: openWhenFound);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消查找", "可以重新点击自动查找。");
        }
    }

    private void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        string address = WorkstationNetwork.NormalizeAddress(AddressTextBox.Text);
        if (string.IsNullOrWhiteSpace(address))
        {
            SetStatus("未连接摄像头监控工位", "请先连接后再打开视频页面。");
            return;
        }
        WorkstationNetwork.OpenUrl(WorkstationNetwork.ToUrl(address));
    }

    private void InstallTool_Click(object sender, RoutedEventArgs e)
    {
        string scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Scripts", "快递助手订单推送.user.js"));
        if (!File.Exists(scriptPath))
            scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Scripts", "快递助手订单推送.user.js"));

        WorkstationNetwork.OpenUrl("https://www.tampermonkey.net/");
        if (File.Exists(scriptPath))
            WorkstationNetwork.OpenUrl(scriptPath);
        SetStatus("已打开安装页面", "安装脚本后，请在脚本菜单里填写摄像头监控工位地址。");
    }

    private async void TestSend_Click(object sender, RoutedEventArgs e)
    {
        string address = WorkstationNetwork.NormalizeAddress(AddressTextBox.Text);
        SetStatus("正在测试发送订单...", address);
        bool ok = await WorkstationNetwork.SendTestOrderAsync(address);
        SetStatus(ok ? "测试订单已发送" : "测试发送失败", ok ? "请到摄像头监控工位查看是否收到测试订单。" : "请先确认地址和网络连接。");
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
                    _config.WorkstationRole = _activeWorkstationRole;
                    WorkstationConfigStore.Save(_config);
                }
                SetStatus($"当前已经是{WorkstationRoles.GetDisplayName(_activeWorkstationRole)}", "无需重启或切换。");
                return;
            }

            _config.WorkstationRole = win.SelectedRole;
            WorkstationConfigStore.Save(_config);
            WorkstationNetwork.AskRestart(this);
        }
    }
}
