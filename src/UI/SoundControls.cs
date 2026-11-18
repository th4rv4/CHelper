using System.Drawing.Drawing2D;

namespace CHelper.UI;

/// <summary>On/off switch, custom-drawn to match the rest of C-Helper.</summary>
public sealed class ToggleSwitch : Control
{
    private bool _on;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
    }

    /// <summary>Raised when the user flips the switch.</summary>
    public event Action<bool> Toggled;

    public bool On { get => _on; set { if (_on != value) { _on = value; Invalidate(); } } }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Enabled) return;
        On = !On;
        Toggled?.Invoke(On);
    }

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        Color back = !Enabled ? Theme.Line : _on ? Theme.Eco : Theme.LineHover;
        using (var path = Theme.RoundRect(track, track.Height / 2))
        using (var b = new SolidBrush(back))
            g.FillPath(b, path);
        int pad = Math.Max(2, Height / 7);
        int d = Height - pad * 2 - 1;
        int x = _on ? Width - pad - d - 1 : pad;
        using var knob = new SolidBrush(Enabled ? Theme.Text : Theme.Muted);
        g.FillEllipse(knob, x, pad, d, d);
    }
}

/// <summary>Plain level slider (no labels): drag or click to set a value between Minimum and Maximum.</summary>
public sealed class LevelSlider : Control
{
    private int _value, _min, _max = 100;
    private bool _dragging;

    public LevelSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
    }

    /// <summary>Raised while dragging, for live labels.</summary>
    public event Action<int> ValuePreview;

    /// <summary>Raised when the user lets go on a new value.</summary>
    public event Action<int> ValueCommitted;

    public Color Accent { get; set; } = Theme.Eco;
    public int Step { get; set; } = 1;
    public bool IsDragging => _dragging;

    public int Minimum { get => _min; set { _min = value; Value = _value; } }
    public int Maximum { get => _max; set { _max = value; Value = _value; } }

    public int Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(value, _min, Math.Max(_min, _max));
            if (_value != value) { _value = value; Invalidate(); }
        }
    }

    private int R => Math.Max(5, Height / 3);
    private int Left0 => R + 1;
    private int Right0 => Width - R - 2;

    private int XFor(int v) => _max <= _min ? Left0 : Left0 + (Right0 - Left0) * (v - _min) / (_max - _min);

    private int ValueAt(int x)
    {
        if (Right0 <= Left0) return _min;
        double t = Math.Clamp((x - Left0) / (double)(Right0 - Left0), 0, 1);
        int v = _min + (int)Math.Round(t * (_max - _min) / Step) * Step;
        return Math.Clamp(v, _min, _max);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        MoveTo(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) MoveTo(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        ValueCommitted?.Invoke(_value);
    }

    private void MoveTo(int x)
    {
        int v = ValueAt(x);
        if (v == _value) return;
        Value = v;
        ValuePreview?.Invoke(v);
    }

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cy = Height / 2;
        int h = Math.Max(4, Height / 6);
        var track = new Rectangle(Left0, cy - h / 2, Right0 - Left0, h);
        using (var path = Theme.RoundRect(track, h / 2))
        using (var b = new SolidBrush(Theme.Line))
            g.FillPath(b, path);

        Color accent = Enabled ? Accent : Theme.Muted;
        int x = XFor(_value);
        if (x > Left0 + 1)
        {
            using var path = Theme.RoundRect(new Rectangle(Left0, track.Y, x - Left0, h), h / 2);
            using var b = new SolidBrush(accent);
            g.FillPath(b, path);
        }
        int r = R;
        var thumb = new Rectangle(x - r, cy - r, r * 2, r * 2);
        using (var b = new SolidBrush(Enabled ? Theme.Text : Theme.Muted)) g.FillEllipse(b, thumb);
        using (var p = new Pen(accent, Math.Max(2f, r / 3f))) g.DrawEllipse(p, thumb);
    }
}

/// <summary>Ten-band equalizer graph: drag a point up or down to change that band (−12 to +12 dB).</summary>
public sealed class EqGraph : Control
{
    private static readonly string[] Labels = { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };
    private const float Range = 12f;
    private readonly float[] _bands = new float[10];
    private int _drag = -1;

    public EqGraph()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
    }

    /// <summary>Raised when the user lets go of a point; carries all ten bands.</summary>
    public event Action<float[]> BandsCommitted;

    public bool IsDragging => _drag >= 0;

    /// <summary>Extra size factor on top of DPI (the Sound panel uses Theme.MainScale).</summary>
    public float UiScale { get; set; } = 1f;

    public float[] Bands
    {
        get => (float[])_bands.Clone();
        set
        {
            if (value == null || _drag >= 0) return;
            for (int i = 0; i < 10 && i < value.Length; i++) _bands[i] = value[i];
            Invalidate();
        }
    }

    private int S(float v) => (int)Math.Round(v * DeviceDpi / 96f * UiScale);
    private Rectangle Plot => new(S(34), S(12), Width - S(34) - S(18), Height - S(12) - S(30));

    private int XFor(int i) => Plot.Left + Plot.Width * i / 9;
    private int YFor(float db) => Plot.Top + (int)Math.Round((Range - db) / (2 * Range) * Plot.Height);
    private float DbAt(int y) => (float)Math.Round(Math.Clamp(Range - (y - Plot.Top) / (float)Plot.Height * 2 * Range, -Range, Range) * 2) / 2f;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        int best = 0;
        for (int i = 1; i < 10; i++)
            if (Math.Abs(XFor(i) - e.X) < Math.Abs(XFor(best) - e.X)) best = i;
        if (Math.Abs(XFor(best) - e.X) > Plot.Width / 18 + S(4)) return;
        _drag = best;
        Capture = true;
        MoveTo(e.Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag >= 0) MoveTo(e.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag < 0) return;
        _drag = -1;
        Capture = false;
        BandsCommitted?.Invoke(Bands);
    }

    private void MoveTo(int y)
    {
        float db = DbAt(y);
        if (Math.Abs(_bands[_drag] - db) < 0.01f) return;
        _bands[_drag] = db;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), S(6)))
        {
            using var b = new SolidBrush(Theme.Card);
            g.FillPath(b, path);
            using var p = new Pen(Theme.Line);
            g.DrawPath(p, path);
        }

        var plot = Plot;
        using var font = Theme.Px(S(10));
        const TextFormatFlags right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        foreach (var (db, text) in new[] { (Range, "+12"), (0f, "0"), (-Range, "−12") })
            TextRenderer.DrawText(g, text, font, new Rectangle(0, YFor(db) - S(8), plot.Left - S(8), S(16)), Theme.Muted, right);

        using (var zero = new Pen(Theme.LineHover) { DashStyle = DashStyle.Dot })
            g.DrawLine(zero, plot.Left, YFor(0), plot.Right, YFor(0));

        var pts = new Point[10];
        using (var track = new Pen(Theme.Line, Math.Max(2f, S(3))))
        {
            for (int i = 0; i < 10; i++)
            {
                int x = XFor(i);
                g.DrawLine(track, x, plot.Top, x, plot.Bottom);
                pts[i] = new Point(x, YFor(_bands[i]));
                TextRenderer.DrawText(g, Labels[i], font, new Rectangle(x - S(20), plot.Bottom + S(8), S(40), S(16)),
                    Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            }
        }

        Color accent = Enabled ? Theme.Eco : Theme.Muted;
        using (var line = new Pen(accent, Math.Max(2f, S(2)))) g.DrawLines(line, pts);
        int r = S(6);
        using var fill = new SolidBrush(Theme.Text);
        using var ring = new Pen(accent, Math.Max(2f, S(2)));
        foreach (var p in pts)
        {
            g.FillEllipse(fill, p.X - r, p.Y - r, r * 2, r * 2);
            g.DrawEllipse(ring, p.X - r, p.Y - r, r * 2, r * 2);
        }
    }
}

/// <summary>Dark colours for drop-down menus.</summary>
public sealed class DarkMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Card;
    public override Color MenuBorder => Theme.Line;
    public override Color MenuItemBorder => Theme.LineHover;
    public override Color MenuItemSelected => Theme.CardHover;
    public override Color ImageMarginGradientBegin => Theme.Card;
    public override Color ImageMarginGradientMiddle => Theme.Card;
    public override Color ImageMarginGradientEnd => Theme.Card;
    public override Color SeparatorDark => Theme.Line;
    public override Color SeparatorLight => Theme.Card;
    public override Color CheckBackground => Theme.CardPressed;
    public override Color CheckSelectedBackground => Theme.CardPressed;
    public override Color CheckPressedBackground => Theme.CardPressed;
}
