using CHelper.Native;

namespace CHelper.Hardware;

/// <summary>BIOS power profile (cmd 0x79, sub 0x19). Values confirmed from the vendor's event log.</summary>
public enum PowerMode
{
    Quiet = 0,
    PowerSaving = 1,
    Performance = 2,
    Entertainment = 3,
}

/// <summary>Fan mode (cmd 0x79, sub 0x01). Values from the vendor Control Center fan buttons.</summary>
public enum FanMode
{
    Automatic = 0,
    Maximum = 1,
    Custom = 6,     // C-Helper's own CPU/GPU curves (software, via cmd 0x68); the EC is never sent 6
    Noiseless = 8,
}

/// <summary>Live fan duty (0-100 %) and temperature (°C) for one fan.</summary>
public readonly record struct FanInfo(int Temp, int Duty);

public static class Clevo
{
    // Fan info (same layout as Linux clevo-acpi): byte0 = duty 0..255, byte2 = temperature °C
    private const int CmdFanCpu = 0x63;
    private const int CmdFanGpu = 0x64;

    private static FanInfo ReadFan(int cmd)
    {
        int v = Dchu.GetInt(cmd);
        return new FanInfo((v >> 16) & 0xFF, (int)Math.Round((v & 0xFF) * 100 / 255.0));
    }

    public static FanInfo ReadCpuFan() => ReadFan(CmdFanCpu);
    public static FanInfo ReadGpuFan() => ReadFan(CmdFanGpu);

    private const int CmdOpt = 0x79;
    private const int SubProfile = 0x19;
    private const int SubFan = 0x01;
    private const int CmdKeyboard = 0x67;
    private const int KbSubColorZone0 = 0xF0;
    private const int KbSubBrightness = 0xF4;
    private const int CmdWebcam = 0x22;
    private const int SubKbTimeout = 0x18;
    private const int CmdFanDuty = 0x68;   // fan1 | fan2 << 8 | fan3 << 16, duty 0-255 (confirmed)
    private const int CmdFanAuto = 0x69;   // 0x0F hands every fan back to the EC (confirmed)


    /// <summary>Vendor packing: value in the low bytes, sub-command in the top byte.</summary>
    private static void Send(int cmd, int sub, int value, bool log = true)
    {
        byte[] b = BitConverter.GetBytes(value);
        b[3] = (byte)sub;
        Dchu.SetData(cmd, b, log);
    }

    public static void SetPowerMode(PowerMode mode) => Send(CmdOpt, SubProfile, (int)mode);

    public static void SetFanMode(FanMode mode) => Send(CmdOpt, SubFan, (int)mode);

    /// <summary>Brightness in percent; the BIOS takes 0-255 (0, 191 and 255 verified on hardware).</summary>
    public static void SetKeyboardBrightness(int percent) =>
        Send(CmdKeyboard, KbSubBrightness, (int)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0));

    public static void SetKeyboardColor(Color c, bool log = true) =>
        Send(CmdKeyboard, KbSubColorZone0, (c.B << 16) | (c.R << 8) | c.G, log);

    /// <summary>Raw brightness 0-255, used by the effects (no logging).</summary>
    public static void SetKeyboardBrightnessRaw(int raw) =>
        Send(CmdKeyboard, KbSubBrightness, Math.Clamp(raw, 0, 255), log: false);

    /// <summary>
    /// The EC turns the backlight off after this many idle seconds and back on at the next key press
    /// (vendor: cmd 0x79 sub 0x18, value = seconds << 8 | 0xFF; 0 = never). Vendor default 30 s.
    /// </summary>
    public static void SetKeyboardTimeout(int seconds) =>
        Send(CmdOpt, SubKbTimeout, seconds <= 0 ? 0 : (Math.Min(seconds, 0xFFFF) << 8) | 0xFF);

    /// <summary>Direct fan duty in percent. The EC keeps it until <see cref="SetFanAuto"/>.</summary>
    public static void SetFanDuty(int cpuPercent, int gpuPercent, bool log = false)
    {
        int c = (int)Math.Round(Math.Clamp(cpuPercent, 0, 100) * 255 / 100.0);
        int g = (int)Math.Round(Math.Clamp(gpuPercent, 0, 100) * 255 / 100.0);
        Dchu.SetData(CmdFanDuty, BitConverter.GetBytes(c | (g << 8) | (g << 16)), log);
    }

    /// <summary>Hand all fans back to the EC's own control.</summary>
    public static void SetFanAuto() => Dchu.SetData(CmdFanAuto, BitConverter.GetBytes(0x0F));

    /// <summary>Touchpad switch state (Linux clevo: GET_TOUCHPAD_SW 0x09). Logged for verification.</summary>
    public static int ReadTouchpadRaw() => Dchu.GetInt(0x09);

    public static void SetWebcam(bool on) => Dchu.SetData(CmdWebcam, BitConverter.GetBytes(on ? 1 : 0));
}
