using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CHelper.Native;

/// <summary>
/// Precision touchpad on/off, the same way the vendor's TPStatus.exe does it: Windows keeps the state in
/// HKCU\...\PrecisionTouchPad\Status "Enabled", and Win+Ctrl+F24 is Windows' own toggle shortcut.
/// </summary>
public static class Touchpad
{
    private const string Key = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status";

    /// <summary>True/false, or null if Windows doesn't report it.</summary>
    public static bool? IsEnabled()
    {
        try
        {
            object v = Registry.GetValue(Key, "Enabled", null);
            return v == null ? null : Convert.ToInt32(v) != 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Presses Win+Ctrl+F24 (Windows toggles the precision touchpad).</summary>
    public static void Toggle()
    {
        const byte VK_CONTROL = 0x11, VK_LWIN = 0x5B, VK_F24 = 0x87;
        const uint KEYUP = 0x2;
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_F24, 0, 0, UIntPtr.Zero);
        keybd_event(VK_F24, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
    }

    // ---------------- watching ----------------

    /// <summary>Raised (on a background thread) whenever the touchpad is switched on or off, by anyone.</summary>
    public static event Action<bool> Changed;

    private static Thread _watcher;

    /// <summary>Starts watching the touchpad state in the registry (once).</summary>
    public static void StartWatching()
    {
        if (_watcher != null) return;
        _watcher = new Thread(Watch) { IsBackground = true, Name = "Touchpad watcher" };
        _watcher.Start();
    }

    private static void Watch()
    {
        const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status");
            if (key == null)
            {
                Log.Info("Touchpad: no precision touchpad state to watch");
                return;
            }
            using var signal = new AutoResetEvent(false);
            bool? last = IsEnabled();
            while (true)
            {
                int rc = RegNotifyChangeKeyValue(key.Handle.DangerousGetHandle(), false, REG_NOTIFY_CHANGE_LAST_SET,
                    signal.SafeWaitHandle.DangerousGetHandle(), true);
                if (rc != 0)
                {
                    Log.Info($"Touchpad: can't watch the state (error {rc})");
                    return;
                }
                signal.WaitOne();
                bool? now = IsEnabled();
                if (now is bool on && now != last) Changed?.Invoke(on);
                last = now;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Touchpad watcher stopped", ex);
        }
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool watchSubtree, uint filter, IntPtr hEvent, bool asynchronous);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
