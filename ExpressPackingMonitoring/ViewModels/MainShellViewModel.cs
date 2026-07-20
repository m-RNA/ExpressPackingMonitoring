using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExpressPackingMonitoring.Config;
using System.Windows.Input;

namespace ExpressPackingMonitoring.ViewModels;

/// <summary>
/// Navigation state for the unified shell. Business services remain owned by
/// AppRuntimeHost and are independent from the selected page.
/// </summary>
public sealed class MainShellViewModel : ObservableObject
{
    private static readonly HashSet<string> KnownModules = new(StringComparer.Ordinal)
    {
        AppModules.Overview,
        AppModules.PcRecording,
        AppModules.MobileBackup,
        AppModules.OrderIntegration,
        AppModules.VideoLibrary,
        AppModules.Settings
    };

    private string _currentModule;

    public MainShellViewModel(string? initialModule = null)
    {
        _currentModule = NormalizeModule(initialModule);
        NavigateCommand = new RelayCommand<string>(Navigate);
    }

    public string CurrentModule
    {
        get => _currentModule;
        private set
        {
            if (!SetProperty(ref _currentModule, NormalizeModule(value)))
                return;

            OnPropertyChanged(nameof(IsOverviewActive));
            OnPropertyChanged(nameof(IsPcRecordingActive));
            OnPropertyChanged(nameof(IsMobileBackupActive));
            OnPropertyChanged(nameof(IsOrderIntegrationActive));
            OnPropertyChanged(nameof(IsVideoLibraryActive));
            OnPropertyChanged(nameof(IsSettingsActive));
        }
    }

    public bool IsOverviewActive => CurrentModule == AppModules.Overview;
    public bool IsPcRecordingActive => CurrentModule == AppModules.PcRecording;
    public bool IsMobileBackupActive => CurrentModule == AppModules.MobileBackup;
    public bool IsOrderIntegrationActive => CurrentModule == AppModules.OrderIntegration;
    public bool IsVideoLibraryActive => CurrentModule == AppModules.VideoLibrary;
    public bool IsSettingsActive => CurrentModule == AppModules.Settings;

    public ICommand NavigateCommand { get; }

    public void Navigate(string? module) => CurrentModule = module ?? AppModules.Overview;

    internal static string NormalizeModule(string? module) =>
        module != null && KnownModules.Contains(module) ? module : AppModules.Overview;
}
