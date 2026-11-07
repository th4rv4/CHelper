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

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
