using System.Diagnostics;

namespace CHelper;

/// <summary>Run-at-logon via Task Scheduler with highest privileges, so there is no UAC prompt on boot.</summary>
public static class Startup
{
    private const string TaskName = "C-Helper";

    public static bool IsEnabled() => Run($"/query /tn \"{TaskName}\"") == 0;

    public static void Enable()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        Run($"/create /f /tn \"{TaskName}\" /tr \"\\\"{exe}\\\" --tray\" /sc onlogon /rl highest /delay 0000:10");
    }

    public static void Disable() => Run($"/delete /f /tn \"{TaskName}\"");

    private static int Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p!.WaitForExit(10_000);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error("schtasks failed", ex);
            return -1;
        }
    }
}
