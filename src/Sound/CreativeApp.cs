using System.Diagnostics;
using CHelper.Native;
using Microsoft.VisualBasic.FileIO;

namespace CHelper.Sound;

/// <summary>
/// Removes Creative App (and the Dolby Digital Live / DTS Connect packs it installs), which C-Helper's Sound panel
/// replaces. Kept: the sound drivers and Creative's sound engine (that's what makes Sound Blaster work) and
/// Speaker EQ Clevo (the laptop's own speaker tuning).
/// Creative App points the engine at speaker tuning files inside its own folder; those are copied to C-Helper's
/// folder and the engine is pointed there first, so the sound doesn't change when the folder goes.
/// Needs admin, which C-Helper always has.
/// </summary>
public static class CreativeApp
{
    private static string X86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static string AppFolder => Path.Combine(X86, "Creative", "Creative App");
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Where the speaker tuning files are kept after Creative App is gone.</summary>
    public static string TuningFolder => Path.Combine(ProgramData, "CHelper", "Sound");

    private const string SpeakerTuning = "f7e70860-8eb1-4c6a-b2e1-1033b409ff5d";
    private static readonly string[] Contexts = { "3f5f306b-a033-4f19-843d-1c44a736ff4d", "852311bc-1afb-454e-92ca-c35252cacaaf" };

    private static readonly (string Name, string Uninstaller)[] Uninstallers =
    {
        ("Creative App", @"Creative\Creative App\Uninstall\unins000.exe"),
        ("Dolby Digital Live Pack", @"Creative\Dolby Digital Live Pack\unins000.exe"),
        ("DTS Connect Pack", @"Creative\DTS Connect Pack\unins000.exe"),
    };

    public static bool Installed => File.Exists(Path.Combine(AppFolder, "Creative.App.exe"));

    /// <summary>Removes Creative App step by step, reporting progress. Run off the UI thread.</summary>
    public static string Remove(Action<string> progress)
    {
        var done = new List<string>();

        progress("Keeping your speaker tuning…");
        int moved = KeepSpeakerTuning();
        if (moved > 0) done.Add("speaker tuning kept");

        progress("Closing Creative App…");
        foreach (var name in new[] { "Creative.App", "Creative.VADMonitorService", "CTAudSvc" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
                catch (Exception ex) { Log.Info($"Couldn't close {name}: {ex.Message}"); }
                finally { p.Dispose(); }
            }
        }

        foreach (var (name, rel) in Uninstallers)
        {
            string exe = Path.Combine(X86, rel);
            if (!File.Exists(exe)) continue;
            progress($"Uninstalling {name}…");
            int code = Run(exe, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", 180_000);
            Log.Info($"{name} uninstaller finished ({code})");
            if (code == 0) done.Add(name);
        }

        // The uninstaller can hand over to a copy of itself; give it time to finish before tidying up.
        for (int i = 0; i < 60 && Installed; i++) Thread.Sleep(1000);

        progress("Moving leftover files to the Recycle Bin…");
        foreach (var dir in new[]
                 {
                     AppFolder, Path.Combine(ProgramData, "Creative", "CreativeApp"),
                     Path.Combine(LocalAppData, "Creative"), Path.Combine(LocalAppData, "Creative_Technology_Ltd"),
                 })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                FileSystem.DeleteDirectory(dir, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                Log.Info($"Moved to the Recycle Bin: {dir}");
            }
            catch (Exception ex)
            {
                Log.Error($"Couldn't remove {dir}", ex);
            }
        }

        bool gone = !Installed;
        Log.Info(gone ? "Creative App removed" : "Creative App is still installed");
        return gone ? "Creative App removed. Sound Blaster keeps working from here."
                    : "Creative App couldn't be fully removed. Check the log.";
    }

    /// <summary>
    /// Copies every speaker tuning file the engine loads from Creative App's folders into C-Helper's folder and
    /// points the engine there. Returns how many settings were changed.
    /// </summary>
    private static int KeepSpeakerTuning()
    {
        int changed = 0;
        string creativeData = Path.Combine(ProgramData, "Creative");
        foreach (var ep in AudioEffects.EndpointsWith(AudioEffects.Flow.Render, SoundBlaster.EngineClsid))
        {
            using var store = new AudioEffects.Store(ep, Contexts);
            if (!store.IsOpen) continue;
            for (uint pid = 0; pid < 4; pid++)
            {
                changed += store.RewriteText(SpeakerTuning, pid, path =>
                {
                    string full = Environment.ExpandEnvironmentVariables(path);
                    bool inCreative = full.StartsWith(creativeData, StringComparison.OrdinalIgnoreCase) ||
                                      full.StartsWith(AppFolder, StringComparison.OrdinalIgnoreCase);
                    if (!inCreative || !File.Exists(full)) return null;
                    Directory.CreateDirectory(TuningFolder);
                    string copy = Path.Combine(TuningFolder, Path.GetFileName(full));
                    File.Copy(full, copy, overwrite: true);
                    Log.Info($"Speaker tuning {full} -> {copy}");
                    return copy;
                });
            }
            store.Commit();
        }
        return changed;
    }

    private static int Run(string file, string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return -1;
            if (!p.WaitForExit(timeoutMs)) return -2;
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error($"Running {file} failed", ex);
            return -1;
        }
    }
}
