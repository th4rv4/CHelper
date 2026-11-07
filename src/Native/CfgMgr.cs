using System.Runtime.InteropServices;

namespace CHelper.Native;

internal static class CfgMgr
{
    public const int CR_SUCCESS = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

    // Flags 0 = not persistent across reboots (same as the vendor's cc30wk.exe).
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Disable_DevNode(uint devInst, int flags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Enable_DevNode(uint devInst, int flags);
}
