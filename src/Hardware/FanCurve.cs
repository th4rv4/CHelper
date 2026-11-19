namespace CHelper.Hardware;

/// <summary>One point of a fan curve: at <see cref="Temp"/> °C the fan runs at <see cref="Duty"/> %.</summary>
public sealed record CurvePoint(int Temp, int Duty);

/// <summary>8-point fan curves (like G-Helper), evaluated in software by C-Helper.</summary>
public static class FanCurve
{
    public const int Points = 8;
    public const int MinTemp = 20, MaxTemp = 100;

    // Tuned for the i7-13620H (TjMax 100 °C) and the RTX 4060 Laptop (throttles from 90 °C), from measurements of
    // what the BIOS does in each power mode under full load. Every preset reaches full speed a few degrees below the
    // limit of the power mode it belongs to, so the fans do the cooling before the BIOS has to cut power:
    //   Turbo     CPU held at ~98 °C          -> Gaming:   100 % from 80 °C CPU / 73 °C GPU, a little ahead of the BIOS
    //   Balanced  CPU held at exactly 87 °C   -> Balanced: 100 % from 84 °C CPU / 80 °C GPU
    //   Quiet, Power Saving  CPU limited to 15 W, GPU 70 W -> Quiet: silent until ~60 °C, 100 % from 90 °C CPU / 86 °C GPU

    /// <summary>Above this the fan runs at 100 % whatever the curve says: CPU 95 °C (TjMax 100), GPU 87 °C (throttles from 90).</summary>
    public const int SafetyCpu = 95, SafetyGpu = 87;

    /// <summary>Below this a fan may not start reliably, so non-zero targets are raised to it.</summary>
    public const int MinSpin = 20;

    public static readonly string[] PresetNames = { "Quiet", "Balanced", "Gaming" };

    public static CurvePoint[] Preset(string name, bool gpu) => (name, gpu) switch
    {
        // Quiet: fans off until ~60 °C, gentle through the middle, full speed before the GPU gets near its throttle point.
        ("Quiet", false) => Make((50, 0), (60, 0), (66, 22), (72, 32), (78, 46), (83, 62), (87, 80), (90, 100)),
        ("Quiet", true) => Make((48, 0), (56, 0), (62, 22), (68, 32), (74, 44), (79, 56), (83, 74), (86, 100)),
        // Gaming: always on and ramping early, so games that don't max the laptop out still run a few degrees cooler
        // (and boost higher); full speed from 80 °C CPU / 73 °C GPU, well before the 4060 throttles.
        ("Gaming", false) => Make((30, 30), (45, 38), (55, 50), (63, 62), (70, 75), (76, 88), (80, 100), (88, 100)),
        ("Gaming", true) => Make((30, 30), (45, 38), (52, 50), (58, 62), (63, 75), (68, 88), (73, 100), (80, 100)),
        // Balanced: silent at idle, steady ramp, full speed from 84 °C so the CPU stays under Balanced's 87 °C limit.
        (_, false) => Make((40, 0), (52, 22), (60, 30), (66, 40), (71, 54), (76, 70), (80, 86), (84, 100)),
        (_, true) => Make((40, 0), (50, 22), (57, 30), (63, 42), (68, 56), (73, 72), (77, 88), (80, 100)),
    };

    private static CurvePoint[] Make(params (int T, int D)[] p) => p.Select(x => new CurvePoint(x.T, x.D)).ToArray();

    /// <summary>The presets as they were before v1.0.9, to recognise curves nobody edited and move them to the new ones.</summary>
    private static CurvePoint[] OldPreset(string name, bool gpu) => (name, gpu) switch
    {
        ("Quiet", false) => Make((50, 0), (60, 0), (66, 22), (73, 30), (80, 42), (86, 58), (91, 78), (95, 100)),
        ("Quiet", true) => Make((48, 0), (56, 0), (62, 22), (68, 32), (74, 46), (79, 64), (83, 82), (87, 100)),
        ("Gaming", false) => Make((40, 25), (55, 32), (65, 44), (72, 56), (78, 68), (84, 82), (89, 94), (93, 100)),
        ("Gaming", true) => Make((40, 28), (50, 35), (58, 48), (64, 62), (69, 76), (73, 88), (77, 100), (85, 100)),
        ("Cool", false) => Make((30, 30), (45, 38), (55, 50), (63, 62), (70, 75), (76, 88), (82, 100), (88, 100)),
        ("Cool", true) => Make((30, 30), (45, 38), (52, 50), (58, 62), (64, 75), (70, 88), (75, 100), (80, 100)),
        (_, false) => Make((40, 0), (52, 22), (60, 28), (68, 38), (75, 52), (82, 68), (88, 85), (93, 100)),
        (_, true) => Make((40, 0), (50, 22), (57, 28), (64, 40), (70, 54), (75, 70), (80, 86), (85, 100)),
    };

    /// <summary>
    /// Moves a saved curve set to the current presets. A curve still exactly as an older version's preset (nobody
    /// edited it) becomes the current version of that preset; the old Cool preset became Gaming. Anything the user
    /// changed is kept as it is, and an edited Cool curve is kept as a custom curve.
    /// </summary>
    public static void Upgrade(FanCurveSet set)
    {
        if (set?.Preset is not ("Quiet" or "Balanced" or "Gaming" or "Cool")) return;
        string now = set.Preset == "Cool" ? "Gaming" : set.Preset;
        bool untouched = set.Cpu != null && set.Gpu != null &&
                         set.Cpu.SequenceEqual(OldPreset(set.Preset, false)) && set.Gpu.SequenceEqual(OldPreset(set.Preset, true));
        if (untouched)
        {
            set.Cpu = Preset(now, gpu: false);
            set.Gpu = Preset(now, gpu: true);
            set.Preset = now;
        }
        else if (set.Preset == "Cool")
        {
            set.Preset = "Custom";
        }
    }

    /// <summary>Linear interpolation between points; flat before the first and after the last.</summary>
    public static int Evaluate(IReadOnlyList<CurvePoint> curve, int temp)
    {
        if (curve == null || curve.Count == 0) return 100;
        if (temp <= curve[0].Temp) return curve[0].Duty;
        for (int i = 1; i < curve.Count; i++)
        {
            var a = curve[i - 1];
            var b = curve[i];
            if (temp <= b.Temp)
            {
                if (b.Temp == a.Temp) return b.Duty;
                return (int)Math.Round(a.Duty + (b.Duty - a.Duty) * (temp - a.Temp) / (double)(b.Temp - a.Temp));
            }
        }
        return curve[^1].Duty;
    }

    /// <summary>Target duty for a fan: curve at the (smoothed) temperature, safety override on the raw one, minimum spin.</summary>
    public static int Target(IReadOnlyList<CurvePoint> curve, int curveTemp, int rawTemp, bool gpu)
    {
        if (rawTemp >= (gpu ? SafetyGpu : SafetyCpu)) return 100;
        int d = Evaluate(curve, curveTemp);
        return d > 0 && d < MinSpin ? MinSpin : d;
    }

    /// <summary>Repairs a curve loaded from settings: 8 points, sorted, within range, duty never decreasing.</summary>
    public static CurvePoint[] Sanitize(CurvePoint[] curve, bool gpu)
    {
        if (curve == null || curve.Length != Points) return Preset("Balanced", gpu);
        var pts = curve.OrderBy(p => p.Temp)
            .Select(p => new CurvePoint(Math.Clamp(p.Temp, MinTemp, MaxTemp), Math.Clamp(p.Duty, 0, 100)))
            .ToArray();
        for (int i = 1; i < pts.Length; i++)
        {
            if (pts[i].Duty < pts[i - 1].Duty) pts[i] = pts[i] with { Duty = pts[i - 1].Duty };
        }
        return pts;
    }
}

/// <summary>CPU + GPU curves for one power mode.</summary>
public sealed class FanCurveSet
{
    public CurvePoint[] Cpu { get; set; }
    public CurvePoint[] Gpu { get; set; }
    public string Preset { get; set; }

    public static string DefaultPreset(PowerMode mode) => mode switch
    {
        PowerMode.Quiet or PowerMode.PowerSaving => "Quiet",
        PowerMode.Performance => "Gaming",
        _ => "Balanced",
    };

    public static FanCurveSet Default(PowerMode mode)
    {
        string p = DefaultPreset(mode);
        return new FanCurveSet { Cpu = FanCurve.Preset(p, gpu: false), Gpu = FanCurve.Preset(p, gpu: true), Preset = p };
    }
}
