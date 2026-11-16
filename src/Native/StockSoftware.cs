using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace CHelper.Native;

public enum StockItemKind { HotkeyService, ClipboardService, StoreApps, Files, InputFilters }

/// <summary>One piece of the stock Control Center found on this laptop.</summary>
public sealed class StockItem
{
    public StockItemKind Kind { get; init; }
    public string Title { get; init; }
    public string Detail { get; init; }
    /// <summary>What Remove does, e.g. "Stop And Disable".</summary>
    public string Action { get; init; }
    /// <summary>Something is still there to remove (a service that's already disabled counts as done).</summary>
    public bool Removable { get; init; }
}

/// <summary>
/// Finds and removes what the stock Control Center installs, none of which C-Helper needs:
/// - CCDCHUService, the stock hotkey service. It listens to the same firmware hotkeys as C-Helper and, without
///   its Store app, opens Documents at every sign-in.
/// - HKClipSvc, the HotKey Clipboard Service.
/// - The CLEVOCO Store apps (for every user, and the copy Windows installs for new accounts).
/// - C:\Program Files (x86)\ControlCenter, moved to the Recycle Bin.
/// Never touched: the HKKbdFltr / HKMouFltr keyboard and mouse filter drivers (taking them out can leave the
/// keyboard dead) and the AcpiBridge driver C-Helper itself talks to the firmware through.
/// Needs admin, which C-Helper always has.
/// </summary>
public static class StockSoftware
{
    private const string HotkeyService = "CCDCHUService";
    private const string ClipboardService = "HKClipSvc";
    private const string KeyboardClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96b-e325-11ce-bfc1-08002be10318}";
    private const string MouseClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96f-e325-11ce-bfc1-08002be10318}";

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ControlCenter");

    // ---------------------------------------------------------------- detect

    /// <summary>Everything found, in the order it should be removed. Slow-ish (starts PowerShell): call off the UI thread.</summary>
    public static List<StockItem> Detect()
    {
        var items = new List<StockItem>();

        AddService(items, StockItemKind.HotkeyService, HotkeyService, "Control Center Hotkey Service",
            "opens Documents at every sign-in");
        AddService(items, StockItemKind.ClipboardService, ClipboardService, "HotKey Clipboard Service", null);

        var apps = StoreApps();
        if (apps.Installed.Count > 0 || apps.Provisioned.Count > 0)
        {
            var names = apps.Names.Count > 0 ? apps.Names : apps.Provisioned;
            items.Add(new StockItem
            {
                Kind = StockItemKind.StoreApps,
                Title = "Stock Store Apps",
                Detail = $"{names.Count} installed: {string.Join(", ", names.Select(Friendly).Distinct())}",
                Action = "Uninstall",
                Removable = true,
            });
        }

        if (Directory.Exists(Folder))
        {
            long bytes = 0;
            try
            {
                bytes = new DirectoryInfo(Folder).EnumerateFiles("*", System.IO.SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch { }
            items.Add(new StockItem
            {
                Kind = StockItemKind.Files,
                Title = "Leftover Files",
                Detail = $"{Folder} · {bytes / 1048576.0:0.#} MB",
                Action = "Move To Recycle Bin",
                Removable = true,
            });
        }

        var filters = InputFilters();
        if (filters.Count > 0)
        {
            items.Add(new StockItem
            {
                Kind = StockItemKind.InputFilters,
                Title = "Keyboard And Touchpad Filters",
                Detail = $"{string.Join(", ", filters)} · Kept: removing them can stop the keyboard working",
                Action = "Left Alone",
                Removable = false,
            });
        }

        return items;
    }

    /// <summary>True if anything is left to remove (shows the button in the main window).</summary>
    public static bool AnythingToRemove(IEnumerable<StockItem> items) => items.Any(i => i.Removable);

    private static void AddService(List<StockItem> items, StockItemKind kind, string name, string title, string note)
    {
        var (exists, running, disabled) = ServiceState(name);
        if (!exists) return;
        string state = running ? "Running" : disabled ? "Disabled" : "Stopped";
        items.Add(new StockItem
        {
            Kind = kind,
            Title = title,
            Detail = $"{name} · {state}" + (note != null && !disabled ? $" · {note}" : ""),
            Action = disabled && !running ? "Already Disabled" : "Stop And Disable",
            Removable = running || !disabled,
        });
    }

    // ---------------------------------------------------------------- remove

    /// <summary>Removes one item and returns a short result ("Disabled", "2 uninstalled"…). Throws if it failed.</summary>
    public static string Remove(StockItemKind kind)
    {
        switch (kind)
        {
            case StockItemKind.HotkeyService:
                DisableService(HotkeyService);
                StopProcessesUnder(Folder); // its launchers (LaunchFnkey, AddFn)
                return "Disabled";

            case StockItemKind.ClipboardService:
                DisableService(ClipboardService);
                return "Disabled";

            case StockItemKind.StoreApps:
                return RemoveStoreApps();

            case StockItemKind.Files:
                // HKClipSvc runs from this folder: stop it (it can't start again without its files anyway).
                Sc($"stop {ClipboardService}");
                StopProcessesUnder(Folder);
                Thread.Sleep(500);
                FileSystem.DeleteDirectory(Folder, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                Log.Info($"Stock files moved to the Recycle Bin: {Folder}");
                return "In The Recycle Bin";

            default:
                return "Left Alone";
        }
    }

    /// <summary>Sets both stock services back to automatic and starts them (undo for Stop And Disable).</summary>
    public static void RestoreServices()
    {
        foreach (var name in new[] { HotkeyService, ClipboardService })
        {
            if (!ServiceState(name).Exists) continue;
            Sc($"config {name} start= auto");
            Sc($"start {name}");
            Log.Info($"Stock service {name} turned back on");
        }
    }

    private static void DisableService(string name)
    {
        Sc($"stop {name}");
        int code = Sc($"config {name} start= disabled");
        if (code != 0) throw new InvalidOperationException($"Couldn't disable {name} (sc exit {code}).");
        Log.Info($"Stock service {name} stopped and disabled");
    }

    // ---------------------------------------------------------------- store apps

    private sealed record Apps(List<string> Installed, List<string> Names, List<string> Provisioned);

    private static Apps StoreApps()
    {
        const string script =
            "$a = @(Get-AppxPackage -AllUsers -Name 'CLEVOCO.*' -ErrorAction SilentlyContinue);" +
            "$p = @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'CLEVOCO.*' });" +
            "ConvertTo-Json -Compress -InputObject @{ Installed = @($a | ForEach-Object PackageFullName); " +
            "Names = @($a | ForEach-Object Name | Select-Object -Unique); Provisioned = @($p | ForEach-Object PackageName) }";
        try
        {
            var (code, output) = PowerShell(script);
            if (code != 0 || string.IsNullOrWhiteSpace(output)) return new Apps(new(), new(), new());
            using var doc = JsonDocument.Parse(output.Trim());
            return new Apps(List(doc.RootElement, "Installed"), List(doc.RootElement, "Names"), List(doc.RootElement, "Provisioned"));
        }
        catch (Exception ex)
        {
            Log.Error("Listing the stock Store apps failed", ex);
            return new Apps(new(), new(), new());
        }
    }

    private static string RemoveStoreApps()
    {
        var apps = StoreApps();
        int removed = 0;
        var errors = new List<string>();
        foreach (var full in apps.Installed)
        {
            var (code, output) = PowerShell($"Remove-AppxPackage -Package '{full}' -AllUsers -ErrorAction Stop");
            if (code == 0) { removed++; Log.Info($"Stock app removed: {full}"); }
            else errors.Add($"{full}: {output.Trim()}");
        }
        foreach (var name in apps.Provisioned)
        {
            var (code, output) = PowerShell($"Remove-AppxProvisionedPackage -Online -PackageName '{name}' -ErrorAction Stop | Out-Null");
            if (code == 0) Log.Info($"Stock app no longer installed for new users: {name}");
            else errors.Add($"{name}: {output.Trim()}");
        }
        // Removing the app can take the new-account copy with it, and then removing that copy fails with
        // "cannot find the file". Only count what is really still there.
        if (errors.Count > 0)
        {
            var left = StoreApps();
            errors = errors.Where(e => left.Installed.Concat(left.Provisioned).Any(n => e.StartsWith(n + ":"))).ToList();
        }
        if (errors.Count > 0)
        {
            Log.Info("Stock app removal problems: " + string.Join(" | ", errors));
            if (removed == 0) throw new InvalidOperationException("Windows wouldn't uninstall the Store apps (see the log).");
        }
        return removed == 1 ? "1 Uninstalled" : $"{removed} Uninstalled";
    }

    /// <summary>"CLEVOCO.FnhotkeysandOSD" → "Fn Hotkeys And OSD".</summary>
    private static string Friendly(string package)
    {
        string name = package.Split('_')[0];
        if (name.StartsWith("CLEVOCO.", StringComparison.OrdinalIgnoreCase)) name = name[8..];
        return name switch
        {
            "FnhotkeysandOSD" => "Fn Hotkeys And OSD",
            "ControlCenter3.0" or "CC30" => "Control Center 3.0",
            _ => name,
        };
    }

    private static List<string> List(JsonElement root, string key)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(key, out var v)) return list;
        if (v.ValueKind == JsonValueKind.Array)
            list.AddRange(v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
        else if (v.ValueKind == JsonValueKind.String)
            list.Add(v.GetString());
        return list;
    }

    // ---------------------------------------------------------------- helpers

    private static List<string> InputFilters()
    {
        var found = new List<string>();
        foreach (var key in new[] { KeyboardClass, MouseClass })
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            if (k?.GetValue("UpperFilters") is string[] filters)
                found.AddRange(filters.Where(f => f.StartsWith("HK", StringComparison.OrdinalIgnoreCase)));
        }
        return found;
    }

    private static (bool Exists, bool Running, bool Disabled) ServiceState(string name)
    {
        var (code, query) = Run("sc.exe", $"query {name}");
        if (code == 1060 || query.Contains("1060")) return (false, false, false); // no such service
        var (_, config) = Run("sc.exe", $"qc {name}");
        bool running = query.Contains("RUNNING") || query.Contains("START_PENDING");
        bool disabled = config.Contains("DISABLED");
        return (true, running, disabled);
    }

    private static void StopProcessesUnder(string folder)
    {
        string prefix = folder.TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string path = p.MainModule?.FileName;
                if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    Log.Info($"Stopped stock process {Path.GetFileName(path)}");
                }
            }
            catch
            {
                // Protected or already gone.
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static int Sc(string args) => Run("sc.exe", args).Code;

    private static (int Code, string Output) PowerShell(string script) =>
        Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"", 120_000);

    private static (int Code, string Output) Run(string file, string args, int timeoutMs = 15_000)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch { }
            return (-1, "timed out");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
