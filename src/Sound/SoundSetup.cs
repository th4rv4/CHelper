using System.Diagnostics;
using Microsoft.Win32;

namespace CHelper.Sound;

/// <summary>
/// Puts Sound Blaster Studio back when it's missing: the Creative audio extension driver and Creative's engine are
/// bundled with C-Helper (in "sound" next to the exe) and set up by Setup-SoundBlaster.ps1. Windows Update can swap
/// the extension for a plain Realtek one, which turns Sound Blaster off; the Sound page then offers to set it up again.
/// </summary>
public static class SoundSetup
{
    private const string ChipId = "FUNC_01&VEN_10EC&DEV_0256&SUBSYS_155825E1";

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "sound");
    private static string Script => Path.Combine(Folder, "Setup-SoundBlaster.ps1");

    /// <summary>The drivers were installed with C-Helper.</summary>
    public static bool Bundled => File.Exists(Script);

    /// <summary>This laptop has the EVOL P15's audio chip, the one the bundled drivers are made for.</summary>
    public static bool ChipPresent()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\INTELAUDIO");
            return key?.GetSubKeyNames().Any(n => n.Contains(ChipId, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Sound Blaster isn't working, but C-Helper has what it needs to set it up.</summary>
    public static bool Needed() => Bundled && ChipPresent() && !SoundBlaster.Installed();

    /// <summary>Runs the setup. Returns its exit code: 0 = done (restart needed), 3 = already set up, other = failed.</summary>
    public static int Run()
    {
        Log.Info("Setting up Sound Blaster from the bundled drivers");
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Script}\" -Source \"{Folder}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Log.Info($"Sound Blaster setup finished ({p.ExitCode}): {output.Trim()} {err.Result.Trim()}".Trim());
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error("Sound Blaster setup failed", ex);
            return 1;
        }
    }
}
