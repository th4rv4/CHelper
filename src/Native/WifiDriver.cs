using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace CHelper.Native;

/// <summary>
/// NotFound: no Intel Wi-Fi card has ever been seen. Missing: the card C-Helper saw before is gone from the device
/// list (removed, or Windows dropped it). Stopped: listed, but with an error.
/// </summary>
public enum WifiState { NotFound, Working, Disabled, Stopped, Missing }

/// <summary>The Intel Wi-Fi card as Windows sees it. <see cref="Problem"/> is the Device Manager error code (43 = stopped).</summary>
public sealed record WifiStatus(WifiState State, string Name, string DeviceId, int Problem);

/// <summary>
/// Notices when the Intel Wi-Fi card stops with an error (Device Manager Code 43, "failed post start") and fixes it
/// the way that works by hand: remove every Intel Wi-Fi driver package from the driver store (old ones pile up from
/// driver packs and Windows Update), put back one known-good copy and let Windows set the card up again.
///
/// The known-good copy is saved by C-Helper itself: whenever the card is working, the driver package it is using is
/// exported to %ProgramData%\CHelper\WifiDriver (again whenever its version changes; the copy it replaces is kept in
/// WifiDriver.previous, and the fix falls back to it when the newest copy does not bring the card back). Until then (a fresh install)
/// the driver bundled with C-Helper is used ("wifi" next to the exe, from vendor/wifi). With neither, the fix can only
/// restart the card.
///
/// Windows keeps the saved Wi-Fi networks per adapter, so a reinstalled card starts with none. While Wi-Fi works,
/// C-Helper also exports them to %ProgramData%\CHelper\WifiNetworks (passwords stay encrypted, readable only by this
/// laptop) and adds them back after a refresh.
/// Needs admin, which C-Helper always has.
/// </summary>
public static class WifiDriver
{
    // Device Manager codes that mean "not running" but aren't a fault.
    private const int CodeDisabled = 22;

    private static readonly string BackupRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CHelper", "WifiDriver");
    private static readonly string PreviousRoot = BackupRoot + ".previous";
    private static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "wifi");
    private static readonly string NetworksRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CHelper", "WifiNetworks");

    /// <summary>Version of the saved driver copy, or null when there is none yet.</summary>
    public static string SavedVersion => VersionOf(BackupRoot);

    /// <summary>Version of the copy kept from before the last driver change, or null when there is none.</summary>
    public static string PreviousVersion => VersionOf(PreviousRoot);

    private static string VersionOf(string root)
    {
        try
        {
            string file = Path.Combine(root, "version.txt");
            return File.Exists(file) && Directory.GetFiles(root, "*.inf", SearchOption.AllDirectories).Length > 0
                ? File.ReadAllText(file).Trim()
                : null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- detect

    /// <summary>Finds the Intel Wi-Fi card and its state. Takes a moment (WMI): call off the UI thread.</summary>
    public static WifiStatus Check()
    {
        try
        {
            using var search = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, ConfigManagerErrorCode, Manufacturer FROM Win32_PnPEntity WHERE PNPClass = 'Net'");
            foreach (ManagementObject o in search.Get())
            {
                using (o)
                {
                    string name = o["Name"] as string ?? "";
                    string maker = o["Manufacturer"] as string ?? "";
                    if (!Regex.IsMatch(name, "Wi-?Fi|Wireless", RegexOptions.IgnoreCase)) continue;
                    if (!maker.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
                    return ToStatus(name, o["PNPDeviceID"] as string, o["ConfigManagerErrorCode"]);
                }
            }

            // Not listed as a network adapter: with its driver gone the card can show up without a class.
            string id = Settings.Current.WifiDeviceId;
            if (!string.IsNullOrEmpty(id))
            {
                using var byId = new ManagementObjectSearcher(
                    $"SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPDeviceID = '{Wql(id)}'");
                foreach (ManagementObject o in byId.Get())
                {
                    using (o) return ToStatus(o["Name"] as string ?? "Wi-Fi", o["PNPDeviceID"] as string, o["ConfigManagerErrorCode"]);
                }
                // Seen before but not in the device list at all any more.
                return new WifiStatus(WifiState.Missing, "Wi-Fi", id, 0);
            }

            // Never seen working (fresh Windows): an Intel card without a driver shows up as an unknown "Network
            // Controller". Recognise it by the hardware IDs in the driver C-Helper would install.
            string infText = KnownInfText();
            if (infText.Length > 0)
            {
                using var driverless = new ManagementObjectSearcher(
                    @"SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0 AND PNPDeviceID LIKE 'PCI\\VEN_8086%'");
                foreach (ManagementObject o in driverless.Get())
                {
                    using (o)
                    {
                        string devId = o["PNPDeviceID"] as string ?? "";
                        var hw = Regex.Match(devId, @"^PCI\\VEN_8086&DEV_[0-9A-F]{4}&SUBSYS_[0-9A-F]{8}", RegexOptions.IgnoreCase);
                        if (hw.Success && infText.Contains(hw.Value, StringComparison.OrdinalIgnoreCase))
                            return ToStatus(o["Name"] as string ?? "Wi-Fi", devId, o["ConfigManagerErrorCode"]);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Wi-Fi check failed", ex);
        }
        return new WifiStatus(WifiState.NotFound, null, null, 0);
    }

    private static WifiStatus ToStatus(string name, string id, object code)
    {
        int problem = code == null ? 0 : Convert.ToInt32(code);
        var state = problem == 0 ? WifiState.Working : problem == CodeDisabled ? WifiState.Disabled : WifiState.Stopped;
        if (!string.IsNullOrEmpty(id) && Settings.Current.WifiDeviceId != id)
        {
            Settings.Current.WifiDeviceId = id;
            Settings.Save();
        }
        return new WifiStatus(state, name, id, problem);
    }

    private static string Wql(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    private static string _infText;

    /// <summary>Text of the driver C-Helper would install (saved copy, else bundled), for matching hardware IDs.</summary>
    private static string KnownInfText()
    {
        if (_infText != null) return _infText;
        try
        {
            var (folder, _) = DriverSource();
            _infText = folder == null ? "" : string.Concat(
                Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories).Where(IsIntelWifiInf).Select(File.ReadAllText));
        }
        catch
        {
            _infText = "";
        }
        return _infText;
    }

    /// <summary>Where a clean reinstall takes the driver from: C-Helper's saved copy, else the bundled one.</summary>
    private static (string Folder, string Version) DriverSource()
    {
        string saved = SavedVersion;
        if (saved != null) return (BackupRoot, saved);
        try
        {
            if (!Directory.Exists(BundledRoot)) return (null, null);
            foreach (string inf in Directory.GetFiles(BundledRoot, "*.inf", SearchOption.AllDirectories).Where(IsIntelWifiInf))
            {
                var ver = Regex.Match(File.ReadAllText(inf), @"^\s*DriverVer\s*=\s*[^,]*,\s*([\d.]+)",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline);
                return (BundledRoot, ver.Success ? ver.Groups[1].Value : "bundled");
            }
        }
        catch { }
        return (null, null);
    }

    // ---------------------------------------------------------------- keep a good copy

    /// <summary>
    /// While the card works, saves the driver package it uses (only when the saved copy is missing or a different
    /// version). Safe to call often: it does nothing when the copy is up to date.
    /// </summary>
    public static void SaveWorkingDriver(WifiStatus status)
    {
        if (status.State != WifiState.Working || string.IsNullOrEmpty(status.DeviceId)) return;
        if (!HasWifiAdapter()) return; // the card looks fine but Windows has no Wi-Fi: not a copy worth keeping
        SaveNetworks();
        try
        {
            var (inf, version) = DriverInUse(status.DeviceId);
            if (inf == null || version == null) return;
            if (SavedVersion == version) return;

            string temp = BackupRoot + ".new";
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);
            var (exit, output) = PnpUtil($"/export-driver {inf} \"{temp}\"");
            var infs = Directory.GetFiles(temp, "*.inf", SearchOption.AllDirectories);
            if (exit != 0 || !infs.Any(IsIntelWifiInf))
            {
                Log.Error($"Saving the Wi-Fi driver failed ({exit}): {output}");
                Directory.Delete(temp, true);
                return;
            }
            File.WriteAllText(Path.Combine(temp, "version.txt"), version);
            if (Directory.Exists(BackupRoot))
            {
                // Keep the copy being replaced: if the new driver turns out bad, the fix can go back to it.
                if (Directory.Exists(PreviousRoot)) Directory.Delete(PreviousRoot, true);
                Directory.Move(BackupRoot, PreviousRoot);
            }
            Directory.Move(temp, BackupRoot);
            Log.Info($"Saved a working copy of the Wi-Fi driver {version} ({inf})");
        }
        catch (Exception ex)
        {
            Log.Error("Saving the Wi-Fi driver failed", ex);
        }
    }

    /// <summary>The driver store name (oemNN.inf) and version of the driver a device is using.</summary>
    private static (string Inf, string Version) DriverInUse(string deviceId)
    {
        using var search = new ManagementObjectSearcher(
            $"SELECT InfName, DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceID = '{Wql(deviceId)}'");
        foreach (ManagementObject o in search.Get())
        {
            using (o) return (o["InfName"] as string, o["DriverVersion"] as string);
        }
        return (null, null);
    }

    // ---------------------------------------------------------------- fix

    /// <summary>
    /// Fixes a stopped card: clean reinstall from the saved (or bundled) copy, or just a restart of the card without one,
    /// then waits for it to come back. Reports (percent, step) as it goes. Returns the card's state afterwards.
    /// </summary>
    public static WifiStatus Repair(WifiStatus status, IProgress<(int Percent, string Step)> progress)
    {
        if (status.State == WifiState.Working) SaveNetworks(); // the latest list, before the card loses it
        var (source, version) = DriverSource();
        var after = ReinstallDriver(status, progress, source, version);

        // The newest saved copy did not bring the card back: go back to the one before it.
        string previous = PreviousVersion;
        if (after.State != WifiState.Working && source == BackupRoot && previous != null && previous != version)
        {
            Log.Info($"Wi-Fi still stopped with driver {version}: trying the previous copy {previous}");
            after = ReinstallDriver(after, progress, PreviousRoot, previous);
            if (after.State == WifiState.Working) UsePreviousAsSaved(version, previous);
        }

        if (after.State == WifiState.Working)
        {
            progress.Report((99, "Restoring Saved Networks…"));
            RestoreNetworks();
        }
        return after;
    }

    /// <summary>The previous copy worked and the newest did not: drop the bad one so it is not tried again.</summary>
    private static void UsePreviousAsSaved(string bad, string good)
    {
        try
        {
            if (Directory.Exists(BackupRoot)) Directory.Delete(BackupRoot, true);
            Directory.Move(PreviousRoot, BackupRoot);
            Log.Info($"Wi-Fi driver {good} is the saved copy again; {bad} did not work");
        }
        catch (Exception ex)
        {
            Log.Error("Switching the saved Wi-Fi driver failed", ex);
        }
    }

    private static WifiStatus ReinstallDriver(WifiStatus status, IProgress<(int Percent, string Step)> progress,
        string source, string version)
    {
        Log.Info(status.State switch
        {
            WifiState.Missing => $"Wi-Fi driver refresh: card missing ({status.DeviceId})",
            WifiState.Working => $"Wi-Fi driver refresh: {status.Name} (working)",
            _ => $"Wi-Fi driver refresh: {status.Name} · Code {status.Problem}",
        });

        if (source == null)
        {
            if (status.State == WifiState.Missing)
            {
                Log.Info("No saved or bundled Wi-Fi driver: looking for the card again only");
                progress.Report((30, "Looking For The Card…"));
                PnpUtil("/scan-devices");
            }
            else
            {
                Log.Info("No saved or bundled Wi-Fi driver: restarting the card only");
                progress.Report((30, "Restarting The Card…"));
                if (!string.IsNullOrEmpty(status.DeviceId)) PnpUtil($"/restart-device \"{status.DeviceId}\"");
            }
            return WaitForWorking(progress, 60);
        }

        progress.Report((10, "Removing Old Drivers…"));
        var stale = IntelWifiPackages();
        for (int i = 0; i < stale.Count; i++)
        {
            var (exit, output) = PnpUtil($"/delete-driver {stale[i]} /uninstall /force");
            Log.Info($"Removed Wi-Fi driver package {stale[i]} ({exit})" + (exit == 0 ? "" : $": {output}"));
            progress.Report((10 + 40 * (i + 1) / stale.Count, "Removing Old Drivers…"));
        }

        string from = source == BackupRoot ? "saved" : source == PreviousRoot ? "previous" : "bundled";
        progress.Report((55, $"Reinstalling Driver {version}…"));
        var (addExit, addOutput) = PnpUtil($"/add-driver \"{Path.Combine(source, "*.inf")}\" /subdirs /install");
        Log.Info($"Reinstalled the {from} Wi-Fi driver {version} ({addExit})" + (addExit is 0 or 3010 ? "" : $": {addOutput}"));

        progress.Report((75, "Starting The Card…"));
        PnpUtil("/scan-devices");
        return WaitForWorking(progress, 80);
    }

    private static WifiStatus WaitForWorking(IProgress<(int Percent, string Step)> progress, int from)
    {
        WifiStatus now = null;
        for (int i = 0; i < 15; i++)
        {
            Thread.Sleep(1000);
            now = Check();
            progress.Report((Math.Min(99, from + i * 2), "Starting The Card…"));
            if (now.State == WifiState.Working && HasWifiAdapter()) break;
        }
        // Device Manager can call a card fine while Windows still has no Wi-Fi on it: that is not fixed.
        if (now.State == WifiState.Working && !HasWifiAdapter())
        {
            Log.Info("Windows has no Wi-Fi adapter yet, so the card is not working");
            now = new WifiStatus(WifiState.Stopped, now.Name, now.DeviceId, 0);
        }
        Log.Info($"Wi-Fi after the fix: {now.State}" + (now.Problem != 0 ? $" · Code {now.Problem}" : ""));
        return now;
    }

    /// <summary>True when Windows has a Wi-Fi network adapter (what "netsh wlan" needs to add networks).</summary>
    private static bool HasWifiAdapter()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211);
        }
        catch { return true; } // cannot tell: trust the card's own status
    }

    /// <summary>
    /// The Intel Wi-Fi driver packages (oemNN.inf) in the driver store, found by their contents: network class and an
    /// Intel netwtw catalog. Their add-ons (PieExtension, WifiDrv Customizations) are a different class and stay.
    /// </summary>
    private static List<string> IntelWifiPackages()
    {
        var found = new List<string>();
        string infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        foreach (string path in Directory.GetFiles(infDir, "oem*.inf"))
        {
            if (IsIntelWifiInf(path)) found.Add(Path.GetFileName(path));
        }
        return found;
    }

    private static readonly Regex NetClass = new(@"^\s*ClassGuid\s*=\s*\{4d36e972-e325-11ce-bfc1-08002be10318\}",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex NetwtwCatalog = new(@"^\s*CatalogFile(\.\w+)?\s*=\s*netwtw\w*\.cat",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static bool IsIntelWifiInf(string path)
    {
        try
        {
            string text = File.ReadAllText(path); // .inf files are UTF-16 or ANSI; ReadAllText detects the BOM
            return NetClass.IsMatch(text) && NetwtwCatalog.IsMatch(text);
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- saved networks

    /// <summary>
    /// Exports the Wi-Fi networks Windows knows. Without key=clear the passwords stay encrypted so that only this laptop
    /// can read them, which is enough to add them back here. An empty export never replaces a good one.
    /// </summary>
    private static void SaveNetworks()
    {
        string temp = NetworksRoot + ".new";
        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);
            var (exit, output) = Run("netsh.exe", $"wlan export profile folder=\"{temp}\"");
            int count = Directory.GetFiles(temp, "*.xml").Length;
            if (count == 0)
            {
                if (exit != 0) Log.Error($"Saving the Wi-Fi networks failed ({exit}): {output}");
                Directory.Delete(temp, true);
                return;
            }
            int before = Directory.Exists(NetworksRoot) ? Directory.GetFiles(NetworksRoot, "*.xml").Length : -1;
            if (Directory.Exists(NetworksRoot)) Directory.Delete(NetworksRoot, true);
            Directory.Move(temp, NetworksRoot);
            if (count != before) Log.Info($"Saved {count} Wi-Fi networks");
        }
        catch (Exception ex)
        {
            Log.Error("Saving the Wi-Fi networks failed", ex);
        }
    }

    /// <summary>Adds the saved Wi-Fi networks back (after a refresh the card starts with none).</summary>
    private static void RestoreNetworks()
    {
        try
        {
            if (!Directory.Exists(NetworksRoot)) return;
            int added = 0;
            var files = Directory.GetFiles(NetworksRoot, "*.xml");
            foreach (string file in files)
            {
                var (exit, output) = Run("netsh.exe", $"wlan add profile filename=\"{file}\" user=all");
                if (exit == 0) added++;
                else Log.Error($"Adding Wi-Fi network {Path.GetFileNameWithoutExtension(file)} back failed ({exit}): {output}");
            }
            Log.Info($"Added {added} of {files.Length} saved Wi-Fi networks back");
        }
        catch (Exception ex)
        {
            Log.Error("Adding the saved Wi-Fi networks back failed", ex);
        }
    }

    private static (int Exit, string Output) PnpUtil(string args) => Run("pnputil.exe", args);

    private static (int Exit, string Output) Run(string exe, string args)
    {
        // A 32-bit process would get the redirected (and missing) SysWOW64 copy; C-Helper is 64-bit.
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(); } catch { }
            return (-1, "timed out");
        }
        return (p.ExitCode, Regex.Replace(output.Trim(), @"\s+", " "));
    }
}
