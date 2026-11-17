using System.Runtime.InteropServices;

namespace CHelper.Native;

/// <summary>
/// NVIDIA GPU usage and graphics clock from the driver's NVML library (nvml.dll, installed with the driver).
/// Only call while the dGPU is powered on: the call keeps it awake, and with the dGPU off there's nothing to read.
/// Call from one thread at a time (the sensor poll).
/// </summary>
public static class NvGpu
{
    private static bool _ready, _unavailable;
    private static IntPtr _device;

    /// <summary>Usage 0-100 and graphics clock in MHz; -1 when it can't be read.</summary>
    public static (int Usage, int Mhz) Read()
    {
        if (_unavailable) return (-1, -1);
        try
        {
            if (!_ready)
            {
                int rc = nvmlInit_v2();
                if (rc != 0) { Fail($"init {rc}", permanent: false); return (-1, -1); }
                rc = nvmlDeviceGetHandleByIndex_v2(0, out _device);
                if (rc != 0) { nvmlShutdown(); Fail($"device {rc}", permanent: false); return (-1, -1); }
                _ready = true;
            }

            int usage = nvmlDeviceGetUtilizationRates(_device, out var util) == 0 ? (int)util.Gpu : -1;
            int mhz = nvmlDeviceGetClockInfo(_device, 0 /* NVML_CLOCK_GRAPHICS */, out uint clock) == 0 ? (int)clock : -1;
            if (usage < 0 && mhz < 0) Reset(); // the dGPU went away (Eco): start over next time
            return (usage, mhz);
        }
        catch (DllNotFoundException)
        {
            Fail("nvml.dll not found");
            return (-1, -1);
        }
        catch (Exception ex)
        {
            Log.Error("GPU usage read failed", ex);
            Reset();
            return (-1, -1);
        }
    }

    /// <summary>Forget the device, e.g. after the dGPU was switched off and on again.</summary>
    public static void Reset()
    {
        if (!_ready) return;
        _ready = false;
        try { nvmlShutdown(); } catch { }
    }

    private static string _lastFail;

    private static void Fail(string why, bool permanent = true)
    {
        if (permanent) _unavailable = true;
        if (why == _lastFail) return; // the poll retries every 2 s: log each reason once
        _lastFail = why;
        Log.Info($"GPU usage not available ({why})");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [DllImport("nvml.dll")]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll")]
    private static extern int nvmlShutdown();

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);

    [DllImport("nvml.dll")]
    private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint clock);
}
