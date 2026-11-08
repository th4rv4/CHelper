using System.Runtime.InteropServices;
using CHelper.Hardware;

namespace CHelper.Native;

/// <summary>
/// Windows' own power mode slider (Settings → System → Power), paired with each laptop power mode
/// the way G-Helper does it, so Windows schedules the CPU to match.
/// </summary>
public static class WindowsPower
{
    private static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid Balanced = Guid.Empty;
    private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static string NameFor(PowerMode mode) => For(mode) == BestPerformance ? "Best Performance"
        : For(mode) == BestEfficiency ? "Best Power Efficiency" : "Balanced";

    private static Guid For(PowerMode mode) => mode switch
    {
        PowerMode.Quiet or PowerMode.PowerSaving => BestEfficiency,
        PowerMode.Performance => BestPerformance,
        _ => Balanced,
    };

    public static void Apply(PowerMode mode)
    {
        // Windows' Battery Saver forces its own mode; leave it alone then (G-Helper does the same).
        if (GetSystemPowerStatus(out var ps) && ps.SystemStatusFlag == 1)
        {
            Log.Info("Windows power mode skipped (Battery Saver is on)");
            return;
        }

        var target = For(mode);
        uint status = PowerGetEffectiveOverlayScheme(out Guid current);
        if (status == 0 && current == target) return;

        status = PowerSetActiveOverlayScheme(target);
        Log.Info($"Windows power mode -> {NameFor(mode)} ({(status == 0 ? "OK" : "error " + status)})");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
}
