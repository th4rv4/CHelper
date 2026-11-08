using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CHelper.Native;

/// <summary>
/// Watches Caps Lock / Num Lock / Scroll Lock (these never reach the BIOS; the vendor app watched the
/// keyboard for them). Uses a low-level keyboard hook, which needs the UI thread's message loop.
/// </summary>
public static class LockKeys
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private static HookProc _proc; // keep a reference so the GC doesn't collect the callback
    private static IntPtr _hook;

    /// <summary>Raised on the UI thread when a lock key is pressed; read the new state a moment later.</summary>
    public static event Action<Keys> Toggled;

    /// <summary>Call on the UI thread.</summary>
    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) Log.Error($"Lock-key hook failed (error {Marshal.GetLastWin32Error()})");
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private static readonly HashSet<Keys> _held = new();

    // Trigger on key-down (first press only, not auto-repeat): on this keyboard the key-up for
    // Num Lock is not always delivered, so waiting for key-up missed the "off" press.
    private static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = (Keys)Marshal.ReadInt32(lParam);           // KBDLLHOOKSTRUCT.vkCode
            int scan = Marshal.ReadInt32(lParam, 4);              // scanCode
            int flags = Marshal.ReadInt32(lParam, 8);             // flags
            if (key is Keys.CapsLock or Keys.NumLock or Keys.Scroll or Keys.Pause)
            {
                bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
                bool up = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;
                Log.Info($"Lock key {key} {(down ? "down" : up ? "up" : wParam.ToString())} scan=0x{scan:X2} flags=0x{flags:X2}");

                if (up) _held.Remove(key);
                else if (down && _held.Add(key) && key != Keys.Pause)
                {
                    try { Toggled?.Invoke(key); }
                    catch (Exception ex) { Log.Error("Lock-key handler failed", ex); }
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);
}
