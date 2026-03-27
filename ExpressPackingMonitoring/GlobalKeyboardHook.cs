using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 全局键盘钩子：即使程序不在前台也能接收扫码枪的输入。
    /// 扫码枪特征：在极短时间内连续输入字符，最后以 Enter 结尾。
    /// </summary>
    public class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;

        // 扫码枪判定：两次按键间隔不超过此毫秒数视为连续输入
        private const int MaxKeyIntervalMs = 100;
        // 最少字符数才认定为扫码枪输入（过短的可能是用户手动按键）
        private const int MinScanLength = 4;

        private IntPtr _hookId = IntPtr.Zero;
        private readonly LowLevelKeyboardProc _hookProc;
        private readonly StringBuilder _buffer = new();
        private DateTime _lastKeyTime = DateTime.MinValue;
        private readonly Dispatcher _dispatcher;
        private bool _isDisposed;

        public event Action<string> BarcodeScanned;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        public GlobalKeyboardHook()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _hookProc = HookCallback;
        }

        public void Start()
        {
            if (_hookId != IntPtr.Zero) return;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(curModule!.ModuleName!), 0);
            Debug.WriteLine($"[GlobalKeyHook] Started, hookId={_hookId}");
        }

        public void Stop()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                _buffer.Clear();
                Debug.WriteLine("[GlobalKeyHook] Stopped");
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                // 如果当前程序窗口在前台，不拦截（让正常的 TextBox 处理）
                if (IsOwnWindowForeground())
                {
                    _buffer.Clear();
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                int vkCode = Marshal.ReadInt32(lParam);
                var now = DateTime.Now;
                double elapsed = (now - _lastKeyTime).TotalMilliseconds;
                _lastKeyTime = now;

                // 如果间隔太久，清空缓冲区（不是连续扫码输入）
                if (elapsed > MaxKeyIntervalMs && _buffer.Length > 0)
                {
                    _buffer.Clear();
                }

                if (vkCode == 0x0D) // Enter
                {
                    string input = _buffer.ToString().Trim();
                    _buffer.Clear();
                    if (input.Length >= MinScanLength)
                    {
                        Debug.WriteLine($"[GlobalKeyHook] Barcode captured: {input}");
                        _dispatcher.BeginInvoke(() => BarcodeScanned?.Invoke(input));
                    }
                }
                else
                {
                    char? ch = VkCodeToChar(vkCode);
                    if (ch.HasValue)
                    {
                        _buffer.Append(ch.Value);
                    }
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private bool IsOwnWindowForeground()
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            GetWindowThreadProcessId(foreground, out uint foregroundPid);
            return foregroundPid == (uint)Environment.ProcessId;
        }

        private static char? VkCodeToChar(int vkCode)
        {
            // 数字 0-9
            if (vkCode >= 0x30 && vkCode <= 0x39)
                return (char)vkCode;

            // 字母 A-Z（统一大写，扫码枪通常输出大写）
            if (vkCode >= 0x41 && vkCode <= 0x5A)
                return (char)vkCode;

            // 小键盘数字 0-9
            if (vkCode >= 0x60 && vkCode <= 0x69)
                return (char)('0' + (vkCode - 0x60));

            // 常见分隔符
            return vkCode switch
            {
                0xBD => '-',  // OEM Minus
                0xBB => '=',  // OEM Plus (=)
                0xBC => ',',  // OEM Comma
                0xBE => '.',  // OEM Period
                0xBF => '/',  // OEM /
                0xBA => ';',  // OEM ;
                0xDE => '\'', // OEM '
                0xDB => '[',  // OEM [
                0xDD => ']',  // OEM ]
                0xDC => '\\', // OEM \
                _ => null
            };
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
        }
    }
}
