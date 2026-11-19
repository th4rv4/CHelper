using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CHelper.Native;

/// <summary>
/// AdGuard DNS (ads and trackers blocked for the whole laptop) on every real network adapter: Wi-Fi, Ethernet,
/// USB / Bluetooth tethering. Windows keeps DNS per adapter, not per Wi-Fi network, so this covers every Wi-Fi network
/// the laptop joins. VPN and virtual adapters are left alone.
///
/// IPv4 94.140.14.14 / 94.140.15.15 and IPv6 2a10:50c0::ad1:ff / 2a10:50c0::ad2:ff, with DNS over HTTPS turned on
/// ("On (automatic template)") where Windows supports it (Windows 11). A reinstalled adapter (e.g. after Wi-Fi Driver
/// Refresh) comes back with automatic DNS, so <see cref="Apply"/> runs again whenever the network changes; it only
/// touches adapters that aren't set up yet.
/// Needs admin, which C-Helper always has.
/// </summary>
public static class AdGuardDns
{
    private static readonly string[] V4 = { "94.140.14.14", "94.140.15.15" };
    private static readonly string[] V6 = { "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff" };
    private const string Template = "https://dns.adguard-dns.com/dns-query";
    private const string InterfaceKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters";

    private static readonly Regex NotReal = new(
        "Virtual|Hyper-V|VPN|TAP-|Tunnel|Loopback|VMware|VirtualBox|WAN Miniport|Wi-Fi Direct|Npcap|WireGuard|Tailscale|ZeroTier|Cisco AnyConnect|Fortinet",
        RegexOptions.IgnoreCase);

    private static readonly object Gate = new();
    private static bool _templatesAdded;
    // Set this session. An unplugged adapter may not report its DNS servers, so it isn't set again on every change.
    private static readonly HashSet<string> Applied = new();

    /// <summary>The adapters AdGuard DNS goes on.</summary>
    private static List<NetworkInterface> Adapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx)
        .Where(n => !NotReal.IsMatch(n.Description) && !NotReal.IsMatch(n.Name))
        .ToList();

    /// <summary>How many adapters there are and how many already use AdGuard DNS.</summary>
    public static (int Adapters, int On) Status()
    {
        try
        {
            var all = Adapters();
            return (all.Count, all.Count(IsOn));
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>Puts AdGuard DNS on every adapter that doesn't have it yet. Returns how many were changed.</summary>
    public static int Apply()
    {
        lock (Gate)
        {
            int changed = 0;
            foreach (var n in Adapters())
            {
                if (IsOn(n)) continue;
                if (n.OperationalStatus != OperationalStatus.Up && Applied.Contains(n.Id)) continue;
                AddTemplates();
                SetDoh(n.Id, on: true);
                var (v4, v6) = Indexes(n);
                if (v4 > 0)
                {
                    Netsh($"interface ipv4 set dnsservers name={v4} source=static address={V4[0]} register=primary validate=no");
                    Netsh($"interface ipv4 add dnsservers name={v4} address={V4[1]} index=2 validate=no");
                }
                if (v6 > 0)
                {
                    Netsh($"interface ipv6 set dnsservers name={v6} source=static address={V6[0]} register=primary validate=no");
                    Netsh($"interface ipv6 add dnsservers name={v6} address={V6[1]} index=2 validate=no");
                }
                Applied.Add(n.Id);
                Log.Info($"AdGuard DNS set on {n.Name} ({n.Description})");
                changed++;
            }
            if (changed > 0) FlushCache();
            return changed;
        }
    }

    /// <summary>Puts every adapter that uses AdGuard DNS back to automatic DNS (from the router).</summary>
    public static void Remove()
    {
        lock (Gate)
        {
            Applied.Clear();
            foreach (var n in Adapters())
            {
                SetDoh(n.Id, on: false);
                if (!UsesAdGuard(n)) continue;
                var (v4, v6) = Indexes(n);
                if (v4 > 0) Netsh($"interface ipv4 set dnsservers name={v4} source=dhcp");
                if (v6 > 0) Netsh($"interface ipv6 set dnsservers name={v6} source=dhcp");
                Log.Info($"AdGuard DNS removed from {n.Name}, back to automatic DNS");
            }
            FlushCache();
        }
    }

    /// <summary>All four AdGuard servers (just the IPv4 pair when the adapter has no IPv6) and nothing else.</summary>
    private static bool IsOn(NetworkInterface n)
    {
        var have = Servers(n);
        var (_, v6) = Indexes(n);
        var want = v6 > 0 ? V4.Concat(V6) : V4;
        return want.All(have.Contains) && have.All(want.Contains);
    }

    private static bool UsesAdGuard(NetworkInterface n) => Servers(n).Any(s => V4.Contains(s) || V6.Contains(s));

    private static HashSet<string> Servers(NetworkInterface n)
    {
        try
        {
            return n.GetIPProperties().DnsAddresses
                .Where(a => !a.IsIPv6SiteLocal) // fec0:0:0:ffff::1 etc.: Windows' placeholders when IPv6 has no DNS
                .Select(a => a.ToString().ToLowerInvariant())
                .ToHashSet();
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    private static (int V4, int V6) Indexes(NetworkInterface n)
    {
        int v4 = 0, v6 = 0;
        var p = n.GetIPProperties();
        try { if (n.Supports(NetworkInterfaceComponent.IPv4)) v4 = p.GetIPv4Properties()?.Index ?? 0; } catch { }
        try { if (n.Supports(NetworkInterfaceComponent.IPv6)) v6 = p.GetIPv6Properties()?.Index ?? 0; } catch { }
        return (v4, v6);
    }

    /// <summary>Tells Windows the AdGuard servers speak DNS over HTTPS (Windows 11; fails quietly elsewhere).</summary>
    private static void AddTemplates()
    {
        if (_templatesAdded) return;
        foreach (string ip in V4.Concat(V6))
            Netsh($"dns add encryption server={ip} dohtemplate={Template} autoupgrade=yes udpfallback=no");
        _templatesAdded = true;
    }

    /// <summary>
    /// "DNS over HTTPS: On (automatic template)" for the AdGuard servers on one adapter, the same setting the Settings
    /// app writes (DohFlags 1 under the adapter's DohInterfaceSettings).
    /// </summary>
    private static void SetDoh(string adapterId, bool on)
    {
        try
        {
            string root = $@"{InterfaceKey}\{adapterId}\DohInterfaceSettings";
            foreach (var (folder, ips) in new[] { ("Doh", V4), ("Doh6", V6) })
            {
                foreach (string ip in ips)
                {
                    string key = $@"{root}\{folder}\{ip}";
                    if (on)
                    {
                        using var k = Registry.LocalMachine.CreateSubKey(key);
                        k.SetValue("DohFlags", 1L, RegistryValueKind.QWord);
                    }
                    else
                    {
                        Registry.LocalMachine.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Setting DNS over HTTPS failed", ex);
        }
    }

    private static void FlushCache() => Run("ipconfig.exe", "/flushdns");

    private static void Netsh(string args)
    {
        var (exit, output) = Run("netsh.exe", args);
        if (exit != 0) Log.Info($"netsh {args} ({exit}): {output}");
    }

    private static (int Exit, string Output) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(); } catch { }
            return (-1, "timed out");
        }
        return (p.ExitCode, Regex.Replace(output.Trim(), @"\s+", " "));
    }
}
