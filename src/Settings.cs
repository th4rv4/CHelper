using System.Text.Json;
using System.Text.Json.Serialization;
using CHelper.Hardware;

namespace CHelper;

public sealed class AppSettings
{
    public string DllPath { get; set; } = "";

    // Power mode, remembered separately for AC and battery (like G-Helper)
    public bool AutoPowerMode { get; set; } = true;
    public PowerMode ModeAC { get; set; } = PowerMode.Entertainment;
    public PowerMode ModeDC { get; set; } = PowerMode.PowerSaving;

    public GpuMode Gpu { get; set; } = GpuMode.Standard;
    /// <summary>Automatic = follow the power mode (Quiet uses the Noiseless curve). Maximum/Custom are manual overrides.</summary>
    public FanMode Fan { get; set; } = FanMode.Automatic;

    /// <summary>Custom fan curves per power mode (used when <see cref="Fan"/> is Custom).</summary>
    public Dictionary<PowerMode, FanCurveSet> FanCurves { get; set; } = new();

    // v0.6 single curve set, migrated into FanCurves on load.
    public CurvePoint[] FanCurveCpu { get; set; }
    public CurvePoint[] FanCurveGpu { get; set; }
    public string FanPreset { get; set; }

    /// <summary>0 = Auto (lowest on battery, highest on AC), otherwise a fixed rate in Hz.</summary>
    public int ScreenRate { get; set; } = 0;

    public int KeyboardColor { get; set; } = unchecked((int)0xFF7B5CFF);
    /// <summary>Keyboard backlight brightness, 0-100 %.</summary>
    public int KeyboardBrightness { get; set; } = 75;
    public KbEffect KeyboardEffect { get; set; } = KbEffect.Static;
    public KbSpeed KeyboardSpeed { get; set; } = KbSpeed.Normal;
    public HeatSource KeyboardHeatSource { get; set; } = HeatSource.Hottest;
    /// <summary>Backlight turns off after this many idle seconds (EC timer); 0 = never. Plugged in / on battery.</summary>
    public int KeyboardTimeoutAC { get; set; } = 300;
    public int KeyboardTimeoutDC { get; set; } = 15;

    // v0.6 single timeout, migrated on load.
    public int? KeyboardTimeout { get; set; }
    public bool Camera { get; set; } = true;

    /// <summary>Fn+Esc opens C-Helper instead of the vendor Control Center.</summary>
    public bool FnEscOpensHelper { get; set; } = true;

    /// <summary>Full Charge Once: the charge limit to put back after unplugging (0 = not active).</summary>
    public int FullChargeRestore { get; set; }

    /// <summary>Check GitHub for a newer C-Helper once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Newest version the "Update Available" popup was shown for (shown once per version).</summary>
    public string UpdateNotified { get; set; }

    /// <summary>Debugging: log non-typing keys (see LockKeys.LogSpecialKeys). Off by default.</summary>
    public bool LogSpecialKeys { get; set; }
}

public static class Settings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CHelper", "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Settings load failed, using defaults", ex);
            Current = new AppSettings();
        }
        // v0.5: Noiseless is no longer a separate choice; it comes with Quiet.
        if (Current.Fan == FanMode.Noiseless) Current.Fan = FanMode.Automatic;
        var s = Current;
        s.FanCurves ??= new();
        foreach (PowerMode mode in Enum.GetValues<PowerMode>())
        {
            if (!s.FanCurves.TryGetValue(mode, out var set) || set == null)
            {
                // A curve tuned in v0.6 carries over to every mode; otherwise each mode gets its own default preset.
                set = s.FanCurveCpu != null
                    ? new FanCurveSet { Cpu = s.FanCurveCpu, Gpu = s.FanCurveGpu, Preset = s.FanPreset ?? "Custom" }
                    : FanCurveSet.Default(mode);
            }
            set.Cpu = FanCurve.Sanitize(set.Cpu, gpu: false);
            set.Gpu = FanCurve.Sanitize(set.Gpu, gpu: true);
            s.FanCurves[mode] = set;
        }
        s.FanCurveCpu = s.FanCurveGpu = null;
        s.FanPreset = null;

        if (s.KeyboardTimeout is int old)
        {
            s.KeyboardTimeoutAC = old;
            s.KeyboardTimeoutDC = old;
            s.KeyboardTimeout = null;
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception ex)
        {
            Log.Error("Settings save failed", ex);
        }
    }
}
