using System.Drawing.Drawing2D;

namespace CHelper.UI;

/// <summary>
/// G-Helper style on-screen popup: dark rounded box, big white icon, large bold text.
/// Never takes focus; fades out by itself.
/// </summary>
public sealed class OsdForm : Form
{
    private static readonly Color Back = Color.FromArgb(10, 11, 14);
    private const double MaxOpacity = 0.92;

    private readonly System.Windows.Forms.Timer _hide = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 25 };

    private string _icon = "keyboard", _text = "";
    private int? _percent;
    private Color _color = Theme.Standard;
    private Font _fText, _fBarText, _fPct;
    private int _fontDpi;

    public OsdForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Back;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;

        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            _fade.Start();
        };
        _fade.Tick += (_, _) =>
        {
            Opacity = Math.Max(0, Opacity - 0.08);
            if (Opacity <= 0)
            {
                _fade.Stop();
                Hide();
            }
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x80 /* WS_EX_TOOLWINDOW */ | 0x8 /* WS_EX_TOPMOST */
                          | 0x20 /* WS_EX_TRANSPARENT: clicks go through */;
            return cp;
        }
    }

    private float MonitorScale(Screen screen)
    {
        // The popup can appear on any monitor; use that monitor's scale.
        try
        {
            var mon = MonitorFromPoint(new POINT { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 }, 2);
            if (GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0) return dpi / 96f;
        }
        catch { /* fall back below */ }
        return DeviceDpi / 96f;
    }

    private float _scale = 1f;
    private int S(float v) => (int)Math.Round(v * _scale);

    /// <param name="icon">Embedded icon name (drawn white).</param>
    /// <param name="text">One line, e.g. "Caps Lock On".</param>
    /// <param name="percent">Shows a progress bar with the value when set.</param>
    /// <param name="color">Bar colour.</param>
    public void ShowOsd(string icon, string text, int? percent = null, Color? color = null)
    {
        _icon = icon;
        _text = text;
        _percent = percent;
        _color = color ?? Theme.Standard;

        var screen = Screen.FromPoint(Cursor.Position);
        _scale = MonitorScale(screen);
        int dpi = (int)(_scale * 96);
        if (dpi != _fontDpi)
        {
            _fText?.Dispose();
            _fBarText?.Dispose();
            _fPct?.Dispose();
            _fText = new Font("Segoe UI", S(19), FontStyle.Bold, GraphicsUnit.Pixel);
            _fBarText = new Font("Segoe UI", S(16), FontStyle.Bold, GraphicsUnit.Pixel);
            _fPct = new Font("Segoe UI Semibold", S(13), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontDpi = dpi;
        }

        // 260 x 64, growing only for long text.
        int width = Math.Max(S(260), ContentWidth() + S(48));
        width = Math.Min(width, screen.WorkingArea.Width - S(40));
        var size = new Size(width, S(64));

        var wa = screen.WorkingArea;
        Bounds = new Rectangle(wa.Left + (wa.Width - size.Width) / 2, wa.Bottom - size.Height - S(80), size.Width, size.Height);
        var old = Region;
        using (var path = Theme.RoundRect(new Rectangle(0, 0, size.Width, size.Height), S(14)))
            Region = new Region(path);
        old?.Dispose();

        _fade.Stop();
        Opacity = MaxOpacity;
        if (!Visible) Show();
        Invalidate();
        _hide.Stop();
        _hide.Start();
    }

    private const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                                          | TextFormatFlags.NoPadding;

    private int IconSize => S(30);
    private int IconGap => S(12);
    private int BarWidth => S(130);

    private string PctText => $"{Math.Clamp(_percent ?? 0, 0, 100)}%";

    /// <summary>Width of icon + text (or icon + title/bar/percentage), so the block can be centred.</summary>
    private int ContentWidth()
    {
        if (!_percent.HasValue)
            return IconSize + IconGap + TextRenderer.MeasureText(_text, _fText, Size.Empty, Flags).Width;
        int title = TextRenderer.MeasureText(_text, _fBarText, Size.Empty, Flags).Width;
        int bar = BarWidth + S(10) + TextRenderer.MeasureText("100%", _fPct, Size.Empty, Flags).Width;
        return IconSize + IconGap + Math.Max(title, bar);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Back);

        int x = Math.Max(S(16), (Width - ContentWidth()) / 2);
        var icon = Icons.Get(_icon, Color.White);
        if (icon != null) g.DrawImage(icon, new Rectangle(x, (Height - IconSize) / 2, IconSize, IconSize));
        x += IconSize + IconGap;
        int right = Width - S(16);

        if (!_percent.HasValue)
        {
            TextRenderer.DrawText(g, _text, _fText, new Rectangle(x, 0, right - x, Height), Color.White,
                Flags | TextFormatFlags.EndEllipsis);
            return;
        }

        // Title above a bar with the percentage beside it
        int mid = Height / 2;
        TextRenderer.DrawText(g, _text, _fBarText, new Rectangle(x, mid - S(22), right - x, S(22)), Color.White,
            Flags | TextFormatFlags.EndEllipsis);

        int barY = mid + S(10);
        int h = Math.Max(3, S(4));
        using (var b = new SolidBrush(Color.FromArgb(70, 74, 82)))
        using (var path = Theme.RoundRect(new Rectangle(x, barY - h / 2, BarWidth, h), Math.Max(1, h / 2)))
            g.FillPath(b, path);
        int fill = (int)(BarWidth * Math.Clamp(_percent.Value, 0, 100) / 100.0);
        if (fill > h)
        {
            using var b = new SolidBrush(_color);
            using var path = Theme.RoundRect(new Rectangle(x, barY - h / 2, fill, h), Math.Max(1, h / 2));
            g.FillPath(b, path);
        }
        TextRenderer.DrawText(g, PctText, _fPct, new Rectangle(x + BarWidth + S(10), barY - S(10), S(48), S(20)),
            Color.White, Flags);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
