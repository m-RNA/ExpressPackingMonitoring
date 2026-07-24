using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.UI;
using System.Drawing;
using System.Windows;

namespace ExpressPackingMonitoring.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly Window _window;
    private readonly Action _exitRequested;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly Icon? _icon;
    private bool _disposed;
    private bool _hasShownMinimizeTip;

    public TrayIconService(Window window, Action exitRequested)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _exitRequested = exitRequested ?? throw new ArgumentNullException(nameof(exitRequested));

        try
        {
            string? executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
                _icon = Icon.ExtractAssociatedIcon(executablePath);
        }
        catch
        {
            _icon = null;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(AppLanguage.Get("打开主界面"), null, (_, _) => RestoreWindow());
        menu.Items.Add(AppLanguage.Get("直接退出"), null, (_, _) => RequestExit());

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _icon ?? SystemIcons.Application,
            Text = "快递打包监控",
            ContextMenuStrip = menu,
            Visible = false
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreWindow();
    }

    public void MinimizeToTray()
    {
        if (_disposed)
            return;

        _notifyIcon.Visible = true;
        _window.ShowInTaskbar = false;
        _window.Hide();
        if (!_hasShownMinimizeTip)
        {
            _hasShownMinimizeTip = true;
            _notifyIcon.ShowBalloonTip(
                2500,
                "快递打包监控",
                AppLanguage.Get("程序仍在后台运行，双击托盘图标可打开主界面"),
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    public void RestoreWindow()
    {
        if (_disposed)
            return;

        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
                return;

            _notifyIcon.Visible = false;
            _window.ShowInTaskbar = true;
            _window.Show();
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
        });
    }

    private void RequestExit()
    {
        RestoreWindow();
        _window.Dispatcher.BeginInvoke(_exitRequested);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon?.Dispose();
    }
}

internal sealed class WindowCloseBehaviorController : IDisposable
{
    private readonly Window _owner;
    private readonly TrayIconService _trayIcon;
    private readonly bool _promptEnabled;

    public WindowCloseBehaviorController(Window owner, Action exitRequested, bool promptEnabled)
    {
        _owner = owner;
        _promptEnabled = promptEnabled;
        _trayIcon = new TrayIconService(owner, exitRequested);
    }

    public WindowCloseChoice HandleClose(AppConfig config, bool bypassPreference)
    {
        if (bypassPreference || !_promptEnabled)
            return WindowCloseChoice.Exit;

        string behavior = WindowCloseBehaviors.Normalize(config.WindowCloseBehavior);
        WindowCloseChoice choice;
        bool rememberChoice = false;

        if (behavior == WindowCloseBehaviors.Ask)
        {
            var dialog = new CloseBehaviorDialog { Owner = _owner };
            if (dialog.ShowDialog() != true || dialog.Choice == WindowCloseChoice.None)
                return WindowCloseChoice.None;

            choice = dialog.Choice;
            rememberChoice = dialog.RememberChoice;
        }
        else
        {
            choice = behavior == WindowCloseBehaviors.MinimizeToTray
                ? WindowCloseChoice.MinimizeToTray
                : WindowCloseChoice.Exit;
        }

        if (rememberChoice)
        {
            string savedBehavior = choice == WindowCloseChoice.MinimizeToTray
                ? WindowCloseBehaviors.MinimizeToTray
                : WindowCloseBehaviors.Exit;
            if (WorkstationConfigStore.TryUpdate(
                    saved => saved.WindowCloseBehavior = savedBehavior,
                    out AppConfig savedConfig,
                    out string error))
            {
                config.WindowCloseBehavior = savedConfig.WindowCloseBehavior;
            }
            else
            {
                MessageBox.Show(
                    _owner,
                    $"关闭行为保存失败，下次仍会询问：{error}",
                    "设置保存失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        if (choice == WindowCloseChoice.MinimizeToTray)
            _trayIcon.MinimizeToTray();
        return choice;
    }

    public void Dispose() => _trayIcon.Dispose();
}
