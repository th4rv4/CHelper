using CHelper.Hardware;

namespace CHelper.UI;

/// <summary>
/// Fan Curves window: CPU and GPU curves for each power mode (like G-Helper), presets, Reset and Apply.
/// Edits are kept per mode until Apply, which saves every mode that was changed.
/// </summary>
public sealed class FanCurveForm : Form
{
    private const int ContentWidth = 680;
    private const int Pad = 20;

    private static readonly PowerMode[] Modes =
        { PowerMode.Quiet, PowerMode.PowerSaving, PowerMode.Entertainment, PowerMode.Performance };

    private readonly Controller _c = Controller.Instance;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };

    // Working copies per mode; only the edited ones are saved on Apply.
    private readonly Dictionary<PowerMode, FanCurveSet> _work = new();
    private readonly HashSet<PowerMode> _dirty = new();
    private PowerMode _mode;

    private CurveChart _cpu, _gpu;
    private Label _cpuNow, _gpuNow, _status, _active;
    private SegRow _modes, _presets;

    public FanCurveForm()
    {
        Text = "Fan Curves";
        Icon = Theme.AppIcon;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;

        foreach (var m in Modes)
        {
            var saved = _c.CurvesFor(m);
            _work[m] = new FanCurveSet { Cpu = saved.Cpu.ToArray(), Gpu = saved.Gpu.ToArray(), Preset = saved.Preset };
        }

        BuildUi();
        ShowMode(_c.CurrentMode);
        _status.Text = Settings.Current.Fan == FanMode.Custom ? "Curves active" : "Apply turns the curves on (Fans → Custom)";

        _poll.Tick += (_, _) => Task.Run(_c.RefreshSensors);
        _c.SensorsChanged += OnSensors;
        _poll.Start();
        Task.Run(_c.RefreshSensors);
    }

    private int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        _c.SensorsChanged -= OnSensors;
        base.OnFormClosed(e);
    }

    private void BuildUi()
    {
        var fHead = Theme.Px(S(13.5f), semibold: true);
        var fBody = Theme.Px(S(12.5f));
        var fSmall = Theme.Px(S(11.5f));
        int y = 14;

        // Curves for: one tab per power mode
        Controls.Add(new Label
        {
            Text = "Curves for",
            Location = new Point(S(Pad), S(y + 8)),
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = fBody,
        });
        _modes = new SegRow(S(34), S(8), fBody, false, 16,
            new("Quiet", "moon", Theme.Eco), new("Power Saving", "leaf", Theme.Eco),
            new("Balanced", "controller", Theme.Standard), new("Turbo", "turbo", Theme.Turbo))
        {
            Location = new Point(S(Pad + 92), S(y)),
            Width = S(ContentWidth - 92),
        };
        _modes.ButtonClicked += i => ShowMode(Modes[i]);
        Controls.Add(_modes);
        y += 34 + 8;

        _active = new Label
        {
            Location = new Point(S(Pad), S(y)),
            Size = new Size(S(ContentWidth), S(18)),
            ForeColor = Theme.Muted,
            Font = fSmall,
        };
        Controls.Add(_active);
        y += 18 + 10;

        // CPU and GPU side by side
        int chartW = (ContentWidth - 20) / 2;
        (_cpu, _cpuNow) = Chart("CPU", Theme.Standard, Pad, chartW, y, fHead, fSmall);
        (_gpu, _gpuNow) = Chart("GPU", Color.FromArgb(0xFF, 0x8A, 0x3D), Pad + chartW + 20, chartW, y, fHead, fSmall);
        _cpu.Edited += OnEdited;
        _gpu.Edited += OnEdited;
        y += 26 + 240 + 14;

        Controls.Add(new Label
        {
            Text = "Preset",
            Location = new Point(S(Pad), S(y + 8)),
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = fBody,
        });
        _presets = new SegRow(S(34), S(8), fBody, false, 0,
            FanCurve.PresetNames.Select(n => new SegRow.Tile(n)).ToArray())
        {
            Location = new Point(S(Pad + 92), S(y)),
            Width = S(ContentWidth - 92),
        };
        _presets.ButtonClicked += i => LoadPreset(FanCurve.PresetNames[i]);
        Controls.Add(_presets);
        y += 34 + 12;

        Controls.Add(new Label
        {
            Text = "Drag the points. Safety: CPU fan 100% from 95°C, GPU fan 100% from 87°C, whatever the curves say.",
            Location = new Point(S(Pad), S(y)),
            Size = new Size(S(ContentWidth), S(18)),
            ForeColor = Theme.Muted,
            Font = fSmall,
        });
        y += 18 + 12;

        _status = new Label
        {
            Location = new Point(S(Pad), S(y + 8)),
            Size = new Size(S(ContentWidth - 240), S(18)),
            ForeColor = Theme.Muted,
            Font = fSmall,
        };
        Controls.Add(_status);

        var reset = new TileButton
        {
            Text = "Reset",
            Vertical = false,
            Font = fBody,
            Location = new Point(S(Pad + ContentWidth - 228), S(y)),
            Size = new Size(S(108), S(34)),
        };
        reset.Click += (_, _) => LoadPreset(FanCurveSet.DefaultPreset(_mode));
        Controls.Add(reset);

        var apply = new TileButton
        {
            Text = "Apply",
            Vertical = false,
            Font = fBody,
            Accent = Theme.Eco,
            Active = true,
            Location = new Point(S(Pad + ContentWidth - 110), S(y)),
            Size = new Size(S(110), S(34)),
        };
        apply.Click += (_, _) => Apply();
        Controls.Add(apply);
        y += 34 + 16;

        ClientSize = new Size(S(ContentWidth + Pad * 2), S(y));
    }

    private (CurveChart, Label) Chart(string title, Color accent, int x, int w, int y, Font fHead, Font fSmall)
    {
        Controls.Add(new Label
        {
            Text = title,
            Location = new Point(S(x), S(y)),
            AutoSize = true,
            Font = fHead,
            ForeColor = Theme.Text,
        });
        var now = new Label
        {
            Location = new Point(S(x + 60), S(y + 2)),
            Size = new Size(S(w - 60), S(18)),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.Muted,
            Font = fSmall,
            AutoEllipsis = true,
        };
        Controls.Add(now);
        var chart = new CurveChart
        {
            Location = new Point(S(x), S(y + 26)),
            Size = new Size(S(w), S(240)),
            Accent = accent,
        };
        Controls.Add(chart);
        return (chart, now);
    }

    private static string ModeName(PowerMode m) => m.DisplayName();

    /// <summary>Switch the editor to another mode's curves (edits to the previous mode are kept).</summary>
    private void ShowMode(PowerMode mode)
    {
        _mode = mode;
        _modes.Select(Array.IndexOf(Modes, mode));
        var set = _work[mode];
        _cpu.Points = set.Cpu;
        _gpu.Points = set.Gpu;
        SelectPreset(set.Preset);
        _active.Text = mode == _c.CurrentMode
            ? $"Editing {ModeName(mode)} · this is the active mode now"
            : $"Editing {ModeName(mode)} · active mode now: {ModeName(_c.CurrentMode)}";
    }

    private void LoadPreset(string name)
    {
        _work[_mode] = new FanCurveSet
        {
            Cpu = FanCurve.Preset(name, gpu: false),
            Gpu = FanCurve.Preset(name, gpu: true),
            Preset = name,
        };
        _dirty.Add(_mode);
        _cpu.Points = _work[_mode].Cpu;
        _gpu.Points = _work[_mode].Gpu;
        SelectPreset(name);
        _status.Text = $"{ModeName(_mode)}: {name} loaded · not applied yet";
    }

    private void OnEdited()
    {
        var set = _work[_mode];
        set.Cpu = _cpu.Points;
        set.Gpu = _gpu.Points;
        set.Preset = "Custom";
        _dirty.Add(_mode);
        SelectPreset(null);
        _status.Text = $"{ModeName(_mode)} edited · not applied yet";
    }

    private void SelectPreset(string name) =>
        _presets.Select(name == null ? -1 : Array.IndexOf(FanCurve.PresetNames, name));

    private void Apply()
    {
        // Applying always saves the mode on screen, plus any other mode that was edited.
        _dirty.Add(_mode);
        var sets = _dirty.ToDictionary(m => m, m => _work[m]);
        _dirty.Clear();
        _status.Text = "Applying…";
        Task.Run(() =>
        {
            _c.SetFanCurves(sets);
            if (!IsDisposed) BeginInvoke(new Action(() => _status.Text = "Applied · curves active"));
        });
    }

    private void OnSensors()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            var cpu = _c.CpuFan;
            var gpu = _c.GpuFan;
            bool dgpu = _c.DgpuPresent || _c.InUltimate;
            _cpu.NowTemp = cpu.Temp;
            _gpu.NowTemp = dgpu ? gpu.Temp : 0;
            _cpuNow.Text = cpu.Temp > 0 ? $"{cpu.Temp}°C · Fan {cpu.Duty}%" : "";
            _gpuNow.Text = dgpu ? $"{gpu.Temp}°C · Fan {gpu.Duty}%" : $"dGPU Off · Fan {gpu.Duty}%";
            _cpu.Invalidate();
            _gpu.Invalidate();
        }));
    }
}
