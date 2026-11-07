using System.Runtime.InteropServices;

namespace CHelper.Native;

/// <summary>Refresh-rate control for the laptop panel (primary display).</summary>
public static class Display
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DM_DISPLAYFREQUENCY = 0x400000;
    private const int CDS_UPDATEREGISTRY = 0x01;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr lParam);

    private static string Device => Screen.PrimaryScreen?.DeviceName;

    private static DEVMODE NewMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    public static int CurrentRate()
    {
        var dm = NewMode();
        return EnumDisplaySettingsW(Device, ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : 0;
    }

    /// <summary>All refresh rates available at the current resolution, ascending.</summary>
    public static int[] AvailableRates()
    {
        var cur = NewMode();
        if (!EnumDisplaySettingsW(Device, ENUM_CURRENT_SETTINGS, ref cur)) return Array.Empty<int>();

        var rates = new SortedSet<int>();
        var dm = NewMode();
        for (int i = 0; EnumDisplaySettingsW(Device, i, ref dm); i++)
        {
            if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight && dm.dmDisplayFrequency > 1)
                rates.Add(dm.dmDisplayFrequency);
            dm = NewMode();
        }
        return rates.ToArray();
    }

    public static bool SetRate(int hz)
    {
        var dm = NewMode();
        if (!EnumDisplaySettingsW(Device, ENUM_CURRENT_SETTINGS, ref dm)) return false;
        if (dm.dmDisplayFrequency == hz) return true;
        dm.dmDisplayFrequency = hz;
        dm.dmFields = DM_DISPLAYFREQUENCY;
        int r = ChangeDisplaySettingsExW(Device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        Log.Info($"Refresh rate -> {hz} Hz (result {r})");
        return r == DISP_CHANGE_SUCCESSFUL;
    }
}
