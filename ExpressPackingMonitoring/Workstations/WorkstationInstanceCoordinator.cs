using ExpressPackingMonitoring.Logging;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExpressPackingMonitoring;

public sealed class WorkstationInstanceCoordinator : IDisposable
{
    private const string NamePrefix = "ExpressPackingMonitoring";
    private readonly string _role;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private Window? _window;
    private bool _disposed;

    private WorkstationInstanceCoordinator(string role, Mutex mutex)
    {
        _role = role;
        _mutex = mutex;
    }

    public static bool TryCreate(string role, out WorkstationInstanceCoordinator? coordinator)
    {
        coordinator = null;
        if (!WorkstationRoles.IsKnown(role)) return false;

        var mutex = new Mutex(initiallyOwned: true, GetMutexName(role), out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return false;
        }

        coordinator = new WorkstationInstanceCoordinator(role, mutex);
        return true;
    }

    public static bool IsRoleRunning(string role)
    {
        if (!WorkstationRoles.IsKnown(role)) return false;

        try
        {
            using var existing = Mutex.OpenExisting(GetMutexName(role));
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool RequestActivate(string role)
    {
        if (!WorkstationRoles.IsKnown(role)) return false;

        try
        {
            using var pipe = new NamedPipeClientStream(".", GetPipeName(role), PipeDirection.Out);
            pipe.Connect(400);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("activate");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartActivationListener(Window window)
    {
        if (_disposed || _listenTask != null) return;
        _window = window;
        _listenTask = Task.Run(ListenForActivationAsync);
    }

    private async Task ListenForActivationAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    GetPipeName(_role),
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_cts.Token);
                ActivateWindow();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                if (_cts.IsCancellationRequested) break;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Instance", $"Activation listener error role={_role}, error={ex.Message}");
                await Task.Delay(300, _cts.Token).ContinueWith(_ => { });
            }
        }
    }

    private void ActivateWindow()
    {
        var window = _window;
        if (window == null || _disposed) return;

        _ = window.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Show();
                window.Activate();
                window.Topmost = true;
                window.Topmost = false;
                window.Focus();
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Instance", $"Activate window failed role={_role}, error={ex.Message}");
            }
        }), DispatcherPriority.Normal);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        try { _mutex.ReleaseMutex(); } catch { }
        try { _mutex.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    private static string GetMutexName(string role) => $@"Local\{GetScopedNamePrefix()}.{NormalizeRole(role)}.Mutex";
    private static string GetPipeName(string role) => $"{GetScopedNamePrefix()}.{NormalizeRole(role)}.Activate";
    private static string GetScopedNamePrefix()
    {
        string scope = Environment.GetEnvironmentVariable("EPM_INSTANCE_SCOPE") ?? "";
        string normalizedScope = new(scope.Where(char.IsLetterOrDigit).Take(48).ToArray());
        return string.IsNullOrEmpty(normalizedScope) ? NamePrefix : $"{NamePrefix}.{normalizedScope}";
    }
    private static string NormalizeRole(string role) =>
        string.Equals(role, WorkstationRoles.PrintStation, StringComparison.OrdinalIgnoreCase)
            ? WorkstationRoles.PrintStation
            : WorkstationRoles.CameraMonitor;
}

public enum DuplicateInstanceChoice
{
    ActivateExisting,
    OpenOtherRole,
    Cancel
}

public sealed class DuplicateInstanceDialog : Window
{
    private readonly string _otherRole;

    public DuplicateInstanceChoice Choice { get; private set; } = DuplicateInstanceChoice.Cancel;

    public DuplicateInstanceDialog(string runningRole, string otherRole, bool canOpenOtherRole)
    {
        _otherRole = otherRole;
        Title = "程序已经在运行";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "PanelBackground");

        var root = new StackPanel
        {
            Margin = new Thickness(24)
        };

        Content = root;

        var titleText = new TextBlock
        {
            Text = $"当前已打开：{WorkstationRoles.GetDisplayName(runningRole)}",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14)
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        root.Children.Add(titleText);

        var bodyText = new TextBlock
        {
            Text = "重复打开同一种录像方式，可能抢占摄像头、麦克风、Web 端口或数据库",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 14)
        };
        bodyText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        root.Children.Add(bodyText);

        var questionText = new TextBlock
        {
            Text = "你想怎么做？",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 18)
        };
        questionText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        root.Children.Add(questionText);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        root.Children.Add(actions);

        var activateButton = CreateButton("切换到已打开窗口", isPrimary: true);
        activateButton.IsDefault = true;
        activateButton.Click += (_, _) =>
        {
            Choice = DuplicateInstanceChoice.ActivateExisting;
            DialogResult = true;
        };
        actions.Children.Add(activateButton);

        if (canOpenOtherRole)
        {
            var otherButton = CreateButton($"临时打开{WorkstationRoles.GetDisplayName(otherRole)}", isPrimary: false);
            otherButton.Click += (_, _) =>
            {
                Choice = DuplicateInstanceChoice.OpenOtherRole;
                DialogResult = true;
            };
            actions.Children.Add(otherButton);
        }

        var cancelButton = CreateButton("取消", isPrimary: false);
        cancelButton.IsCancel = true;
        cancelButton.Click += (_, _) =>
        {
            Choice = DuplicateInstanceChoice.Cancel;
            DialogResult = false;
        };
        actions.Children.Add(cancelButton);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Choice = DuplicateInstanceChoice.Cancel;
                DialogResult = false;
            }
        };
    }

    public string OtherRole => _otherRole;

    private static Button CreateButton(string text, bool isPrimary)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand
        };

        button.SetResourceReference(FrameworkElement.StyleProperty, isPrimary ? "PrimaryButtonStyle" : "SecondaryButtonStyle");

        return button;
    }
}
