using System.Management;
using CHelper.Native;

namespace CHelper.Hardware;

public enum GpuMode
{
    Eco,        // dGPU powered off (instant, no restart)
    Standard,   // MSHybrid, dGPU on
    Optimized,  // Eco on battery, Standard on AC
    Ultimate,   // MUX: Discrete GPU only (restart)
}

public static class Gpu
{
    // Extended sub-commands (Package_RW, cmd 4)
    private const byte ExMuxGet = 21;
    private const byte ExMuxSet = 22;
    private const byte ExDgpuPower = 34;

    // MUX modes as numbered by the vendor's GPU switch panel
    public const int MuxIgpuOnly = 1;
    public const int MuxDgpuOnly = 2;
    public const int MuxHybrid = 3;
    public const int MuxDynamic = 4;

    private const string NvpcfId = @"ACPI\NVDA0820\NPCF";

    public static (int Current, int SupportedMask) ReadMux()
    {
        byte[] o = Dchu.Ex(ExMuxGet);
        return (o[0], o[1]);
    }

    public static bool MuxSupports(int mask, int mode) => (mask & (1 << (mode - 1))) != 0;

    /// <summary>Takes effect after a restart.</summary>
    public static void WriteMux(int mode) => Dchu.Ex(ExMuxSet, (byte)mode);

    /// <summary>
    /// The vendor's dGPU power sequence (cc30wk.exe 0x0013):
    /// OFF = disable NVPCF (x3), then ex 34 = 0.  ON = ex 34 = 1, then enable NVPCF.
    /// </summary>
    public static void SetDgpuPower(bool on)
    {
        bool haveNvpcf = CfgMgr.CM_Locate_DevNodeW(out uint dev, NvpcfId, 0) == CfgMgr.CR_SUCCESS;
        if (!haveNvpcf) Log.Info("NVPCF device not found; sending the BIOS command only");

        if (on)
        {
            Dchu.Ex(ExDgpuPower, 1);
            Thread.Sleep(100);
            if (haveNvpcf) Log.Info($"NVPCF enable -> {CfgMgr.CM_Enable_DevNode(dev, 0)}");
        }
        else
        {
            if (haveNvpcf)
            {
                for (int i = 0; i < 3; i++)
                {
                    CfgMgr.CM_Disable_DevNode(dev, 0);
                    Thread.Sleep(100);
                }
                Log.Info("NVPCF disabled");
            }
            Dchu.Ex(ExDgpuPower, 0);
        }
    }

    /// <summary>True when an NVIDIA display adapter is present on the bus.</summary>
    public static bool IsDgpuPresent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                if ((o["Name"] as string ?? "").Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("GPU presence query failed", ex);
        }
        return false;
    }
}
