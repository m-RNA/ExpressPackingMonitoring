using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    private const string AppRelativePath = "app\\ExpressPackingMonitoring.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        string appPath = Path.Combine(baseDir, AppRelativePath);
        if (!File.Exists(appPath))
        {
            ShowError($"未找到主程序：{AppRelativePath}\n\n请确认 app 文件夹与本启动程序放在同一目录。");
            return 2;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appPath,
                WorkingDirectory = Path.GetDirectoryName(appPath) ?? baseDir,
                UseShellExecute = false
            };

            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex)
        {
            ShowError($"启动主程序失败：\n{ex.Message}");
            return 1;
        }
    }

    private static void ShowError(string message)
    {
        MessageBoxW(IntPtr.Zero, message, "打包监控", 0x00000010);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);
}
