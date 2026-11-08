using CHelper.Hardware;
using CHelper.Native;
using Microsoft.Win32;

namespace CHelper;

/// <summary>All hardware logic. Methods are blocking; call them off the UI thread.</summary>
public sealed class Controller
{
    public static Controller Instance { get; } = new();

    public event Action StateChanged;
    public event Action SensorsChanged;

    private static AppSettings S => Settings.Current;
    private readonly object _gpuGate = new();

    public PowerMode CurrentMode { get; private set; } = PowerMode.Entertainment;
    public int MuxCurrent { get; private set; } = Gpu.MuxHybrid;
    public int MuxMask { get; private set; }
    public bool DgpuPresent { get; private set; } = true;
    public bool GpuBusy { get; private set; }
    public int[] ScreenRates { get; private set; } = Array.Empty<int>();
    public int ScreenRate { get; private set; }
    public Charger.State Charge { get; private set; }
    public FanInfo CpuFan { get; private set; }
    public FanInfo GpuFan { get; private set; }
    public Battery.Status? BatteryNow { get; private set; }
    public double BatteryHealth { get; private set; } = -1;

    /// <summary>Software keyboard effects (Breathing, Color Cycle, CPU Heat).</summary>
    public KeyboardEffects Effects { get; } = new();

    /// <summary>Duty the custom curves are currently asking for (-1 when curves are off).</summary>
    public int CurveCpuDuty { get; private set; } = -1;
    public int CurveGpuDuty { get; private set; } = -1;
    public bool CurvesActive => _curveTimer != null;

    public static bool OnAC => SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Offline;

    /// <summary>dGPU Only (MUX) is active: the NVIDIA GPU drives the panel, so it must never be powered off.</summary>
    public bool InUltimate => MuxCurrent == Gpu.MuxDgpuOnly;

    public void Start()
    {
        try
        {
            (MuxCurrent, MuxMask) = Gpu.ReadMux();
            Log.Info($"MUX current={MuxCurrent} mask=0x{MuxMask:X2}");
        }
        catch (Exception ex)
        {
            Log.Error("MUX read failed", ex);
        }

        // Keep the saved GPU mode consistent with what the BIOS will actually boot into.
        if (InUltimate) S.Gpu = GpuMode.Ultimate;
        else if (S.Gpu == GpuMode.Ultimate) S.Gpu = GpuMode.Standard;

        DgpuPresent = Gpu.IsDgpuPresent();
        Safe("charger read", () => Charge = Charger.Read());
        VendorHotkey.SetEnabled(S.FnEscOpensHelper);
        HotkeyListener.Start();
        ApplyAll("startup");
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private bool? _lastAC;
    private CancellationTokenSource _sourceDebounce;

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange)
        {
            // Windows sends bursts of StatusChange (battery level, dGPU toggling...).
            // Wait for it to settle, then act only if plugged-in really changed.
            _sourceDebounce?.Cancel();
            var cts = _sourceDebounce = new CancellationTokenSource();
            Task.Run(async () =>
            {
                try { await Task.Delay(1500, cts.Token); }
                catch (TaskCanceledException) { return; }
                bool ac = OnAC;
                if (_lastAC == ac) return;
                _lastAC = ac;
                ApplySourceDependent("power source changed");
            });
        }
        else if (e.Mode == PowerModes.Suspend)
        {
            Effects.Stop();
            StopCurves("suspend");
        }
        else if (e.Mode == PowerModes.Resume)
        {
            Task.Run(async () =>
            {
                await Task.Delay(3000);
                Safe("hotkeys", HotkeyListener.Restart);
                ApplyAll("resume");
            });
        }
    }

    private void Changed() => StateChanged?.Invoke();

    private void Safe(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error(what, ex); }
    }

    public void ApplyAll(string reason)
    {
        Log.Info($"Apply all ({reason})");
        _lastAC = OnAC;
        Safe("power mode", () => ApplyPowerMode());
        Safe("keyboard", () =>
        {
            Clevo.SetKeyboardTimeout(KeyboardTimeoutNow);
            ApplyKeyboardLighting();
        });
        Safe("camera", () => Clevo.SetWebcam(S.Camera));
        Safe("gpu", () => ApplyGpu());
        Safe("screen", () => ApplyScreen());
        Changed();
    }

    private void ApplySourceDependent(string reason)
    {
        Log.Info($"Apply source-dependent ({reason}), AC={OnAC}");
        Safe("power mode", () => ApplyPowerMode());
        Safe("gpu", () => ApplyGpu());
        Safe("screen", () => ApplyScreen());
        Safe("keyboard", () =>
        {
            Clevo.SetKeyboardTimeout(KeyboardTimeoutNow);
            ApplyKeyboardLighting(); // effects pause by the new idle limit
        });
        Changed();
    }

    // ---------------- Power mode ----------------

    public PowerMode ModeForSource => S.AutoPowerMode && !OnAC ? S.ModeDC : S.ModeAC;

    private void ApplyPowerMode()
    {
        CurrentMode = ModeForSource;
        Clevo.SetPowerMode(CurrentMode);
        Safe("windows power mode", () => WindowsPower.Apply(CurrentMode));
        ApplyFan(); // after the profile, which may reset the fan mode
    }

    /// <summary>Quiet = quiet power profile + Noiseless fan curve, unless the fans are overridden by hand.</summary>
    public FanMode EffectiveFan => S.Fan != FanMode.Automatic ? S.Fan
        : CurrentMode == PowerMode.Quiet ? FanMode.Noiseless : FanMode.Automatic;

    private void ApplyFan()
    {
        var fan = EffectiveFan;
        Log.Info($"Fan mode {fan} (power mode {CurrentMode}, choice {S.Fan})");
        if (fan == FanMode.Custom)
        {
            // The EC stays in Auto underneath; C-Helper overrides the duty from the curves.
            Clevo.SetFanMode(FanMode.Automatic);
            StartCurves();
        }
        else
        {
            StopCurves("fan mode " + fan);
            Clevo.SetFanMode(fan);
        }
    }

    // ---------------- Custom fan curves ----------------

    private readonly object _curveGate = new();
    private double _cpuTempAvg = -1;
    private System.Threading.Timer _curveTimer;

    private void StartCurves()
    {
        lock (_curveGate)
        {
            if (_curveTimer != null) return;
            _curveTimer = new System.Threading.Timer(_ => CurveTick(), null, 0, 2000);
            Log.Info("Custom fan curves on");
        }
    }

    /// <summary>Stops the curve loop and hands the fans back to the EC (cmd 0x69 = 0x0F, confirmed).</summary>
    public void StopCurves(string reason)
    {
        lock (_curveGate)
        {
            if (_curveTimer == null) return;
            _curveTimer.Dispose();
            _curveTimer = null;
            CurveCpuDuty = CurveGpuDuty = -1;
            _cpuTempAvg = -1;
            Safe("fan auto", Clevo.SetFanAuto);
            Log.Info($"Custom fan curves off ({reason}), fans back to the EC");
        }
    }

    private void CurveTick()
    {
        if (!Monitor.TryEnter(_curveGate)) return;
        try
        {
            if (_curveTimer == null) return;
            var cpu = Clevo.ReadCpuFan();
            var gpu = Clevo.ReadGpuFan();
            int cpuT = cpu.Temp;
            // With the dGPU off its sensor reads junk; the GPU fan then follows the CPU temperature.
            int gpuT = DgpuPresent || InUltimate ? gpu.Temp : cpuT;
            if (cpuT <= 0 || cpuT > 125)
            {
                // Bad reading: don't guess, let the EC drive the fans until the next tick.
                Clevo.SetFanAuto();
                CurveCpuDuty = CurveGpuDuty = -1;
                return;
            }

            // The CPU sensor jumps 10-15 °C on short turbo bursts; follow a smoothed value so the fans
            // don't rev on every spike. The safety override still uses the raw reading.
            _cpuTempAvg = _cpuTempAvg < 0 ? cpuT : _cpuTempAvg + 0.35 * (cpuT - _cpuTempAvg);
            int cpuCurveT = (int)Math.Round(_cpuTempAvg);
            var set = CurvesFor(CurrentMode);
            int c = Smooth(CurveCpuDuty, FanCurve.Target(set.Cpu, cpuCurveT, cpuT, gpu: false));
            int g = Smooth(CurveGpuDuty, FanCurve.Target(set.Gpu, gpuT, gpuT, gpu: true));
            if (CurveCpuDuty < 0) Log.Info($"Fan curves: CPU {cpuT}°C -> {c}%, GPU {gpuT}°C -> {g}%");
            Clevo.SetFanDuty(c, g); // re-sent every tick in case the EC took over by itself
            CurveCpuDuty = c;
            CurveGpuDuty = g;
        }
        catch (Exception ex)
        {
            Log.Error("Fan curve tick failed", ex);
            try { Clevo.SetFanAuto(); } catch { /* best effort */ }
        }
        finally
        {
            Monitor.Exit(_curveGate);
        }
    }

    /// <summary>Speed up at once; slow down by at most 5 % per tick so the fans don't hunt.</summary>
    private static int Smooth(int current, int target) =>
        current < 0 || target >= current ? target : Math.Max(target, current - 5);

    /// <summary>Saves new curves and switches the fans to Custom.</summary>
    /// <summary>The curves for a power mode (always present after Settings.Load).</summary>
    public FanCurveSet CurvesFor(PowerMode mode) =>
        S.FanCurves.TryGetValue(mode, out var set) && set != null ? set : FanCurveSet.Default(mode);

    /// <summary>Saves the curves of every edited mode and switches the fans to Custom.</summary>
    public void SetFanCurves(IReadOnlyDictionary<PowerMode, FanCurveSet> sets)
    {
        foreach (var (mode, set) in sets)
        {
            S.FanCurves[mode] = new FanCurveSet
            {
                Cpu = FanCurve.Sanitize(set.Cpu, gpu: false),
                Gpu = FanCurve.Sanitize(set.Gpu, gpu: true),
                Preset = set.Preset,
            };
        }
        S.Fan = FanMode.Custom;
        Settings.Save();
        Safe("fan mode", ApplyFan);
        Changed();
    }

    /// <summary>Called on quit and on crash: never leave the fans at a fixed duty, or the keyboard mid-effect.</summary>
    public void Shutdown()
    {
        StopCurves("exit");
        if (Effects.Running)
        {
            Effects.Stop();
            Safe("keyboard", () =>
            {
                Clevo.SetKeyboardColor(Color.FromArgb(S.KeyboardColor));
                Clevo.SetKeyboardBrightness(S.KeyboardBrightness);
            });
        }
    }

    public void SetPowerMode(PowerMode mode)
    {
        if (S.AutoPowerMode && !OnAC) S.ModeDC = mode;
        else S.ModeAC = mode;
        if (S.Fan == FanMode.Maximum) S.Fan = FanMode.Automatic; // picking a mode ends a Maximum override; custom curves stay
        Settings.Save();
        Safe("power mode", () => ApplyPowerMode());
        Changed();
    }

    public void SetAutoPowerMode(bool on)
    {
        S.AutoPowerMode = on;
        Settings.Save();
        Safe("power mode", () => ApplyPowerMode());
        Changed();
    }

    // ---------------- GPU ----------------

    /// <returns>true when a restart is needed for the change to take effect.</returns>
    public bool SetGpuMode(GpuMode mode)
    {
        lock (_gpuGate)
        {
            GpuBusy = true;
            Changed();
            try
            {
                if (mode == GpuMode.Ultimate)
                {
                    if (!Gpu.MuxSupports(MuxMask, Gpu.MuxDgpuOnly)) return false;
                    if (!Gpu.IsDgpuPresent()) Gpu.SetDgpuPower(true);   // never switch the MUX with the dGPU off
                    Gpu.WriteMux(Gpu.MuxDgpuOnly);
                    S.Gpu = GpuMode.Ultimate;
                    Settings.Save();
                    return true;
                }

                S.Gpu = mode;
                Settings.Save();

                if (InUltimate)
                {
                    // Leave dGPU Only first; Eco/Optimized apply after the restart.
                    Gpu.WriteMux(Gpu.MuxHybrid);
                    return true;
                }

                ApplyGpuLocked();
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("GPU mode change failed", ex);
                return false;
            }
            finally
            {
                RefreshMux();
                GpuBusy = false;
                Changed();
            }
        }
    }

    private void ApplyGpu()
    {
        lock (_gpuGate) ApplyGpuLocked();
    }

    private void ApplyGpuLocked()
    {
        if (InUltimate) return;

        bool wantOff = S.Gpu == GpuMode.Eco || (S.Gpu == GpuMode.Optimized && !OnAC);
        bool present = Gpu.IsDgpuPresent();

        if (wantOff && present) Gpu.SetDgpuPower(false);
        else if (!wantOff && !present) Gpu.SetDgpuPower(true);
        else
        {
            DgpuPresent = present;
            return;
        }

        // The device needs a moment to leave/rejoin the bus.
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(500);
            DgpuPresent = Gpu.IsDgpuPresent();
            if (DgpuPresent != wantOff) break;
        }
        Log.Info($"dGPU present after change: {DgpuPresent}");
    }

    private void RefreshMux()
    {
        try { (MuxCurrent, MuxMask) = Gpu.ReadMux(); }
        catch (Exception ex) { Log.Error("MUX read failed", ex); }
    }

    public void RefreshSensors()
    {
        try
        {
            CpuFan = Clevo.ReadCpuFan();
            GpuFan = Clevo.ReadGpuFan();
            try
            {
                BatteryNow = Battery.Read();
                BatteryHealth = Battery.Health();
            }
            catch (Exception ex) { Log.Error("Battery read failed", ex); }
            SensorsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Sensor read failed", ex);
        }
    }

    public void RefreshDgpuPresence()
    {
        if (GpuBusy) return;
        bool p = Gpu.IsDgpuPresent();
        if (p != DgpuPresent)
        {
            DgpuPresent = p;
            Changed();
        }
    }

    // ---------------- Fans ----------------

    public void SetFanMode(FanMode mode)
    {
        S.Fan = mode == FanMode.Noiseless ? FanMode.Automatic : mode;
        Settings.Save();
        Safe("fan mode", ApplyFan);
        Changed();
    }

    // ---------------- Screen ----------------

    public void SetScreenRate(int hz)
    {
        S.ScreenRate = hz;
        Settings.Save();
        Safe("screen", () => ApplyScreen());
        Changed();
    }

    private void ApplyScreen()
    {
        ScreenRates = Display.AvailableRates();
        if (ScreenRates.Length > 0)
        {
            int target = S.ScreenRate == 0
                ? (OnAC ? ScreenRates[^1] : LowRate(ScreenRates))
                : S.ScreenRate;
            if (Array.IndexOf(ScreenRates, target) >= 0) Display.SetRate(target);
        }
        ScreenRate = Display.CurrentRate();
    }

    /// <summary>The battery-saving rate: 60 Hz when the panel offers it, otherwise the lowest.</summary>
    public static int LowRate(int[] rates) => Array.IndexOf(rates, 60) >= 0 ? 60 : rates[0];

    // ---------------- Battery ----------------

    public void SetChargeLimit(int stop)
    {
        Safe("charge limit", () =>
        {
            var state = Charge ?? Charger.Read();
            Charger.SetLimit(stop, state);
            Charge = Charger.Read();
            Log.Info($"Charge limit -> enabled={Charge.Enabled} start={Charge.Start} stop={Charge.Stop}");
        });
        Changed();
    }

    // ---------------- Fn+Esc ----------------

    public void SetFnEsc(bool on)
    {
        S.FnEscOpensHelper = on;
        Settings.Save();
        VendorHotkey.SetEnabled(on);
        Changed();
    }

    // ---------------- Keyboard / camera ----------------

    /// <summary>Static colour (also the Breathing colour). Picking a colour leaves Color Cycle / CPU Heat.</summary>
    public void SetKeyboardColor(Color c)
    {
        S.KeyboardColor = c.ToArgb();
        if (S.KeyboardEffect is KbEffect.ColorCycle or KbEffect.CpuHeat) S.KeyboardEffect = KbEffect.Static;
        Settings.Save();
        Safe("keyboard", ApplyKeyboardLighting);
        Changed();
    }

    /// <summary>Effect and (optionally) colour in one go, used by Fn + numpad "/".</summary>
    public void SetKeyboardLook(KbEffect effect, Color? color)
    {
        if (color.HasValue) S.KeyboardColor = color.Value.ToArgb();
        S.KeyboardEffect = effect;
        if (S.KeyboardBrightness == 0) S.KeyboardBrightness = 75; // cycling should never leave it dark
        Settings.Save();
        Safe("keyboard", ApplyKeyboardLighting);
        Changed();
    }

    public void SetKeyboardEffect(KbEffect effect)
    {
        S.KeyboardEffect = effect;
        Settings.Save();
        Safe("keyboard", ApplyKeyboardLighting);
        Changed();
    }

    public void SetKeyboardSpeed(KbSpeed speed)
    {
        S.KeyboardSpeed = speed;
        Settings.Save();
        Safe("keyboard", ApplyKeyboardLighting);
        Changed();
    }

    /// <summary>Backlight timeout for the current power source.</summary>
    public int KeyboardTimeoutNow => OnAC ? S.KeyboardTimeoutAC : S.KeyboardTimeoutDC;

    public void SetKeyboardTimeout(bool pluggedIn, int seconds)
    {
        if (pluggedIn) S.KeyboardTimeoutAC = seconds;
        else S.KeyboardTimeoutDC = seconds;
        Settings.Save();
        Safe("keyboard timeout", () => Clevo.SetKeyboardTimeout(KeyboardTimeoutNow));
        Safe("keyboard", ApplyKeyboardLighting); // effects need the new idle limit
        Changed();
    }

    /// <summary>Static: write colour + brightness once. Effects: (re)configure the animation loop.</summary>
    private void ApplyKeyboardLighting()
    {
        var color = Color.FromArgb(S.KeyboardColor);
        Effects.Configure(S.KeyboardEffect, S.KeyboardSpeed, color, S.KeyboardBrightness, KeyboardTimeoutNow);
        if (!Effects.Running)
        {
            Clevo.SetKeyboardColor(color);
            Clevo.SetKeyboardBrightness(S.KeyboardBrightness);
        }
    }

    private int _kbLive = -1;
    private int _kbLiveRunning;

    /// <summary>
    /// Live preview while dragging: only the newest value is sent, and never more than one call at a time,
    /// so fast drags don't queue up BIOS writes. Not saved; SetKeyboardBrightness saves on release.
    /// </summary>
    public void PreviewKeyboardBrightness(int percent)
    {
        Interlocked.Exchange(ref _kbLive, percent);
        if (Interlocked.CompareExchange(ref _kbLiveRunning, 1, 0) != 0) return;
        Task.Run(() =>
        {
            try
            {
                int v;
                while ((v = Interlocked.Exchange(ref _kbLive, -1)) >= 0)
                {
                    int b = v;
                    if (S.KeyboardEffect != KbEffect.Static && b > 0)
                        Effects.Configure(S.KeyboardEffect, S.KeyboardSpeed, Color.FromArgb(S.KeyboardColor), b, KeyboardTimeoutNow);
                    else
                    {
                        if (Effects.Running) Effects.Stop();
                        Safe("keyboard brightness", () => Clevo.SetKeyboardBrightness(b));
                    }
                    Thread.Sleep(30);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _kbLiveRunning, 0);
                if (Volatile.Read(ref _kbLive) >= 0) PreviewKeyboardBrightness(Volatile.Read(ref _kbLive));
            }
        });
    }

    public void SetKeyboardBrightness(int percent)
    {
        S.KeyboardBrightness = percent;
        Settings.Save();
        Safe("keyboard", ApplyKeyboardLighting);
        Changed();
    }

    /// <summary>The camera key already switched the hardware; just mirror it in the UI.</summary>
    public void SyncCamera(bool on)
    {
        S.Camera = on;
        Settings.Save();
        Changed();
    }

    public void SetCamera(bool on)
    {
        S.Camera = on;
        Settings.Save();
        Safe("camera", () => Clevo.SetWebcam(on));
        Changed();
    }
}
