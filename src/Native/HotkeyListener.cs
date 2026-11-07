using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CHelper.Native;

/// <summary>
/// Receives Fn-key events straight from the Clevo ACPI bridge driver (ACPI\CLV0001), the same way the
/// vendor's background service does, so no vendor app is needed:
///   1. open the CLV0001 device interface {86994c74-ad43-4812-b7e7-0c420b5c5fd7}
///   2. IOCTL 0x322404 with an event handle  (register)
///   3. wait for the event, then IOCTL 0x322408 -> 8-byte event code (e.g. 149 = Fn+Esc)
/// </summary>
public static class HotkeyListener
{
    private const uint IoctlRegister = 0x322404;
    private const uint IoctlGetEvent = 0x322408;
    private static readonly Guid InterfaceGuid = new("86994c74-ad43-4812-b7e7-0c420b5c5fd7");
    private const string FallbackPath = @"\\?\ACPI#CLV0001#1#{86994c74-ad43-4812-b7e7-0c420b5c5fd7}";

    private static Thread _thread;
    private static readonly ManualResetEvent Stop = new(false);

    /// <summary>Raised on a background thread with the BIOS event code.</summary>
    public static event Action<int> EventReceived;

    public static bool Running => _thread != null;

    public static void Start()
    {
        if (_thread != null) return;
        Stop.Reset();
        _thread = new Thread(Loop) { IsBackground = true, Name = "BIOS hotkeys" };
        _thread.Start();
    }

    public static void Shutdown() => Stop.Set();

    private static void Loop()
    {
        string path = FindDevicePath() ?? FallbackPath;
        using SafeFileHandle device = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (device.IsInvalid)
        {
            Log.Error($"Hotkeys: cannot open {path} (error {Marshal.GetLastWin32Error()})");
            _thread = null;
            return;
        }

        using var evt = new ManualResetEvent(false);
        IntPtr reg = Marshal.AllocHGlobal(IntPtr.Size);
        IntPtr outBuf = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteIntPtr(reg, evt.SafeWaitHandle.DangerousGetHandle());
            if (!DeviceIoControl(device, IoctlRegister, reg, IntPtr.Size, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                Log.Error($"Hotkeys: register failed (error {Marshal.GetLastWin32Error()})");
                _thread = null;
                return;
            }
            Log.Info($"Hotkeys: listening on {path}");

            var handles = new WaitHandle[] { Stop, evt };
            while (WaitHandle.WaitAny(handles) == 1)
            {
                evt.Reset();
                Marshal.WriteInt64(outBuf, 0);
                if (!DeviceIoControl(device, IoctlGetEvent, IntPtr.Zero, 0, outBuf, 8, out _, IntPtr.Zero))
                {
                    Log.Error($"Hotkeys: read failed (error {Marshal.GetLastWin32Error()})");
                    continue;
                }
                int code = (int)Marshal.ReadInt64(outBuf);
                Log.Info($"Hotkey event {code} (0x{code:X2})");
                try { EventReceived?.Invoke(code); }
                catch (Exception ex) { Log.Error("Hotkey handler failed", ex); }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Hotkeys: listener crashed", ex);
        }
        finally
        {
            Marshal.FreeHGlobal(reg);
            Marshal.FreeHGlobal(outBuf);
            _thread = null;
        }
    }

    private static string FindDevicePath()
    {
        try
        {
            Guid g = InterfaceGuid;
            if (CM_Get_Device_Interface_List_SizeW(out int len, ref g, null, 0) != 0 || len <= 1) return null;
            var buffer = new char[len];
            if (CM_Get_Device_Interface_ListW(ref g, null, buffer, len, 0) != 0) return null;
            string first = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrEmpty(first) ? null : first;
        }
        catch (Exception ex)
        {
            Log.Error("Hotkeys: interface lookup failed", ex);
            return null;
        }
    }

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuf, int inSize,
        IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid interfaceClass, string deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string deviceId, char[] buffer, int length, int flags);
}
