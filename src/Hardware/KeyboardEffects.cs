using System.Runtime.InteropServices;

namespace CHelper.Hardware;

public enum KbEffect { Static, Breathing, ColorCycle, CpuHeat }

public enum KbSpeed { Slow, Normal, Fast }

/// <summary>What the Heat effect follows (KbEffect.CpuHeat is shown as "Heat").</summary>
public enum HeatSource { Cpu, Gpu, Hottest }

/// <summary>
/// Software lighting effects for the single-zone keyboard. The EC only takes a colour and a brightness,
/// so C-Helper animates them itself (~30 updates per second, unlogged).
/// While the user is idle past the backlight timeout, writes pause so the EC can switch the light off.
/// </summary>
public sealed class KeyboardEffects
{
    private const int FrameMs = 33;

    private readonly object _gate = new();
    private Thread _thread;
    private CancellationTokenSource _cts;

    private KbEffect _effect;
    private KbSpeed _speed;
    private Color _color;
    private int _brightness;   // 0-100, peak for Breathing
    private int _timeout;      // seconds, 0 = never

    /// <summary>Latest CPU temperature seen by Heat.</summary>
    public int CpuTemp { get; private set; }

    /// <summary>Temperature source for Heat. Read every second by the effect loop.</summary>
    public HeatSource Source { get; set; } = HeatSource.Hottest;

    /// <summary>Whether the dGPU is powered (its sensor reads junk in Eco). Set by the controller.</summary>
    public Func<bool> GpuOn { get; set; } = () => true;

    /// <summary>What Heat is showing, e.g. "GPU 74°C", for the window hint.</summary>
    public string HeatLabel { get; private set; } = "";

    /// <summary>Colour currently shown (for the CPU Heat swatch / popup).</summary>
    public Color CurrentColor { get; private set; }

    public bool Running => _thread != null;

    /// <summary>Start, restart or stop the effect. Static stops the loop; the caller writes the static colour.</summary>
    public void Configure(KbEffect effect, KbSpeed speed, Color color, int brightness, int timeoutSeconds)
    {
        bool stop;
        lock (_gate)
        {
            _effect = effect;
            _speed = speed;
            _color = color;
            _brightness = brightness;
            _timeout = timeoutSeconds;
            stop = effect == KbEffect.Static || brightness <= 0;
            if (!stop && _thread == null) StartLocked();
        }
        if (stop) Stop();
    }

    /// <summary>Stops the loop and waits for it, so no effect frame lands after this returns.</summary>
    public void Stop()
    {
        Thread t;
        CancellationTokenSource cts;
        lock (_gate)
        {
            t = _thread;
            cts = _cts;
            _thread = null;
            _cts = null;
        }
        if (t == null) return;
        cts.Cancel();
        if (Thread.CurrentThread != t) t.Join(2000);
        cts.Dispose();
        Log.Info("Keyboard effect stopped");
    }

    private void StartLocked()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Loop(token)) { IsBackground = true, Name = "KeyboardEffects", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        Log.Info($"Keyboard effect started: {_effect}");
    }

    private void Loop(CancellationToken token)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int lastRaw = -1;
        int lastRgb = -1;
        bool paused = false;
        long lastHeat = -10_000;

        while (!token.IsCancellationRequested)
        {
            KbEffect effect;
            KbSpeed speed;
            Color color;
            int brightness, timeout;
            lock (_gate)
            {
                effect = _effect;
                speed = _speed;
                color = _color;
                brightness = _brightness;
                timeout = _timeout;
            }

            try
            {
                // Let the EC's backlight timeout work: no writes while idle past it.
                bool idle = timeout > 0 && IdleSeconds() >= timeout;
                if (idle)
                {
                    paused = true;
                }
                else
                {
                    if (paused)
                    {
                        // Woken by a key press: force a full rewrite.
                        paused = false;
                        lastRaw = -1;
                        lastRgb = -1;
                    }

                    double t = clock.Elapsed.TotalSeconds;
                    int peak = (int)Math.Round(Math.Clamp(brightness, 0, 100) * 255 / 100.0);

                    switch (effect)
                    {
                        case KbEffect.Breathing:
                        {
                            double period = speed switch { KbSpeed.Slow => 6.0, KbSpeed.Fast => 2.0, _ => 4.0 };
                            double phase = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t / period); // 0..1..0
                            int raw = (int)Math.Round(peak * phase * phase);               // eased: longer dark
                            if (lastRgb != color.ToArgb())
                            {
                                Clevo.SetKeyboardColor(color, log: false);
                                lastRgb = color.ToArgb();
                            }
                            if (raw != lastRaw)
                            {
                                Clevo.SetKeyboardBrightnessRaw(raw);
                                lastRaw = raw;
                            }
                            CurrentColor = color;
                            break;
                        }

                        case KbEffect.ColorCycle:
                        {
                            double period = speed switch { KbSpeed.Slow => 20.0, KbSpeed.Fast => 4.0, _ => 10.0 };
                            var c = FromHue(t / period % 1.0 * 360.0);
                            if (lastRaw != peak)
                            {
                                Clevo.SetKeyboardBrightnessRaw(peak);
                                lastRaw = peak;
                            }
                            if (c.ToArgb() != lastRgb)
                            {
                                Clevo.SetKeyboardColor(c, log: false);
                                lastRgb = c.ToArgb();
                            }
                            CurrentColor = c;
                            break;
                        }

                        case KbEffect.CpuHeat:
                        {
                            if (clock.ElapsedMilliseconds - lastHeat >= 1000 || lastRgb == -1)
                            {
                                lastHeat = clock.ElapsedMilliseconds;
                                // Read here: the window's sensor poll stops when it is hidden.
                                var c = HeatFromSensors();
                                if (lastRaw != peak)
                                {
                                    Clevo.SetKeyboardBrightnessRaw(peak);
                                    lastRaw = peak;
                                }
                                if (c.ToArgb() != lastRgb)
                                {
                                    Clevo.SetKeyboardColor(c, log: false);
                                    lastRgb = c.ToArgb();
                                }
                                CurrentColor = c;
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Keyboard effect frame failed", ex);
                Thread.Sleep(1000);
            }

            token.WaitHandle.WaitOne(effect == KbEffect.CpuHeat || paused ? 250 : FrameMs);
        }
    }

    /// <summary>CPU scale: green below 45 °C, yellow around 65 °C, red from 85 °C.</summary>
    public static Color HeatColor(int temp) => HeatColor(CpuFraction(temp));

    /// <summary>0 = green, 1 = red.</summary>
    public static Color HeatColor(double fraction) => FromHue(120 * (1 - Math.Clamp(fraction, 0, 1)));

    private static double CpuFraction(int temp) => (temp - 45) / 40.0;

    /// <summary>GPU scale: green below 40 °C, red from 80 °C (the RTX 4060 Laptop throttles around 87 °C).</summary>
    private static double GpuFraction(int temp) => (temp - 40) / 40.0;

    private static bool Valid(int temp) => temp >= 20 && temp <= 125;

    /// <summary>
    /// Colour for the chosen source. Each chip is judged on its own scale, so Hottest shows whichever is closer
    /// to its limit. A GPU that is off (Eco) or reading junk falls back to the CPU.
    /// </summary>
    private Color HeatFromSensors()
    {
        int cpu = Clevo.ReadCpuFan().Temp;
        CpuTemp = cpu;
        int gpu = -1;
        if (Source != HeatSource.Cpu && GpuOn())
        {
            int t = Clevo.ReadGpuFan().Temp;
            if (Valid(t)) gpu = t;
        }

        double cpuF = Valid(cpu) ? CpuFraction(cpu) : 0;
        double gpuF = gpu > 0 ? GpuFraction(gpu) : -1;

        bool useGpu = Source switch
        {
            HeatSource.Gpu => gpu > 0,
            HeatSource.Hottest => gpu > 0 && gpuF > cpuF,
            _ => false,
        };
        HeatLabel = useGpu ? $"GPU {gpu}°C" : Valid(cpu) ? $"CPU {cpu}°C" : "";
        return HeatColor(useGpu ? gpuF : cpuF);
    }

    public static Color FromHue(double hue)
    {
        double h = (hue % 360 + 360) % 360 / 60.0;
        double x = 1 - Math.Abs(h % 2 - 1);
        (double r, double g, double b) = (int)h switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x),
        };
        return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    private static int IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return (int)((uint)Environment.TickCount - info.dwTime) / 1000;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}
