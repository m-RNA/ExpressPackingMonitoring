using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace ExpressPackingMonitoring.UI;

public sealed class SettingsCapabilities
{
    private SettingsCapabilities(bool supportsCameraFeatures)
    {
        SupportsCameraFeatures = supportsCameraFeatures;
        SupportsSpeechSettings = supportsCameraFeatures;
        SupportsScannerSettings = supportsCameraFeatures;
        SupportsOrderVoiceSettings = supportsCameraFeatures;
        SupportsCameraMaintenance = supportsCameraFeatures;
        IsNoCameraWorkstation = !supportsCameraFeatures;
    }

    public bool SupportsCameraFeatures { get; }
    public bool SupportsSpeechSettings { get; }
    public bool SupportsScannerSettings { get; }
    public bool SupportsOrderVoiceSettings { get; }
    public bool SupportsCameraMaintenance { get; }
    public bool IsNoCameraWorkstation { get; }

    public static SettingsCapabilities ForRole(string? role) =>
        new(!string.Equals(role, WorkstationRoles.PrintStation, StringComparison.OrdinalIgnoreCase));
}

public sealed class SettingsContext
{
    public required SettingsCapabilities Capabilities { get; init; }
    public required Func<AppConfig, Task<bool>> ApplyAsync { get; init; }
    public Func<string>? ConnectionAddressProvider { get; init; }
    public Action<Window>? ShowMobileConnection { get; init; }
    public Action? CopyMobileConnectionUrl { get; init; }
    public Action<double?>? SetPreviewZoomScale { get; init; }
    public Func<bool>? SuspendCameraForSetupWizard { get; init; }
    public Action? ResumeCameraAfterSetupWizard { get; init; }
    public Action<string>? ShowToast { get; init; }
    public Func<IProgress<string>, CancellationToken, Task<(int success, int fail, int skip)>>? BatchConvertMkvToMp4Async { get; init; }
    public ICommand? ResetEncoderDetectCommand { get; init; }
    public object? ToastSource { get; init; }

    public static SettingsContext ForCameraWorkstation(MainViewModel mainViewModel)
    {
        ArgumentNullException.ThrowIfNull(mainViewModel);
        return new SettingsContext
        {
            Capabilities = SettingsCapabilities.ForRole(WorkstationRoles.CameraMonitor),
            ApplyAsync = mainViewModel.ApplySettingsAsync,
            ConnectionAddressProvider = () => mainViewModel.MonitorAccessAddress,
            ShowMobileConnection = mainViewModel.ShowMobileConnection,
            CopyMobileConnectionUrl = mainViewModel.CopyMobileConnectionUrl,
            SetPreviewZoomScale = value => mainViewModel.PreviewZoomScale = value,
            SuspendCameraForSetupWizard = mainViewModel.SuspendCameraForSetupWizard,
            ResumeCameraAfterSetupWizard = mainViewModel.ResumeCameraAfterSetupWizard,
            ShowToast = mainViewModel.ShowToast,
            BatchConvertMkvToMp4Async = mainViewModel.BatchConvertMkvToMp4Async,
            ResetEncoderDetectCommand = mainViewModel.ResetEncoderDetectCommand,
            ToastSource = mainViewModel
        };
    }
}
