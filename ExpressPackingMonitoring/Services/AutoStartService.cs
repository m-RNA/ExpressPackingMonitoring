using ExpressPackingMonitoring.Logging;
using Microsoft.Win32;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal static class AutoStartService
{
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "ExpressPackingMonitoring";

    public static void Apply(bool enable)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            if (key == null)
                return;

            if (!enable)
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                return;
            }

            string executablePath = ResolveStartupExecutable(
                Environment.ProcessPath ?? "",
                AppContext.BaseDirectory,
                File.Exists);
            if (!string.IsNullOrWhiteSpace(executablePath))
                key.SetValue(AppName, $"\"{executablePath}\"");
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("AutoStart", $"Failed to apply auto-start setting: {ex.Message}");
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    internal static string ResolveStartupExecutable(
        string processPath,
        string baseDirectory,
        Func<string, bool> fileExists)
    {
        try
        {
            var runtimeDirectory = new DirectoryInfo(
                Path.GetFullPath(baseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(runtimeDirectory.Name, "app", StringComparison.OrdinalIgnoreCase) &&
                runtimeDirectory.Parent != null)
            {
                string launcherPath = Path.Combine(runtimeDirectory.Parent.FullName, "ExpressPackingMonitoring.exe");
                if (fileExists(launcherPath))
                    return launcherPath;
            }
        }
        catch
        {
        }

        return !string.IsNullOrWhiteSpace(processPath) && fileExists(processPath)
            ? Path.GetFullPath(processPath)
            : "";
    }
}
