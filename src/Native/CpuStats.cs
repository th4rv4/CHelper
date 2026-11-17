using System.Runtime.InteropServices;

namespace CHelper.Native;

/// <summary>
/// Total CPU usage (like Task Manager) and the average real clock across all cores. Usage comes from
/// GetSystemTimes; the clock from the "Processor Information" counters (base frequency × % performance),
/// which follow turbo and throttling. Both need two samples, so the first call after start returns nothing.
/// Call from one thread at a time (the sensor poll).
/// </summary>
public static class CpuStats
{
    private static long _idle, _kernel, _user;
    private static IntPtr _query, _perf, _freq;
    private static bool _pdhFailed;

    /// <summary>Usage 0-100 and clock in MHz; -1 when not known (yet).</summary>
    public static (int Usage, int Mhz) Read() => (Usage(), Mhz());

    private static int Usage()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return -1;
        long dIdle = idle - _idle, dKernel = kernel - _kernel, dUser = user - _user;
        bool first = _kernel == 0;
        (_idle, _kernel, _user) = (idle, kernel, user);
        long total = dKernel + dUser; // kernel time includes idle time
        if (first || total <= 0) return -1;
        return (int)Math.Clamp(Math.Round(100.0 * (total - dIdle) / total), 0, 100);
    }

    private static int Mhz()
    {
        if (_pdhFailed) return -1;
        try
        {
            if (_query == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0 ||
                    PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _perf) != 0 ||
                    PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _freq) != 0)
                {
                    _pdhFailed = true;
                    Log.Info("CPU clock counters not available");
                    return -1;
                }
                PdhCollectQueryData(_query); // first sample
                return -1;
            }
            if (PdhCollectQueryData(_query) != 0) return -1;
            if (PdhGetFormattedCounterValue(_perf, PDH_FMT_DOUBLE, IntPtr.Zero, out var perf) != 0 || perf.CStatus != 0) return -1;
            if (PdhGetFormattedCounterValue(_freq, PDH_FMT_DOUBLE, IntPtr.Zero, out var freq) != 0 || freq.CStatus != 0) return -1;
            double mhz = freq.Value * perf.Value / 100.0;
            return mhz > 0 ? (int)Math.Round(mhz) : -1;
        }
        catch (Exception ex)
        {
            _pdhFailed = true;
            Log.Error("CPU clock read failed", ex);
            return -1;
        }
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhValue
    {
        public uint CStatus;
        public double Value; // union { long, double, ... } aligned to 8
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhValue value);
}
