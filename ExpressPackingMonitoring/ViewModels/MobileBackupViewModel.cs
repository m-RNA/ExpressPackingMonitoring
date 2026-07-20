using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ExpressPackingMonitoring.ViewModels;

public sealed class MobileBackupViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _runtime;
    private readonly DashboardDataService _dashboard;
    private readonly DispatcherTimer _refreshTimer;
    private string _statusText = "准备中";
    private string _accessUrl = "";
    private string _connectionMessage = "局域网服务正在准备";
    private string _deviceSummary = "暂无手机连接";
    private int _connectedMobileDeviceCount;
    private int _todayVideoCount;
    private string _operationMessage = "";
    private BitmapSource? _qrCode;
    private bool _disposed;

    public MobileBackupViewModel(MainViewModel runtime, DashboardDataService dashboard)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        ToggleEnabledCommand = new RelayCommand(ToggleEnabled);
        RefreshCommand = new RelayCommand(Refresh);
        _runtime.PropertyChanged += Runtime_PropertyChanged;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
        Refresh();
    }

    public ObservableCollection<MobileUploadDashboardItem> Uploads { get; } = new();
    public ObservableCollection<RecentMobileVideoItem> RecentVideos { get; } = new();
    public ObservableCollection<string> ConnectedDevices { get; } = new();

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string AccessUrl { get => _accessUrl; private set => SetProperty(ref _accessUrl, value); }
    public string ConnectionMessage { get => _connectionMessage; private set => SetProperty(ref _connectionMessage, value); }
    public string DeviceSummary { get => _deviceSummary; private set => SetProperty(ref _deviceSummary, value); }
    public int ConnectedMobileDeviceCount
    {
        get => _connectedMobileDeviceCount;
        private set
        {
            if (SetProperty(ref _connectedMobileDeviceCount, value))
                OnPropertyChanged(nameof(ConnectedMobileDeviceCountText));
        }
    }
    public string ConnectedMobileDeviceCountText => AppLanguage.Format("Main.MobileDeviceCount", ConnectedMobileDeviceCount);
    public int TodayVideoCount
    {
        get => _todayVideoCount;
        private set
        {
            if (SetProperty(ref _todayVideoCount, value))
                OnPropertyChanged(nameof(TodayVideoCountText));
        }
    }
    public string TodayVideoCountText => AppLanguage.Format("Main.TodayMobileVideoCount", TodayVideoCount);
    public string OperationMessage { get => _operationMessage; set => SetProperty(ref _operationMessage, value); }
    public BitmapSource? QrCode { get => _qrCode; private set => SetProperty(ref _qrCode, value); }
    public bool IsConfigured => _runtime.Config.MobileBackupSetupVersion >= AppConfig.CurrentMobileBackupSetupVersion;
    public bool IsEnabled => _runtime.Config.EnableMobileBackup;
    public bool IsConnectionReady => !string.IsNullOrWhiteSpace(AccessUrl);
    public bool ContainsAccessKey => MobileConnectionService.ContainsAccessKey(AccessUrl);
    public string ToggleText => IsEnabled ? "暂停备份" : "恢复备份";
    public string UploadSummary => Uploads.Count == 0 ? "当前没有上传任务" : $"{Uploads.Count} 个任务正在上传或等待校验";
    public ICommand ToggleEnabledCommand { get; }
    public ICommand RefreshCommand { get; }

    public void Refresh()
    {
        if (_disposed) return;

        StatusText = !IsConfigured ? "未配置" : IsEnabled ? "运行正常" : "已暂停";
        RefreshConnection();
        Replace(Uploads, _dashboard.GetMobileUploads());
        Replace(RecentVideos, _dashboard.GetRecentMobileVideos());
        TodayVideoCount = _dashboard.GetTodayMobileVideoCount();
        RefreshDevices();
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsConnectionReady));
        OnPropertyChanged(nameof(ContainsAccessKey));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(UploadSummary));
    }

    private void RefreshConnection()
    {
        string previousUrl = AccessUrl;
        if (_runtime.TryGetMobileConnectionUrl(out string url))
        {
            AccessUrl = url;
            ConnectionMessage = ContainsAccessKey
                ? "连接地址包含访问密钥，请不要转发给无关人员"
                : "手机和电脑连接同一局域网后即可扫码";
            if (!string.Equals(previousUrl, url, StringComparison.Ordinal))
                QrCode = MobileConnectionService.CreateQrBitmap(url, 220);
        }
        else
        {
            AccessUrl = "";
            QrCode = null;
            ConnectionMessage = string.IsNullOrWhiteSpace(_runtime.MonitorAccessAddress)
                ? "局域网服务尚未准备完成，请检查服务、端口和防火墙设置"
                : "当前没有可供其他设备访问的局域网地址";
        }
    }

    private void RefreshDevices()
    {
        ConnectedDevices.Clear();
        ConnectedClientInfo[] mobileClients = _runtime.GetConnectedClientsSnapshot()
            .Where(client => client.ClientType is "mobile-app" or "web-mobile")
            .ToArray();
        foreach (string name in mobileClients
                     .Select(client => string.IsNullOrWhiteSpace(client.DisplayName) ? "手机设备" : client.DisplayName)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(name => name))
        {
            ConnectedDevices.Add(name);
        }

        ConnectedMobileDeviceCount = ConnectedClientRegistry.CountDistinctAddresses(mobileClients);
        DeviceSummary = ConnectedMobileDeviceCount == 0
            ? "暂无手机连接"
            : $"已连接 {ConnectedMobileDeviceCount} 台手机";
    }

    private void ToggleEnabled()
    {
        AppConfig next = CloneConfig(_runtime.Config);
        next.EnableMobileBackup = !next.EnableMobileBackup;
        if (_runtime.ApplyModuleConfiguration(next))
        {
            OperationMessage = next.EnableMobileBackup ? "已恢复手机备份" : "已暂停接收新的备份任务";
            Refresh();
        }
    }

    private void Runtime_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Config)
            or nameof(MainViewModel.MonitorAccessAddress)
            or nameof(MainViewModel.ConnectedDeviceText))
        {
            Refresh();
        }
    }

    private static AppConfig CloneConfig(AppConfig config) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config)) ?? new AppConfig();

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (T value in values) target.Add(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        _runtime.PropertyChanged -= Runtime_PropertyChanged;
    }
}
