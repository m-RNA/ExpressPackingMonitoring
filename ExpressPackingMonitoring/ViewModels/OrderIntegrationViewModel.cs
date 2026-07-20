using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;

namespace ExpressPackingMonitoring.ViewModels;

public sealed class OrderIntegrationViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _runtime;
    private readonly DashboardDataService _dashboard;
    private readonly DispatcherTimer _refreshTimer;
    private OrderTargetRowViewModel? _selectedTarget;
    private string _statusText = "准备中";
    private string _operationMessage = "";
    private bool _disposed;

    public OrderIntegrationViewModel(MainViewModel runtime, DashboardDataService dashboard)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        AddTargetCommand = new RelayCommand(AddTarget);
        DeleteTargetCommand = new RelayCommand(DeleteTarget, () => SelectedTarget is { IsLocal: false });
        SaveTargetsCommand = new RelayCommand(SaveTargets);
        ToggleEnabledCommand = new RelayCommand(ToggleEnabled);
        TestTargetCommand = new AsyncRelayCommand(TestTargetAsync, () => SelectedTarget != null);
        SendTestOrderCommand = new AsyncRelayCommand(SendTestOrderAsync);
        InstallScriptCommand = new RelayCommand(InstallScript);
        ReloadTargets();
        RefreshRecentOrders();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += (_, _) => RefreshRecentOrders();
        _refreshTimer.Start();
    }

    public ObservableCollection<OrderTargetRowViewModel> Targets { get; } = new();
    public ObservableCollection<RecentOrderDashboardItem> RecentOrders { get; } = new();

    public OrderTargetRowViewModel? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value)) return;
            (DeleteTargetCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (TestTargetCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string OperationMessage { get => _operationMessage; set => SetProperty(ref _operationMessage, value); }
    public string ToggleText => _runtime.Config.EnableOrderIntegration ? "暂停联动" : "恢复联动";
    public string ConnectionSummary => _runtime.ConnectedDeviceText;

    public ICommand AddTargetCommand { get; }
    public ICommand DeleteTargetCommand { get; }
    public ICommand SaveTargetsCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand TestTargetCommand { get; }
    public ICommand SendTestOrderCommand { get; }
    public ICommand InstallScriptCommand { get; }

    public void ReloadTargets()
    {
        Targets.Clear();
        foreach (OrderIntegrationTarget target in _runtime.Config.OrderIntegrationTargets)
            Targets.Add(new OrderTargetRowViewModel(target));
        if (!Targets.Any(target => target.IsLocal))
        {
            Targets.Insert(0, new OrderTargetRowViewModel(new OrderIntegrationTarget
            {
                DisplayName = "当前电脑",
                Address = "local",
                Enabled = true,
                IsLocal = true
            }));
        }
        SelectedTarget = Targets.FirstOrDefault();
        RefreshStatus();
    }

    private void AddTarget()
    {
        var row = new OrderTargetRowViewModel(new OrderIntegrationTarget
        {
            DisplayName = $"录像电脑 {Targets.Count(target => !target.IsLocal) + 1}",
            Enabled = true
        });
        Targets.Add(row);
        SelectedTarget = row;
        OperationMessage = "请填写目标电脑的 IP:端口，然后测试连接";
    }

    private void DeleteTarget()
    {
        if (SelectedTarget is not { IsLocal: false } target) return;
        Targets.Remove(target);
        SelectedTarget = Targets.FirstOrDefault();
        OperationMessage = "目标已从编辑列表移除，点击保存配置后生效";
    }

    private async Task TestTargetAsync()
    {
        if (SelectedTarget == null) return;
        string address = ResolveAddress(SelectedTarget);
        if (string.IsNullOrWhiteSpace(address))
        {
            SelectedTarget.TestResult = "请先填写地址";
            return;
        }

        SelectedTarget.TestResult = "正在测试";
        bool ok = await WorkstationNetwork.CanConnectAsync(address);
        SelectedTarget.TestResult = ok ? "连接成功" : "连接失败，请检查地址和防火墙";
    }

    private async Task SendTestOrderAsync()
    {
        IReadOnlyList<OrderTargetRowViewModel> enabledTargets = Targets.Where(target => target.Enabled).ToList();
        if (enabledTargets.Count == 0)
        {
            OperationMessage = "请至少启用一个接收目标";
            return;
        }

        OperationMessage = "正在向各目标发送测试订单";
        foreach (OrderTargetRowViewModel target in enabledTargets)
        {
            string address = ResolveAddress(target);
            WorkstationNetwork.TestOrderSendResult result = await WorkstationNetwork.SendTestOrderAsync(address);
            target.TestResult = result.Sent && result.MonitorConfirmed
                ? "已确认收到测试订单"
                : result.Sent ? "已发送，等待目标确认" : $"发送失败：{result.ErrorMessage}";
        }
        OperationMessage = "测试完成，请查看各目标确认结果";
        RefreshRecentOrders();
    }

    private void SaveTargets()
    {
        var saved = new List<OrderIntegrationTarget>();
        foreach (OrderTargetRowViewModel row in Targets)
        {
            if (!row.IsLocal && string.IsNullOrWhiteSpace(row.Address))
            {
                OperationMessage = $"请填写“{row.DisplayName}”的地址，或将其删除";
                return;
            }
            saved.Add(row.ToModel(_runtime.Config.WebServerPort));
        }
        if (!saved.Any(target => target.Enabled))
        {
            OperationMessage = "请至少启用一个接收目标";
            return;
        }

        AppConfig next = CloneConfig(_runtime.Config);
        next.OrderIntegrationTargets = saved;
        next.EnableOrderIntegration = true;
        next.OrderIntegrationSetupVersion = AppConfig.CurrentOrderIntegrationSetupVersion;
        if (_runtime.ApplyModuleConfiguration(next))
        {
            OperationMessage = "订单联动配置已保存";
            ReloadTargets();
        }
    }

    private void ToggleEnabled()
    {
        AppConfig next = CloneConfig(_runtime.Config);
        next.EnableOrderIntegration = !next.EnableOrderIntegration;
        if (_runtime.ApplyModuleConfiguration(next))
        {
            OperationMessage = next.EnableOrderIntegration ? "已恢复订单联动" : "已暂停接收订单";
            RefreshStatus();
        }
    }

    private void InstallScript()
    {
        IEnumerable<string> addresses = Targets
            .Where(target => target.Enabled)
            .Select(ResolveAddress)
            .Where(address => !string.IsNullOrWhiteSpace(address));
        string guidePath = PrintToolInstallGuide.CreateLocalGuide(string.Join(',', addresses));
        WorkstationNetwork.OpenUrl(new Uri(guidePath).AbsoluteUri);
        OperationMessage = "已打开油猴脚本安装与更新页面";
    }

    private void RefreshRecentOrders()
    {
        if (_disposed) return;
        RecentOrders.Clear();
        foreach (RecentOrderDashboardItem item in _dashboard.GetRecentOrders()) RecentOrders.Add(item);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        bool configured = _runtime.Config.OrderIntegrationSetupVersion >= AppConfig.CurrentOrderIntegrationSetupVersion;
        StatusText = !configured ? "未配置" : _runtime.Config.EnableOrderIntegration ? "运行正常" : "已暂停";
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(ConnectionSummary));
    }

    private string ResolveAddress(OrderTargetRowViewModel target) => target.IsLocal
        ? WorkstationNetwork.GetBestLocalAccessAddress(_runtime.Config.WebServerPort)
        : WorkstationNetwork.NormalizeAddress(target.Address, _runtime.Config.WebServerPort);

    private static AppConfig CloneConfig(AppConfig config) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config)) ?? new AppConfig();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
    }
}

public sealed class OrderTargetRowViewModel : ObservableObject
{
    private string _displayName;
    private string _address;
    private bool _enabled;
    private string _testResult = "尚未测试";

    public OrderTargetRowViewModel(OrderIntegrationTarget target)
    {
        Id = string.IsNullOrWhiteSpace(target.Id) ? Guid.NewGuid().ToString("N") : target.Id;
        _displayName = target.DisplayName;
        _address = target.IsLocal ? "当前电脑" : target.Address;
        _enabled = target.Enabled;
        IsLocal = target.IsLocal;
    }

    public string Id { get; }
    public bool IsLocal { get; }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public string Address { get => _address; set { if (!IsLocal) SetProperty(ref _address, value); } }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string TestResult { get => _testResult; set => SetProperty(ref _testResult, value); }

    public OrderIntegrationTarget ToModel(int defaultPort) => new()
    {
        Id = Id,
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? (IsLocal ? "当前电脑" : "录像电脑") : DisplayName.Trim(),
        Address = IsLocal ? "local" : WorkstationNetwork.NormalizeAddress(Address, defaultPort),
        Enabled = Enabled,
        IsLocal = IsLocal
    };
}
