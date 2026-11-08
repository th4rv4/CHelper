using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CHelper.Native;

/// <summary>
/// Single instance across privilege levels. CHelper.exe starts as a normal app (asInvoker), so clicking a taskbar
/// or Start menu shortcut never shows a UAC prompt just to bring the running copy forward:
/// <list type="bullet">
/// <item>A running (elevated) copy owns a hidden message window and accepts "show" / "quit" messages from
/// non-elevated processes (ChangeWindowMessageFilterEx).</item>
/// <item>If none is running, the elevated copy is started through the sign-in scheduled task (no prompt), or
/// with a single UAC prompt when that task doesn't exist.</item>
/// </list>
/// </summary>
public static class Instance
{
    private const string WindowTitle = "CHelper.Messages";
    public static readonly uint ShowMessage = RegisterWindowMessage("CHelper.ShowWindow");
    public static readonly uint QuitMessage = RegisterWindowMessage("CHelper.Quit");

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static IntPtr FindRunning() => FindWindowEx(HWND_MESSAGE, IntPtr.Zero, null, WindowTitle);

    public static bool IsRunning => FindRunning() != IntPtr.Zero;

    /// <summary>Sends a message to the running copy; false if none is running.</summary>
    public static bool Signal(uint message)
    {
        IntPtr hwnd = FindRunning();
        if (hwnd == IntPtr.Zero) return false;
        // The clicked copy owns the foreground right now; pass that on so the window can come to the front.
        AllowSetForegroundWindow(ASFW_ANY);
        return PostMessage(hwnd, message, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Waits until the running copy has exited (up to <paramref name="timeoutMs"/>).</summary>
    public static void WaitForExit(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (IsRunning && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(100);
    }

    /// <summary>
    /// Starts the elevated copy: via the "C-Helper" sign-in task when it exists (already allowed to run as admin,
    /// so no prompt), otherwise by relaunching this exe as administrator (one UAC prompt).
    /// </summary>
    public static void StartElevated(bool showWindow)
    {
        if (RunStartupTask())
        {
            // The task starts C-Helper hidden (--tray); open the window once it is up.
            var sw = Stopwatch.StartNew();
            while (!IsRunning && sw.ElapsedMilliseconds < 8_000) Thread.Sleep(150);
            if (IsRunning)
            {
                if (showWindow) Signal(ShowMessage);
                return;
            }
            // Task Scheduler accepted the run but nothing started (e.g. an older task that won't start on
            // battery): fall back to a UAC prompt instead of doing nothing.
        }

        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = showWindow ? "" : "--tray",
            });
        }
        catch (Win32Exception)
        {
            // UAC prompt cancelled
        }
    }

    private static bool RunStartupTask()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/run /tn \"C-Helper\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The hidden window the running copy listens on. Create it on the UI thread.</summary>
    public sealed class Window : NativeWindow, IDisposable
    {
        public event Action ShowRequested;
        public event Action QuitRequested;

        public Window()
        {
            CreateHandle(new CreateParams { Caption = WindowTitle, Parent = HWND_MESSAGE });
            // Let non-elevated copies (taskbar / Start menu clicks) reach this elevated window.
            ChangeWindowMessageFilterEx(Handle, ShowMessage, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(Handle, QuitMessage, MSGFLT_ALLOW, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)ShowMessage) ShowRequested?.Invoke();
            else if (m.Msg == (int)QuitMessage) QuitRequested?.Invoke();
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }

    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const uint MSGFLT_ALLOW = 1;
    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr changeInfo);
}
