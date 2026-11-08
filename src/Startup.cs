using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace CHelper;

/// <summary>
/// Run-at-logon via Task Scheduler with highest privileges, so there is no UAC prompt on boot.
/// The task is defined in XML because "schtasks /create" on its own uses Task Scheduler's defaults: the task
/// won't start on battery, Windows kills it when the charger is unplugged, it is stopped after 3 days and it runs
/// at below-normal priority.
/// </summary>
public static class Startup
{
    private const string TaskName = "C-Helper";

    public static bool IsEnabled() => Run($"/query /tn \"{TaskName}\"") == 0;

    public static void Enable()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        string xmlPath = Path.Combine(Path.GetTempPath(), "CHelper-task.xml");
        try
        {
            File.WriteAllText(xmlPath, TaskXml(exe), System.Text.Encoding.Unicode);
            int code = Run($"/create /f /tn \"{TaskName}\" /xml \"{xmlPath}\"");
            Log.Info($"Startup task created (schtasks exit {code})");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    public static void Disable() => Run($"/delete /f /tn \"{TaskName}\"");

    /// <summary>
    /// Tasks made by earlier versions (or the old installer) still have the battery limits, so Windows closes
    /// C-Helper on unplug. Re-create such a task with the right settings. Needs admin.
    /// </summary>
    public static void RepairIfNeeded()
    {
        try
        {
            string xml = RunOutput($"/query /tn \"{TaskName}\" /xml");
            if (xml == null) return; // no task: Startup is off
            if (xml.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>") &&
                xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>")) return;
            Log.Info("Startup task has battery / time limits; re-creating it");
            Enable();
        }
        catch (Exception ex)
        {
            Log.Error("Startup task check failed", ex);
        }
    }

    private static string TaskXml(string exe)
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string command = SecurityElement.Escape(exe);
        string dir = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts C-Helper at sign-in without a UAC prompt.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>--tray</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static int Run(string args)
    {
        try
        {
            using var p = Start(args);
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error("schtasks failed", ex);
            return -1;
        }
    }

    /// <summary>schtasks output, or null if it failed.</summary>
    private static string RunOutput(string args)
    {
        using var p = Start(args);
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return p.ExitCode == 0 ? output : null;
    }

    private static Process Start(string args) => Process.Start(new ProcessStartInfo("schtasks.exe", args)
    {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    })!;
}
