using ExpressPackingMonitoring.Logging;
using System.IO;
using System.IO.Pipes;
using System.Windows;
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
    private static string NormalizeRole(string role) => "Unified";
}
