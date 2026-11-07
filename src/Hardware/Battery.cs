using System.Management;

namespace CHelper.Hardware;

/// <summary>
/// Battery charge, charge/discharge rate and health from Windows' battery driver (root\WMI),
/// the same data G-Helper shows. Independent of the vendor app.
/// </summary>
public static class Battery
{
    /// <param name="Percent">Charge level, or -1 if unknown.</param>
    /// <param name="Watts">Positive while charging, negative while discharging, 0 when idle (e.g. held at the charge limit).</param>
    public readonly record struct Status(int Percent, double Watts, bool Online);

    private static long _designMwh = -1;
    private static long _fullMwh = -1;
    private static DateTime _healthRead = DateTime.MinValue;

    public static Status Read()
    {
        double watts = 0;
        bool online = SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline;

        using (var searcher = new ManagementObjectSearcher(@"root\WMI",
                   "SELECT ChargeRate, DischargeRate, Charging, Discharging, PowerOnline FROM BatteryStatus"))
        {
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    online = Convert.ToBoolean(o["PowerOnline"] ?? online);
                    if (Convert.ToBoolean(o["Discharging"] ?? false))
                        watts -= Convert.ToInt64(o["DischargeRate"] ?? 0) / 1000.0;
                    else if (Convert.ToBoolean(o["Charging"] ?? false))
                        watts += Convert.ToInt64(o["ChargeRate"] ?? 0) / 1000.0;
                }
            }
        }

        int percent = PercentFromWindows(); // same number as the taskbar

        if (Math.Abs(watts) < 0.3) watts = 0;
        return new Status(percent, watts, online);
    }

    /// <summary>Full-charge capacity as % of design capacity, or -1 if unknown. Re-read at most every 15 minutes.</summary>
    public static double Health()
    {
        if (DateTime.UtcNow - _healthRead > TimeSpan.FromMinutes(15)) ReadCapacities();
        return _designMwh > 0 && _fullMwh > 0 ? _fullMwh * 100.0 / _designMwh : -1;
    }

    private static void ReadCapacities()
    {
        _healthRead = DateTime.UtcNow;
        try
        {
            _designMwh = Sum("SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity");
            _fullMwh = Sum("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
            Log.Info($"Battery design {_designMwh} mWh, full charge {_fullMwh} mWh");
        }
        catch (Exception ex)
        {
            Log.Error("Battery capacity read failed", ex);
        }
    }

    private static long Sum(string query, string field)
    {
        long total = 0;
        using var searcher = new ManagementObjectSearcher(@"root\WMI", query);
        foreach (ManagementObject o in searcher.Get())
        {
            using (o) total += Convert.ToInt64(o[field] ?? 0);
        }
        return total;
    }

    private static int PercentFromWindows()
    {
        float p = SystemInformation.PowerStatus.BatteryLifePercent;
        return p is >= 0 and <= 1 ? (int)Math.Round(p * 100) : -1;
    }
}
