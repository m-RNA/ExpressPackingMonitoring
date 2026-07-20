using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace ExpressPackingMonitoring
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private WorkstationInstanceCoordinator? _instanceCoordinator;
        private AppRuntimeHost? _runtimeHost;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            CameraBarcodeRuntimeOptions.Initialize(e.Args);
            var config = WorkstationConfigStore.Load();
            AppLanguage.Initialize(config.Language);
            AppLanguage.EnableAutomaticWpfLocalization();
            RegisterRuntimeExceptionLogging();
            RuntimeLog.Info("App", "Application startup");
            RuntimeLog.LogSessionStart(e.Args);
            RuntimeLog.LogBuildInfo();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (AudioProbe.TryHandleCommandLine(e.Args, out int exitCode))
            {
                RuntimeLog.Info("App", $"AudioProbe command handled, exitCode={exitCode}");
                RuntimeLog.RecordShutdownRequest("AudioProbeCompleted", $"exitCode={exitCode}");
                Shutdown(exitCode);
                return;
            }

            string startupModule = StartupModulePolicy.Resolve(e.Args);
            Properties["StartupModule"] = startupModule;

            if (!WorkstationInstanceCoordinator.TryCreate("Unified", out _instanceCoordinator))
            {
                WorkstationInstanceCoordinator.RequestActivate("Unified");
                RuntimeLog.RecordShutdownRequest("DuplicateInstanceActivated", startupModule);
                Shutdown(0);
                return;
            }

            _runtimeHost = new AppRuntimeHost();
            Window window = new MainWindow(_runtimeHost, startupModule);
            window.SourceInitialized += (_, _) =>
            {
                if (PresentationSource.FromVisual(window) is HwndSource source)
                    source.AddHook(ShutdownWindowProc);
            };
            MainWindow = window;
            _instanceCoordinator?.StartActivationListener(window);
            window.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            (string source, string detail) = RuntimeLog.GetShutdownRequest();
            if (string.Equals(source, "not-recorded", StringComparison.Ordinal))
            {
                RuntimeLog.RecordShutdownRequest("ApplicationExitWithoutRecordedClose", $"shutdownMode={ShutdownMode}");
                (source, detail) = RuntimeLog.GetShutdownRequest();
            }
            RuntimeLog.Info("App", $"Session exit session={RuntimeLog.CurrentSessionId}, pid={Environment.ProcessId}, exitCode={e.ApplicationExitCode}, source={source}, detail={detail}");
            _instanceCoordinator?.Dispose();
            _instanceCoordinator = null;
            _runtimeHost?.Dispose();
            _runtimeHost = null;
            base.OnExit(e);
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            RuntimeLog.RecordShutdownRequest("WindowsSessionEnding", e.ReasonSessionEnding.ToString());
            base.OnSessionEnding(e);
        }

        private static IntPtr ShutdownWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            string? shutdownSource = RuntimeLog.ClassifyShutdownWindowMessage(message, wParam);
            if (shutdownSource != null)
            {
                RuntimeLog.RecordShutdownRequest(
                    shutdownSource,
                    $"hwnd=0x{hwnd.ToInt64():X}, message=0x{message:X4}, wParam=0x{wParam.ToInt64():X}, lParam=0x{lParam.ToInt64():X}");
            }

            return IntPtr.Zero;
        }

        private static void RegisterRuntimeExceptionLogging()
        {
            Current.DispatcherUnhandledException += (_, e) =>
            {
                RuntimeLog.RecordShutdownRequest("DispatcherUnhandledException", e.Exception.GetType().FullName ?? e.Exception.GetType().Name);
                RuntimeLog.Error("Unhandled", "DispatcherUnhandledException", e.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.IsTerminating)
                    RuntimeLog.RecordShutdownRequest("AppDomainUnhandledException", e.ExceptionObject?.GetType().FullName ?? "unknown");
                RuntimeLog.Error("Unhandled", $"AppDomain unhandled exception, terminating={e.IsTerminating}", e.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                RuntimeLog.Error("Unhandled", "UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }
    }
}
