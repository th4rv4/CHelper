using System.Drawing.Drawing2D;
using CHelper.Hardware;

namespace CHelper.UI;

/// <summary>
/// Editable 8-point fan curve (G-Helper style): temperature across, fan % up.
/// Drag a point; the points on either side are pushed along so the curve never goes down.
/// </summary>
public sealed class CurveChart : Control
{
    private CurvePoint[] _points = FanCurve.Preset("Balanced", false);
    private int _drag = -1, _hover = -1;

    public CurveChart()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
    }

    public event Action Edited;

    public Color Accent { get; set; } = Theme.Standard;

    /// <summary>Live temperature marker; 0 or less hides it.</summary>
    public int NowTemp { get; set; }

    public CurvePoint[] Points
    {
        get => _points.ToArray();
        set
        {
            _points = (value ?? FanCurve.Preset("Balanced", false)).ToArray();
            Invalidate();
        }
    }

    private float Dpi => DeviceDpi / 96f;
    private int S(float v) => (int)Math.Round(v * Dpi);

    private Rectangle Plot => new(S(40), S(8), Width - S(40) - S(12), Height - S(8) - S(26));

    private PointF ToScreen(CurvePoint p)
    {
        var r = Plot;
        float x = r.Left + r.Width * (p.Temp - FanCurve.MinTemp) / (float)(FanCurve.MaxTemp - FanCurve.MinTemp);
        float y = r.Bottom - r.Height * p.Duty / 100f;
        return new PointF(x, y);
    }

    private (int Temp, int Duty) FromScreen(Point pt)
    {
        var r = Plot;
        int t = (int)Math.Round(FanCurve.MinTemp + (pt.X - r.Left) * (FanCurve.MaxTemp - FanCurve.MinTemp) / (double)r.Width);
        int d = (int)Math.Round((r.Bottom - pt.Y) * 100.0 / r.Height);
        return (Math.Clamp(t, FanCurve.MinTemp, FanCurve.MaxTemp), Math.Clamp(d, 0, 100));
    }

    private int HitTest(Point pt)
    {
        int best = -1;
        double bestDist = S(14);
        for (int i = 0; i < _points.Length; i++)
        {
            var p = ToScreen(_points[i]);
            double dist = Math.Sqrt((p.X - pt.X) * (p.X - pt.X) + (p.Y - pt.Y) * (p.Y - pt.Y));
            if (dist <= bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }
        return best;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _drag = HitTest(e.Location);
        if (_drag >= 0) Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag < 0)
        {
            int h = HitTest(e.Location);
            if (h != _hover)
            {
                _hover = h;
                Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            return;
        }

        var (t, d) = FromScreen(e.Location);
        int i = _drag;
        // Keep temperatures strictly increasing (1 °C apart).
        int minT = i == 0 ? FanCurve.MinTemp : _points[i - 1].Temp + 1;
        int maxT = i == _points.Length - 1 ? FanCurve.MaxTemp : _points[i + 1].Temp - 1;
        t = Math.Clamp(t, minT, Math.Max(minT, maxT));
        _points[i] = new CurvePoint(t, d);
        // Push neighbours so the duty never goes down from left to right.
        for (int k = i + 1; k < _points.Length; k++)
            if (_points[k].Duty < d) _points[k] = _points[k] with { Duty = d };
        for (int k = i - 1; k >= 0; k--)
            if (_points[k].Duty > d) _points[k] = _points[k] with { Duty = d };
        Invalidate();
        Edited?.Invoke();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = -1;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_drag < 0 && _hover >= 0)
        {
            _hover = -1;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = Plot;
        using var font = Theme.Px(S(11));

        using (var card = Theme.RoundRect(Rectangle.Inflate(r, S(6), S(6)), S(8)))
        using (var b = new SolidBrush(Theme.Card))
            g.FillPath(b, card);

        using (var grid = new Pen(Theme.Line, Math.Max(1, S(1))))
        {
            for (int t = FanCurve.MinTemp; t <= FanCurve.MaxTemp; t += 10)
            {
                float x = ToScreen(new CurvePoint(t, 0)).X;
                g.DrawLine(grid, x, r.Top, x, r.Bottom);
                TextRenderer.DrawText(g, $"{t}°", font, new Rectangle((int)x - S(20), r.Bottom + S(8), S(40), S(16)), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            }
            for (int d = 0; d <= 100; d += 20)
            {
                float y = ToScreen(new CurvePoint(FanCurve.MinTemp, d)).Y;
                g.DrawLine(grid, r.Left, y, r.Right, y);
                TextRenderer.DrawText(g, $"{d}%", font, new Rectangle(0, (int)y - S(8), r.Left - S(8), S(16)), Theme.Muted,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        var pts = _points.Select(ToScreen).ToArray();

        // Area under the curve, extended flat to the edges like the curve is evaluated
        using (var area = new GraphicsPath())
        {
            var first = pts[0];
            var last = pts[^1];
            area.AddLine(r.Left, r.Bottom, r.Left, first.Y);
            area.AddLines(pts);
            area.AddLine(last.X, last.Y, r.Right, last.Y);
            area.AddLine(r.Right, last.Y, r.Right, r.Bottom);
            area.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(40, Accent));
            g.FillPath(fill, area);
        }

        using (var line = new Pen(Accent, Math.Max(2f, 2.5f * Dpi)) { LineJoin = LineJoin.Round })
        {
            g.DrawLine(line, r.Left, pts[0].Y, pts[0].X, pts[0].Y);
            g.DrawLines(line, pts);
            g.DrawLine(line, pts[^1].X, pts[^1].Y, r.Right, pts[^1].Y);
        }

        // Live temperature marker
        if (NowTemp > 0)
        {
            int nowDuty = FanCurve.Evaluate(_points, NowTemp);
            var p = ToScreen(new CurvePoint(Math.Clamp(NowTemp, FanCurve.MinTemp, FanCurve.MaxTemp), nowDuty));
            using var pen = new Pen(Color.FromArgb(0x6A, 0x70, 0x7C), Math.Max(1, S(1)));
            g.DrawLine(pen, p.X, r.Top, p.X, r.Bottom);
            int m = S(5);
            using var b = new SolidBrush(Theme.Text);
            g.FillEllipse(b, p.X - m, p.Y - m, m * 2, m * 2);
        }

        float rad = S(6);
        using (var dot = new Pen(Accent, Math.Max(2f, 2.5f * Dpi)))
        using (var inner = new SolidBrush(Theme.Back))
        using (var active = new SolidBrush(Accent))
        {
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i];
                bool hot = i == _drag || i == _hover;
                float rr = hot ? rad * 1.25f : rad;
                g.FillEllipse(hot ? active : inner, p.X - rr, p.Y - rr, rr * 2, rr * 2);
                g.DrawEllipse(dot, p.X - rr, p.Y - rr, rr * 2, rr * 2);
            }
        }

        // Value tag for the point being dragged / hovered
        int tag = _drag >= 0 ? _drag : _hover;
        if (tag >= 0)
        {
            string text = $"{_points[tag].Temp}°C · {_points[tag].Duty}%";
            using var tf = Theme.Px(S(12), semibold: true);
            var size = TextRenderer.MeasureText(text, tf, Size.Empty, TextFormatFlags.NoPadding);
            var p = pts[tag];
            int w = size.Width + S(14), h = size.Height + S(8);
            int x = (int)Math.Clamp(p.X - w / 2f, r.Left, r.Right - w);
            int y = (int)(p.Y - rad - S(8) - h);
            if (y < r.Top) y = (int)(p.Y + rad + S(8));
            var box = new Rectangle(x, y, w, h);
            using (var path = Theme.RoundRect(box, S(5)))
            using (var b = new SolidBrush(Color.FromArgb(0x0A, 0x0B, 0x0E)))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, text, tf, box, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
