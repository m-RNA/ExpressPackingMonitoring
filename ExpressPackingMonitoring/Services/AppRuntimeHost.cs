using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// Owns the long-lived application runtime. Module pages observe this runtime;
/// they never create or dispose cameras, databases, or LAN services themselves.
/// </summary>
public sealed class AppRuntimeHost : IDisposable
{
    private int _disposed;

    public AppRuntimeHost()
        : this(new MainViewModel())
    {
    }

    internal AppRuntimeHost(MainViewModel mainViewModel)
    {
        Main = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        Dashboard = new DashboardDataService(Main.Database);
        MobileBackup = new MobileBackupViewModel(Main, Dashboard);
        OrderIntegration = new OrderIntegrationViewModel(Main, Dashboard);
        InstanceId = Guid.NewGuid();
    }

    public Guid InstanceId { get; }

    public MainViewModel Main { get; }

    public DashboardDataService Dashboard { get; }

    public MobileBackupViewModel MobileBackup { get; }

    public OrderIntegrationViewModel OrderIntegration { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        MobileBackup.Dispose();
        OrderIntegration.Dispose();
        Main.Dispose();
    }
}
