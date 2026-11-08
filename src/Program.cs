using CHelper.Native;
using CHelper.UI;

namespace CHelper;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // "CHelper.exe --quit" asks a running copy to exit cleanly (fans handed back first).
        // Used by the installer and uninstaller.
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            QuitRunning();
            return;
        }

        using var mutex = new Mutex(true, "CHelper.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            // Bring the running copy to the front instead of starting a second one.
            try
            {
                using var ev = EventWaitHandle.OpenExisting(TrayContext.ShowEventName);
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }

        ApplicationConfiguration.Initialize();
        Settings.Load();
        Log.Info("C-Helper starting");

        try
        {
            Dchu.Init(Settings.Current.DllPath);
        }
        catch (Exception ex)
        {
            Log.Error("DCHU init failed", ex);
            MessageBox.Show(ex.Message, "C-Helper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Whatever happens, never leave the fans at a fixed speed set by the custom curves.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeShutdown("process exit");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);
            SafeShutdown("crash");
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI exception", e.Exception);

        bool startHidden = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        var context = new TrayContext(startHidden);
        Task.Run(() =>
        {
            try { Controller.Instance.Start(); }
            catch (Exception ex) { Log.Error("Controller start failed", ex); }
        });
        Application.Run(context);
        SafeShutdown("exit");
        Log.Info("C-Helper exiting");
    }

    private static void QuitRunning()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(TrayContext.QuitEventName);
            ev.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return; // not running
        }

        // Wait (up to 10 s) until it has released the single-instance mutex.
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var m = Mutex.OpenExisting("CHelper.SingleInstance");
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return;
            }
            Thread.Sleep(100);
        }
    }

    private static int _shutdown;

    private static void SafeShutdown(string reason)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        try
        {
            Log.Info($"Shutdown ({reason})");
            Controller.Instance.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown failed", ex);
        }
    }
}
