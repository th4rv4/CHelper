using System.Drawing.Drawing2D;

namespace CHelper.UI;

/// <summary>Slider that snaps to a fixed list of values, with value labels under the track.</summary>
public sealed class StepSlider : Control
{
    private int[] _values = { 60, 70, 80, 90, 100 };
    private int _index;
    private bool _dragging;

    public StepSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        BackColor = Theme.Back;
    }

    /// <summary>Raised when the user releases the thumb on a new value.</summary>
    public event Action<int> ValueCommitted;

    /// <summary>Raised while dragging, for live labels.</summary>
    public event Action<int> ValuePreview;

    public Color Accent { get; set; } = Theme.Eco;

    public bool IsDragging => _dragging;

    /// <summary>Label text for a value (default "80%").</summary>
    public Func<int, string> Format { get; set; } = v => $"{v}%";

    /// <summary>Draw a label under every Nth value only.</summary>
    public int LabelEvery { get; set; } = 1;

    public int[] Values
    {
        get => _values;
        set
        {
            if (value == null || value.Length == 0) return;
            int current = Value;
            _values = value;
            Value = current;
        }
    }

    public int Value
    {
        get => _values[_index];
        set
        {
            int best = 0;
            for (int i = 1; i < _values.Length; i++)
                if (Math.Abs(_values[i] - value) < Math.Abs(_values[best] - value)) best = i;
            _index = best;
            Invalidate();
        }
    }

    /// <summary>Extra size factor on top of DPI (the main window uses Theme.MainScale).</summary>
    public float UiScale { get; set; } = 1f;

    private int S(float v) => (int)Math.Round(v * DeviceDpi / 96f * UiScale);
    private int TrackLeft => S(22);
    private int TrackRight => Width - S(22);
    private int TrackY => S(14);

    private int XFor(int index) =>
        _values.Length == 1 ? TrackLeft : TrackLeft + (TrackRight - TrackLeft) * index / (_values.Length - 1);

    private int IndexAt(int x)
    {
        int best = 0;
        for (int i = 1; i < _values.Length; i++)
            if (Math.Abs(XFor(i) - x) < Math.Abs(XFor(best) - x)) best = i;
        return best;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!Enabled) return;
        _dragging = true;
        MoveTo(e.X);
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) MoveTo(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ValueCommitted?.Invoke(Value);
        }
        base.OnMouseUp(e);
    }

    private void MoveTo(int x)
    {
        int i = IndexAt(x);
        if (i == _index) return;
        _index = i;
        ValuePreview?.Invoke(Value);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int h = Math.Max(4, S(5));
        var track = new Rectangle(TrackLeft, TrackY - h / 2, TrackRight - TrackLeft, h);
        using (var path = Theme.RoundRect(track, h / 2))
        using (var b = new SolidBrush(Theme.Line))
            g.FillPath(b, path);

        Color accent = Enabled ? Accent : Theme.Muted;
        int x = XFor(_index);
        if (x > TrackLeft)
        {
            var fill = new Rectangle(TrackLeft, track.Y, x - TrackLeft, h);
            using var path = Theme.RoundRect(fill, h / 2);
            using var b = new SolidBrush(accent);
            g.FillPath(b, path);
        }

        int r = S(9);
        var thumb = new Rectangle(x - r, TrackY - r, r * 2, r * 2);
        using (var b = new SolidBrush(Theme.Text)) g.FillEllipse(b, thumb);
        using (var p = new Pen(accent, Math.Max(2f, S(3)))) g.DrawEllipse(p, thumb);

        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding;
        for (int i = 0; i < _values.Length; i++)
        {
            if (i % Math.Max(1, LabelEvery) != 0 && i != _values.Length - 1) continue;
            int tx = XFor(i);
            var rect = new Rectangle(tx - S(24), TrackY + S(14), S(48), S(16));
            TextRenderer.DrawText(g, Format(_values[i]), Font, rect, i == _index ? Theme.Text : Theme.Muted, flags);
        }
    }
}
