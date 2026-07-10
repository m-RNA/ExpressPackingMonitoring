using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Config;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace ExpressPackingMonitoring
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private WorkstationInstanceCoordinator? _instanceCoordinator;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            RegisterRuntimeExceptionLogging();
            RuntimeLog.Info("App", "Application startup");
            RuntimeLog.LogBuildInfo();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (AudioProbe.TryHandleCommandLine(e.Args, out int exitCode))
            {
                RuntimeLog.Info("App", $"AudioProbe command handled, exitCode={exitCode}");
                Shutdown(exitCode);
                return;
            }

            var config = WorkstationConfigStore.Load();
            bool forceChoose = e.Args.Any(a => string.Equals(a, "--choose-workstation", StringComparison.OrdinalIgnoreCase));
            string temporaryRole = ResolveRoleOption(e.Args, "--temporary-role");
            bool useTemporaryRole = WorkstationRoles.IsKnown(temporaryRole);
            string requestedRole = ResolveRoleOption(e.Args, "--role");
            if (string.IsNullOrWhiteSpace(requestedRole))
                requestedRole = ResolveLegacyRequestedRole(e.Args);

            if (forceChoose && !useTemporaryRole)
                config.WorkstationRole = "";
            if (!useTemporaryRole && !string.IsNullOrWhiteSpace(requestedRole))
            {
                if (!TrySaveStartupRole(config, requestedRole))
                    return;
            }

            string startupRole = useTemporaryRole ? temporaryRole : config.WorkstationRole;
            if (!useTemporaryRole && !WorkstationRoles.IsKnown(startupRole))
            {
                var selector = new WorkstationSelectionWindow();
                if (selector.ShowDialog() != true || string.IsNullOrWhiteSpace(selector.SelectedRole))
                {
                    Shutdown(0);
                    return;
                }

                if (!TrySaveStartupRole(config, selector.SelectedRole))
                    return;
                if (WorkstationNetwork.TryRestartApplication())
                    return;

                startupRole = config.WorkstationRole;
            }

            if (!WorkstationRoles.IsKnown(startupRole))
            {
                Shutdown(0);
                return;
            }

            if (!PrepareWorkstationInstance(ref startupRole, out _instanceCoordinator))
                return;

            Window window = string.Equals(startupRole, WorkstationRoles.PrintStation, StringComparison.OrdinalIgnoreCase)
                ? new PrintWorkstationWindow(config)
                : new MainWindow();
            MainWindow = window;
            _instanceCoordinator?.StartActivationListener(window);
            window.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        private bool TrySaveStartupRole(AppConfig config, string role)
        {
            if (WorkstationConfigStore.TryUpdate(
                    current => current.WorkstationRole = role,
                    out AppConfig savedConfig,
                    out string error))
            {
                config.WorkstationRole = savedConfig.WorkstationRole;
                return true;
            }

            MessageBox.Show(
                $"配置保存失败，程序无法安全启动。\n\n请检查磁盘空间和配置目录权限。\n{error}",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return false;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _instanceCoordinator?.Dispose();
            _instanceCoordinator = null;
            base.OnExit(e);
        }

        private bool PrepareWorkstationInstance(ref string startupRole, out WorkstationInstanceCoordinator? coordinator)
        {
            if (WorkstationInstanceCoordinator.TryCreate(startupRole, out coordinator))
                return true;

            string otherRole = WorkstationRoles.GetOtherRole(startupRole);
            bool canOpenOtherRole = !WorkstationInstanceCoordinator.IsRoleRunning(otherRole);
            RuntimeLog.Warn("Instance", $"Duplicate startup requested role={startupRole}, canOpenOtherRole={canOpenOtherRole}");

            var dialog = new DuplicateInstanceDialog(startupRole, otherRole, canOpenOtherRole);
            dialog.ShowDialog();

            if (dialog.Choice == DuplicateInstanceChoice.OpenOtherRole)
            {
                startupRole = otherRole;
                if (WorkstationInstanceCoordinator.TryCreate(startupRole, out coordinator))
                {
                    RuntimeLog.Info("Instance", $"Temporary role startup role={startupRole}");
                    return true;
                }

                WorkstationInstanceCoordinator.RequestActivate(startupRole);
                Shutdown(0);
                return false;
            }

            if (dialog.Choice == DuplicateInstanceChoice.ActivateExisting)
                WorkstationInstanceCoordinator.RequestActivate(startupRole);

            Shutdown(0);
            return false;
        }

        private static string ResolveLegacyRequestedRole(string[] args)
        {
            if (args.Any(a => string.Equals(a, "--monitor", StringComparison.OrdinalIgnoreCase)))
                return WorkstationRoles.CameraMonitor;
            if (args.Any(a => string.Equals(a, "--order-workstation", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "--print-station", StringComparison.OrdinalIgnoreCase)))
                return WorkstationRoles.PrintStation;
            return "";
        }

        private static string ResolveRoleOption(string[] args, string optionName)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? "";
                if (arg.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
                    return NormalizeRoleName(arg[(optionName.Length + 1)..]);
                if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    return NormalizeRoleName(args[i + 1]);
            }

            return "";
        }

        private static string NormalizeRoleName(string? role)
        {
            if (string.Equals(role, WorkstationRoles.CameraMonitor, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "monitor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "camera", StringComparison.OrdinalIgnoreCase))
                return WorkstationRoles.CameraMonitor;
            if (string.Equals(role, WorkstationRoles.PrintStation, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "print", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "printer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "order", StringComparison.OrdinalIgnoreCase))
                return WorkstationRoles.PrintStation;
            return "";
        }

        private static void RegisterRuntimeExceptionLogging()
        {
            Current.DispatcherUnhandledException += (_, e) =>
            {
                RuntimeLog.Error("Unhandled", "DispatcherUnhandledException", e.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
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
