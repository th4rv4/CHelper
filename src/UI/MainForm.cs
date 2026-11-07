using System.Diagnostics;
using CHelper.Hardware;

namespace CHelper.UI;

public sealed class MainForm : Form
{
    // Layout in 96-DPI units; everything goes through S() so it scales with the display.
    private const int ContentWidth = 508;
    private const int Pad = 16;

    // UI order of the modes
    private static readonly PowerMode[] ModeOrder =
        { PowerMode.Quiet, PowerMode.PowerSaving, PowerMode.Entertainment, PowerMode.Performance };
    private static readonly GpuMode[] GpuOrder =
        { GpuMode.Eco, GpuMode.Standard, GpuMode.Optimized, GpuMode.Ultimate };
    private static readonly FanMode[] FanOrder =
        { FanMode.Automatic, FanMode.Maximum, FanMode.Custom };

    private readonly Controller _c = Controller.Instance;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };

    private Font _fBody, _fSmall, _fHead, _fBig;
    private Label _stats, _modeHint, _gpuHint, _fanHint, _screenHint, _kbHint, _chargeHint;
    private Label _batCharge, _batRateLabel, _batRate, _batHealth;
    private StepSlider _charge;
    private SegRow _modes, _gpu, _fans, _screen, _options, _kbEffects, _kbSpeed;
    private TileButton _kbTimeoutAC, _kbTimeoutDC;
    private readonly ToolTip _tips = new() { InitialDelay = 400 };
    private FanCurveForm _curves;

    private static readonly KbEffect[] EffectOrder = { KbEffect.Static, KbEffect.Breathing, KbEffect.ColorCycle, KbEffect.CpuHeat };
    private static readonly KbSpeed[] SpeedOrder = { KbSpeed.Slow, KbSpeed.Normal, KbSpeed.Fast };
    private static readonly int[] TimeoutSteps = { 0, 15, 30, 60, 300 };
    private static readonly Color[] Rainbow =
        { Color.Red, Color.Yellow, Color.Lime, Color.Cyan, Color.Blue, Color.Magenta, Color.Red };
    private static readonly Color[] Heat =
        { KeyboardEffects.HeatColor(45), KeyboardEffects.HeatColor(65), KeyboardEffects.HeatColor(85) };
    private StepSlider _kbSlider;
    private TileButton _kbColor;
    private int[] _screenValues = Array.Empty<int>();
    private int _y;
    private bool _startupEnabled;

    public event Action QuitRequested;

    public MainForm()
    {
        Text = "C-Helper";
        Icon = Theme.AppIcon;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;

        BuildUi();
        ApplyScale(this);

        _c.StateChanged += OnStateChanged;
        _c.SensorsChanged += () =>
        {
            if (IsHandleCreated && !IsDisposed && Visible) BeginInvoke(new Action(UpdateStats));
        };
        _poll.Tick += (_, _) =>
        {
            if (!Visible) return;
            Task.Run(() =>
            {
                _c.RefreshSensors();
                _c.RefreshDgpuPresence();
            });
        };
        _poll.Start();
        Task.Run(() =>
        {
            _startupEnabled = Startup.IsEnabled();
            OnStateChanged();
        });
    }

    private int S(float v) => (int)Math.Round(v * DeviceDpi / 96f * Theme.MainScale);

    /// <summary>Hands the main window's size factor to the custom-drawn controls (tiles, sliders).</summary>
    private static void ApplyScale(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is TileButton t) t.UiScale = Theme.MainScale;
            else if (c is StepSlider s) s.UiScale = Theme.MainScale;
            ApplyScale(c);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // Moved to a monitor with a different scale: rebuild at the new size.
        SuspendLayout();
        var old = Controls.Cast<Control>().ToList();
        Controls.Clear();
        old.ForEach(c => c.Dispose());
        BuildUi();
        ApplyScale(this);
        ResumeLayout();
        RefreshState();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) return;
        RefreshState();
        Task.Run(() => _c.RefreshSensors());
    }

    // ---------------- layout ----------------

    private void BuildUi()
    {
        _fBody = Theme.Px(S(12.5f));
        _fSmall = Theme.Px(S(11.5f));
        _fHead = Theme.Px(S(13.5f), semibold: true);
        _fBig = Theme.Px(S(16f), semibold: true);
        Font = _fBody;

        _y = 12;
        _stats = new Label
        {
            Location = new Point(S(Pad), S(_y)),
            Size = new Size(S(ContentWidth), S(18)),
            ForeColor = Theme.Muted,
            Font = _fSmall,
        };
        Controls.Add(_stats);
        _y += 28;

        _modeHint = Section("gauge", "Power Mode");
        _modes = Row(66, true, 26,
            new("Quiet", "moon", Theme.Eco),
            new("Power Saving", "leaf", Theme.Eco),
            new("Entertainment", "controller", Theme.Standard),
            new("Performance", "turbo", Theme.Turbo));
        _modes.ButtonClicked += i => Run(() => _c.SetPowerMode(ModeOrder[i]));

        _gpuHint = Section("gpu", "GPU Mode");
        _gpu = Row(66, true, 26,
            new("Eco", "eco", Theme.Eco),
            new("Standard", "standard", Theme.Standard),
            new("Optimized", "optimized", Theme.Optimized),
            new("dGPU Only", "ultimate", Theme.Turbo));
        _gpu.ButtonClicked += i => OnGpuClicked(GpuOrder[i]);

        _fanHint = Section("fan", "Fans");
        _fans = Row(40, true, 0,
            new("Automatic"), new("Maximum", null, Theme.Turbo), new("Custom"), new("Edit Curves"));
        _fans.ButtonClicked += i =>
        {
            if (i == FanOrder.Length) OpenCurves();
            else Run(() => _c.SetFanMode(FanOrder[i]));
        };

        _screenHint = Section("screen", "Laptop Screen");
        BuildScreenRow();

        _kbHint = Section("keyboard", "Keyboard Lighting");
        _kbEffects = Row(38, false, 16,
            new("Static", "fx-static"), new("Breathing", "fx-breathing"),
            new("Color Cycle", "fx-cycle"), new("CPU Heat", "fx-heat"));
        _kbEffects.ButtonClicked += i => Run(() => _c.SetKeyboardEffect(EffectOrder[i]));
        _y -= 4;

        _kbColor = new TileButton
        {
            Location = new Point(S(Pad), S(_y)),
            Size = new Size(S(52), S(40)),
            Font = _fBody,
        };
        _kbColor.Click += (_, _) => PickColor();
        Controls.Add(_kbColor);
        _kbSlider = new StepSlider
        {
            Location = new Point(S(Pad + 60), S(_y - 2)),
            Size = new Size(S(ContentWidth - 60), S(44)),
            Font = _fSmall,
            Accent = Theme.Standard,
            Values = Enumerable.Range(0, 21).Select(i => i * 5).ToArray(),
            LabelEvery = 5,
            Format = v => v == 0 ? "Off" : $"{v}%",
        };
        _kbSlider.ValuePreview += v =>
        {
            _kbHint.Text = v == 0 ? "Off" : $"Brightness {v}%";
            _c.PreviewKeyboardBrightness(v);
        };
        _kbSlider.ValueCommitted += v => Run(() => _c.SetKeyboardBrightness(v));
        Controls.Add(_kbSlider);
        _y += 40 + 10;

        // Speed (animated effects) and the backlight timer (plugged in / on battery) on one line
        const int timeoutW = 224;
        Controls.Add(new Label
        {
            Text = "Speed",
            Location = new Point(S(Pad), S(_y + 7)),
            Size = new Size(S(50), S(18)),
            ForeColor = Theme.Muted,
            Font = _fSmall,
        });
        _kbSpeed = new SegRow(S(32), S(6), _fBody, false, 0, new("Slow"), new("Normal"), new("Fast"))
        {
            Location = new Point(S(Pad + 52), S(_y)),
            Width = S(ContentWidth - 52 - timeoutW - 12),
        };
        _kbSpeed.ButtonClicked += i => Run(() => _c.SetKeyboardSpeed(SpeedOrder[i]));
        Controls.Add(_kbSpeed);
        _kbTimeoutAC = TimeoutButton("plugged", Pad + ContentWidth - timeoutW, pluggedIn: true);
        _kbTimeoutDC = TimeoutButton("battery-saver", Pad + ContentWidth - timeoutW + 116, pluggedIn: false);
        _y += 32 + 12;

        Section("settings", "Options");
        _options = Row(46, false, 18,
            new("Camera", "camera"), new("Auto Mode", "automation"), new("Startup", "startup"), new("Fn+Esc", "keyboard"));
        _options.ButtonClicked += OnOptionClicked;

        _chargeHint = Section("battery-saver", "Battery Charge Limit");
        _charge = new StepSlider
        {
            Location = new Point(S(Pad), S(_y - 4)),
            Size = new Size(S(ContentWidth), S(46)),
            Font = _fSmall,
            Enabled = false,
        };
        _charge.ValuePreview += v => _chargeHint.Text = ChargeText(v, null);
        _charge.ValueCommitted += v => Run(() => _c.SetChargeLimit(v));
        Controls.Add(_charge);
        _y += 46 + 6;

        // Charge · Discharging/Charging W · Health (G-Helper style readouts)
        Controls.Add(new Panel
        {
            Location = new Point(S(Pad), S(_y)),
            Size = new Size(S(ContentWidth), Math.Max(1, S(1))),
            BackColor = Theme.Line,
        });
        _y += 10;
        int colW = ContentWidth / 3;
        (_, _batCharge) = Readout(0, colW, "Charge");
        (_batRateLabel, _batRate) = Readout(1, colW, "Battery");
        (_, _batHealth) = Readout(2, colW, "Health");
        _y += 46;

        // Footer
        Controls.Add(new Panel
        {
            Location = new Point(0, S(_y)),
            Size = new Size(S(ContentWidth + Pad * 2), Math.Max(1, S(1))),
            BackColor = Theme.Line,
        });
        _y += 10;

        Controls.Add(new Label
        {
            Text = $"v{Application.ProductVersion.Split('+')[0]}",
            Location = new Point(S(Pad), S(_y + 9)),
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = _fSmall,
        });

        var log = new TileButton
        {
            Text = "Log",
            IconName = "log",
            IconSize = 14,
            Vertical = false,
            Borderless = true,
            Font = _fSmall,
            Location = new Point(S(Pad + 60), S(_y + 2)),
            Size = new Size(S(64), S(30)),
        };
        log.Click += (_, _) => OpenLog();
        Controls.Add(log);

        var quit = new TileButton
        {
            Text = "Quit",
            IconName = "quit",
            IconSize = 16,
            Vertical = false,
            Font = _fBody,
            Location = new Point(S(Pad + ContentWidth - 96), S(_y)),
            Size = new Size(S(96), S(34)),
        };
        quit.Click += (_, _) => QuitRequested?.Invoke();
        Controls.Add(quit);
        _y += 34 + 12;

        ClientSize = new Size(S(ContentWidth + Pad * 2), S(_y));
    }

    private string FanHint()
    {
        var s = Settings.Current;
        if (s.Fan == FanMode.Custom)
            return _c.CurveCpuDuty >= 0 ? $"Custom Curves · CPU {_c.CpuFan.Temp}°C → {_c.CurveCpuDuty}%" : "Custom Curves";
        if (s.Fan == FanMode.Maximum) return "Overrides The Power Mode";
        return _c.CurrentMode == PowerMode.Quiet ? "Quiet Curve" : "Follows Power Mode";
    }

    private string KeyboardHint()
    {
        var s = Settings.Current;
        if (s.KeyboardBrightness == 0) return "Off";
        string speed = s.KeyboardSpeed.ToString();
        return s.KeyboardEffect switch
        {
            KbEffect.Breathing => $"Breathing · {speed} · {s.KeyboardBrightness}%",
            KbEffect.ColorCycle => $"Color Cycle · {speed} · {s.KeyboardBrightness}%",
            KbEffect.CpuHeat => _c.CpuFan.Temp > 0 ? $"CPU Heat · {_c.CpuFan.Temp}°C" : "CPU Heat",
            _ => $"Brightness {s.KeyboardBrightness}%",
        };
    }

    private static string TimeoutText(int seconds) => seconds >= 60 ? $"{seconds / 60} min" : $"{seconds} s";

    private TileButton TimeoutButton(string icon, int x, bool pluggedIn)
    {
        var b = new TileButton
        {
            Vertical = false,
            IconName = icon,
            IconSize = 16,
            Font = _fBody,
            Location = new Point(S(x), S(_y)),
            Size = new Size(S(108), S(32)),
        };
        b.Click += (_, _) => CycleTimeout(pluggedIn);
        _tips.SetToolTip(b, pluggedIn
            ? "Keyboard light turns off after this long idle, while plugged in. Click to change."
            : "Keyboard light turns off after this long idle, on battery. Click to change.");
        Controls.Add(b);
        return b;
    }

    private void CycleTimeout(bool pluggedIn)
    {
        var s = Settings.Current;
        int current = pluggedIn ? s.KeyboardTimeoutAC : s.KeyboardTimeoutDC;
        int i = Array.IndexOf(TimeoutSteps, current);
        int next = TimeoutSteps[(i + 1) % TimeoutSteps.Length];
        Run(() => _c.SetKeyboardTimeout(pluggedIn, next));
    }

    private void OpenCurves()
    {
        if (_curves == null || _curves.IsDisposed)
        {
            _curves = new FanCurveForm();
            var wa = Screen.FromControl(this).WorkingArea;
            _curves.Location = new Point(
                Math.Max(wa.Left, Left - _curves.Width - S(12)),
                Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - _curves.Height)));
            _curves.Show(this);
        }
        else
        {
            _curves.Activate();
        }
    }

    private (Label Caption, Label Value) Readout(int col, int colW, string caption)
    {
        var cap = new Label
        {
            Text = caption,
            Location = new Point(S(Pad + col * colW), S(_y)),
            Size = new Size(S(colW - 8), S(16)),
            ForeColor = Theme.Muted,
            Font = _fSmall,
        };
        var val = new Label
        {
            Text = "—",
            Location = new Point(S(Pad + col * colW), S(_y + 17)),
            Size = new Size(S(colW - 8), S(24)),
            ForeColor = Theme.Text,
            Font = _fBig,
        };
        Controls.Add(cap);
        Controls.Add(val);
        return (cap, val);
    }

    private Label Section(string icon, string title)
    {
        Controls.Add(new PictureBox
        {
            Image = Icons.Get(icon, Theme.Text),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(S(Pad), S(_y + 2)),
            Size = new Size(S(20), S(20)),
        });
        Controls.Add(new Label
        {
            Text = title,
            Location = new Point(S(Pad + 28), S(_y + 2)),
            AutoSize = true,
            Font = _fHead,
            ForeColor = Theme.Text,
        });
        var hint = new Label
        {
            Location = new Point(S(Pad + 200), S(_y + 4)),
            Size = new Size(S(ContentWidth - 200), S(18)),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.Muted,
            Font = _fSmall,
            AutoEllipsis = true,
        };
        Controls.Add(hint);
        _y += 28;
        return hint;
    }

    private SegRow Row(int height, bool vertical, int iconSize, params SegRow.Tile[] items)
    {
        var row = new SegRow(S(height), S(6), _fBody, vertical, iconSize, items)
        {
            Location = new Point(S(Pad), S(_y)),
            Width = S(ContentWidth),
        };
        Controls.Add(row);
        _y += height + 12;
        return row;
    }

    private void BuildScreenRow()
    {
        int[] rates = Native.Display.AvailableRates();
        Log.Info($"Panel refresh rates: {string.Join(", ", rates)}");
        // Every rate the panel offers (up to 4, keeping the highest), then Auto
        var values = rates.Skip(Math.Max(0, rates.Length - 4)).ToList();
        values.Add(0);
        _screenValues = values.ToArray();

        var items = _screenValues.Select(v => new SegRow.Tile(v == 0 ? "Auto" : $"{v} Hz")).ToArray();
        _screen = Row(40, true, 0, items);
        _screen.ButtonClicked += i => Run(() => _c.SetScreenRate(_screenValues[i]));
    }

    // ---------------- actions ----------------

    private static void Run(Action action) => Task.Run(() =>
    {
        try { action(); }
        catch (Exception ex) { Log.Error("action failed", ex); }
    });

    private void OnGpuClicked(GpuMode mode)
    {
        var s = Settings.Current;
        if (mode == s.Gpu) return;

        bool needsRestart = mode == GpuMode.Ultimate || _c.InUltimate;
        if (needsRestart)
        {
            string msg = mode == GpuMode.Ultimate
                ? "dGPU Only makes the NVIDIA GPU drive the screen directly (best FPS, more battery drain).\n\nIt needs a restart. Switch now?"
                : "Leaving dGPU Only needs a restart. Switch now?";
            if (MessageBox.Show(this, msg, "C-Helper", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
        }
        else if (mode == GpuMode.Eco || mode == GpuMode.Optimized)
        {
            _gpuHint.Text = "Close Games Before Eco · Switching…";
        }

        Task.Run(() =>
        {
            bool restart = _c.SetGpuMode(mode);
            if (!restart) return;
            BeginInvoke(new Action(() =>
            {
                if (MessageBox.Show(this, "Restart now to apply the GPU mode?", "C-Helper",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
            }));
        });
    }

    private void OnOptionClicked(int index)
    {
        var s = Settings.Current;
        switch (index)
        {
            case 0:
                Run(() => _c.SetCamera(!s.Camera));
                break;
            case 1:
                Run(() => _c.SetAutoPowerMode(!s.AutoPowerMode));
                break;
            case 2:
                Run(() =>
                {
                    if (_startupEnabled) Startup.Disable();
                    else Startup.Enable();
                    _startupEnabled = Startup.IsEnabled();
                    OnStateChanged();
                });
                break;
            case 3:
                Run(() => _c.SetFnEsc(!s.FnEscOpensHelper));
                break;
        }
    }

    private void PickColor()
    {
        using var dlg = new ColorDialog
        {
            Color = Color.FromArgb(Settings.Current.KeyboardColor),
            FullOpen = true,
            AnyColor = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            var c = dlg.Color;
            Run(() => _c.SetKeyboardColor(c));
        }
    }

    private static void OpenLog()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CHelper", "log.txt");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ---------------- state ----------------

    private void OnStateChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(new Action(RefreshState));
    }

    private static string ModeName(PowerMode m) => m == PowerMode.PowerSaving ? "Power Saving" : m.ToString();

    private void RefreshState()
    {
        var s = Settings.Current;

        // Power mode
        _modes.Select(Array.IndexOf(ModeOrder, _c.CurrentMode));

        // GPU
        bool ultimateOk = Gpu.MuxSupports(_c.MuxMask, Gpu.MuxDgpuOnly);
        for (int i = 0; i < _gpu.Count; i++)
        {
            bool enabled = !_c.GpuBusy;
            if (GpuOrder[i] == GpuMode.Ultimate) enabled &= ultimateOk;
            if (GpuOrder[i] is GpuMode.Eco or GpuMode.Optimized) enabled &= !_c.InUltimate;
            _gpu.SetEnabled(i, enabled);
        }
        _gpu.Select(Array.IndexOf(GpuOrder, s.Gpu));

        // Fans
        _fans.Select(Array.IndexOf(FanOrder, s.Fan));
        _fanHint.Text = FanHint();

        // Screen
        _screen.Select(Array.IndexOf(_screenValues, s.ScreenRate));
        string now = _c.ScreenRate > 0 ? $"{_c.ScreenRate} Hz Now" : "";
        _screenHint.Text = s.ScreenRate == 0 ? $"{now} · Max On AC, Min On Battery" : now;

        // Keyboard
        _kbEffects.Select(Array.IndexOf(EffectOrder, s.KeyboardEffect));
        _kbColor.Fill = Color.FromArgb(s.KeyboardColor);
        _kbColor.Gradient = s.KeyboardEffect switch
        {
            KbEffect.ColorCycle => Rainbow,
            KbEffect.CpuHeat => Heat,
            _ => null,
        };
        if (!_kbSlider.IsDragging) _kbSlider.Value = s.KeyboardBrightness;
        bool animated = s.KeyboardEffect is KbEffect.Breathing or KbEffect.ColorCycle;
        for (int i = 0; i < _kbSpeed.Count; i++) _kbSpeed.SetEnabled(i, animated);
        _kbSpeed.Select(animated ? Array.IndexOf(SpeedOrder, s.KeyboardSpeed) : -1);
        _kbTimeoutAC.Text = s.KeyboardTimeoutAC <= 0 ? "Never" : TimeoutText(s.KeyboardTimeoutAC);
        _kbTimeoutDC.Text = s.KeyboardTimeoutDC <= 0 ? "Never" : TimeoutText(s.KeyboardTimeoutDC);
        _kbHint.Text = KeyboardHint();

        // Options
        _options.SetOn(0, s.Camera);
        _options.SetOn(1, s.AutoPowerMode);
        _options.SetOn(2, _startupEnabled);
        _options.SetOn(3, s.FnEscOpensHelper);

        // Battery charge limit
        var charge = _c.Charge;
        if (charge != null && !_charge.IsDragging)
        {
            _charge.Values = Charger.SliderStops(charge);
            _charge.Value = charge.Limit;
            _charge.Enabled = true;
            _chargeHint.Text = ChargeText(charge.Limit, charge);
        }

        UpdateStats();
    }

    private static string ChargeText(int stop, Charger.State state)
    {
        if (stop >= 100) return "No Limit · Charges To 100%";
        return state != null && state.Enabled && state.Stop == stop
            ? $"Stop At {stop}% · Start Below {state.Start}%"
            : $"Stop At {stop}%";
    }

    /// <summary>Live readouts, G-Helper style: CPU/GPU temperature and fan in the section headers.</summary>
    private void UpdateStats()
    {
        var s = Settings.Current;
        var ps = SystemInformation.PowerStatus;
        string src = ps.PowerLineStatus == PowerLineStatus.Offline ? "On Battery" : "Plugged In";
        string auto = s.AutoPowerMode ? $" · Auto: AC {ModeName(s.ModeAC)}, Battery {ModeName(s.ModeDC)}" : "";
        _stats.Text = $"{src}{auto}";

        // Battery readouts
        var bat = _c.BatteryNow;
        int pct = bat?.Percent ?? (ps.BatteryLifePercent is >= 0 and <= 1 ? (int)Math.Round(ps.BatteryLifePercent * 100) : -1);
        _batCharge.Text = pct >= 0 ? $"{pct}%" : "—";
        if (bat is { } b && b.Watts < 0)
        {
            _batRateLabel.Text = "Discharging";
            _batRate.Text = $"{-b.Watts:0.0} W";
            _batRate.ForeColor = Theme.Warn;
        }
        else if (bat is { } c && c.Watts > 0)
        {
            _batRateLabel.Text = "Charging";
            _batRate.Text = $"{c.Watts:0.0} W";
            _batRate.ForeColor = Theme.Accent;
        }
        else
        {
            _batRateLabel.Text = "Battery";
            _batRate.Text = ps.PowerLineStatus == PowerLineStatus.Offline ? "On Battery" : "Plugged In";
            _batRate.ForeColor = Theme.Text;
        }
        _batHealth.Text = _c.BatteryHealth > 0 ? $"{_c.BatteryHealth:0.0}%" : "—";

        var cpu = _c.CpuFan;
        _modeHint.Text = cpu.Temp > 0 ? $"CPU {cpu.Temp}°C · Fan {cpu.Duty}%" : "";
        _fanHint.Text = FanHint();
        if (s.KeyboardEffect == KbEffect.CpuHeat) _kbHint.Text = KeyboardHint();

        var gpu = _c.GpuFan;
        _gpuHint.Text = _c.GpuBusy ? "Switching…"
            : !_c.DgpuPresent && !_c.InUltimate ? $"dGPU Off · Fan {gpu.Duty}%"
            : gpu.Temp > 0 ? $"GPU {gpu.Temp}°C · Fan {gpu.Duty}%"
            : _c.InUltimate ? "dGPU Only" : "dGPU On";
    }

    public void ShowNearTray()
    {
        if (!Visible)
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Right - Width - S(12), Math.Max(wa.Top, wa.Bottom - Height - S(12)));
            Show();
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        ForceForeground();
    }

    /// <summary>
    /// Windows blocks background apps from taking focus (the taskbar icon just flashes).
    /// Attaching to the current foreground thread's input lets SetForegroundWindow succeed.
    /// </summary>
    private void ForceForeground()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == Handle) return;
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint myThread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
        try
        {
            BringWindowToTop(Handle);
            SetForegroundWindow(Handle);
        }
        finally
        {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }
        if (GetForegroundWindow() != Handle)
        {
            // Last resort: a quick TopMost flip brings it above other windows.
            TopMost = true;
            TopMost = false;
        }
        Activate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
