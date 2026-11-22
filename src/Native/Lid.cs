using System.Runtime.InteropServices;

namespace CHelper.Native;

/// <summary>
/// The lid: Windows' "When I close the lid" setting while plugged in, and whether the lid is closed right now.
/// </summary>
public static class Lid
{
    // Power settings → Power buttons and lid → Lid close action. 0 = Do nothing, 1 = Sleep, 2 = Hibernate, 3 = Shut down.
    private static Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static Guid LidAction = new("5ca83367-6e45-459f-a27b-476b1d01c936");
    private static Guid LidSwitchState = new("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");

    public const int DoNothing = 0;

    /// <summary>True while the lid is closed; null until Windows has reported it.</summary>
    public static bool? Closed { get; private set; }

    public static event Action<bool> Changed;

    private static Watcher _watcher;

    /// <summary>Start following the lid. Call once from the UI thread.</summary>
    public static void Watch()
    {
        try { _watcher ??= new Watcher(); }
        catch (Exception ex) { Log.Error("lid watch failed", ex); }
    }

    /// <summary>The plugged-in lid close action of the active power plan, or -1 if it can't be read.</summary>
    public static int ReadAcAction()
    {
        if (!ActiveScheme(out var scheme)) return -1;
        return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref SubButtons, ref LidAction, out uint v) == 0 ? (int)v : -1;
    }

    public static bool WriteAcAction(int value)
    {
        if (!ActiveScheme(out var scheme)) return false;
        if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref SubButtons, ref LidAction, (uint)value) != 0) return false;
        return PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0; // makes the change take effect
    }

    private static bool ActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0) return false;
        try { scheme = Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
        return true;
    }

    /// <summary>Hidden window Windows tells about the lid (and once straight away with its current state).</summary>
    private sealed class Watcher : NativeWindow
    {
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;

        public Watcher()
        {
            CreateHandle(new CreateParams { Caption = "C-Helper Lid" });
            if (RegisterPowerSettingNotification(Handle, ref LidSwitchState, 0) == IntPtr.Zero)
                Log.Info("lid: no notifications");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                var setting = Marshal.PtrToStructure<Guid>(m.LParam);
                if (setting == LidSwitchState)
                {
                    int open = Marshal.ReadInt32(m.LParam, 20); // after the GUID and the data length
                    bool closed = open == 0;
                    if (Closed != closed)
                    {
                        Closed = closed;
                        Log.Info(closed ? "lid closed" : "lid opened");
                        Changed?.Invoke(closed);
                    }
                }
            }
            base.WndProc(ref m);
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
}
