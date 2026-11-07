using System.Drawing.Drawing2D;

namespace CHelper.UI;

/// <summary>
/// G-Helper style button: rounded card, optional icon above (or beside) the text,
/// coloured border and icon when active (no fill tint). Fully custom-drawn so it scales cleanly with DPI.
/// </summary>
public sealed class TileButton : Control
{
    private string _icon;
    private bool _active, _hover, _down;
    private Color _accent = Theme.Standard;
    private Color? _fill;

    public TileButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
    }

    public string IconName { get => _icon; set { _icon = value; Invalidate(); } }
    public Color Accent { get => _accent; set { _accent = value; Invalidate(); } }
    public bool Active { get => _active; set { if (_active != value) { _active = value; Invalidate(); } } }

    /// <summary>When set, the card is filled with this colour (used for the keyboard colour swatch).</summary>
    public Color? Fill { get => _fill; set { _fill = value; Invalidate(); } }

    private Color[] _gradient;

    /// <summary>When set, the card is filled with a left-to-right gradient (rainbow / heat swatch).</summary>
    public Color[] Gradient { get => _gradient; set { _gradient = value; Invalidate(); } }

    /// <summary>Icon above text (true) or before text (false).</summary>
    public bool Vertical { get; set; } = true;
    public int IconSize { get; set; } = 28;
    public bool Borderless { get; set; }

    /// <summary>Extra size factor on top of DPI (the main window uses Theme.MainScale).</summary>
    public float UiScale { get; set; } = 1f;

    private float DpiScale => DeviceDpi / 96f * UiScale;
    private int S(float v) => (int)Math.Round(v * DpiScale);

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        bool showActive = _active && Enabled;
        float penW = showActive ? Math.Max(2f, 2f * DpiScale) : Math.Max(1f, DpiScale);
        int inset = (int)Math.Ceiling(penW / 2);
        var rect = new Rectangle(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
        int radius = S(6);

        if (!Borderless)
        {
            using var path = Theme.RoundRect(rect, radius);
            Color back = _fill ?? (_down ? Theme.CardPressed : _hover && Enabled ? Theme.CardHover : Theme.Card);
            if (_gradient is { Length: >= 2 } && rect.Width > 0)
            {
                using var gb = new LinearGradientBrush(rect, _gradient[0], _gradient[^1], LinearGradientMode.Horizontal)
                {
                    InterpolationColors = new ColorBlend
                    {
                        Colors = _gradient,
                        Positions = Enumerable.Range(0, _gradient.Length).Select(i => i / (float)(_gradient.Length - 1)).ToArray(),
                    },
                };
                g.FillPath(gb, path);
            }
            else
            {
                using var b = new SolidBrush(back);
                g.FillPath(b, path);
            }

            Color border = showActive ? _accent : (_hover && Enabled ? Theme.LineHover : Theme.Line);
            using var pen = new Pen(border, penW);
            g.DrawPath(pen, path);
        }

        if (string.IsNullOrEmpty(Text) && _icon == null) return;

        // Like G-Helper: an active tile gets a coloured border and icon; the text stays white.
        Color content = !Enabled ? Theme.Muted : (Borderless && !_hover ? Theme.Muted : ForeColor);
        var icon = _icon == null ? null : Icons.Get(_icon, showActive ? _accent : content);
        int iconPx = icon == null ? 0 : S(IconSize);
        int gap = icon == null || string.IsNullOrEmpty(Text) ? 0 : S(Vertical ? 5 : 8);
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        // Measure without EndEllipsis: with an empty proposed size it would measure only the "…".
        Size textSize = string.IsNullOrEmpty(Text) ? Size.Empty
            : TextRenderer.MeasureText(g, Text, Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        if (Vertical)
        {
            int total = iconPx + gap + textSize.Height;
            int y = (Height - total) / 2;
            if (icon != null) g.DrawImage(icon, new Rectangle((Width - iconPx) / 2, y, iconPx, iconPx));
            var tr = new Rectangle(S(4), y + iconPx + gap, Width - S(8), textSize.Height);
            TextRenderer.DrawText(g, Text, Font, tr, content, flags | TextFormatFlags.HorizontalCenter);
        }
        else
        {
            int maxText = Width - S(12) - iconPx - gap;
            int textW = Math.Min(textSize.Width, maxText);
            int total = iconPx + gap + textW;
            int x = Math.Max(S(6), (Width - total) / 2);
            if (icon != null) g.DrawImage(icon, new Rectangle(x, (Height - iconPx) / 2, iconPx, iconPx));
            var tr = new Rectangle(x + iconPx + gap, 0, textW + S(4), Height);
            TextRenderer.DrawText(g, Text, Font, tr, content, flags | TextFormatFlags.VerticalCenter);
        }
    }
}
