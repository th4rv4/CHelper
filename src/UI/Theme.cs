using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CHelper.UI;

public static class Theme
{
    public static readonly Color Back = Color.FromArgb(0x12, 0x14, 0x18);
    public static readonly Color Card = Color.FromArgb(0x1B, 0x1E, 0x24);
    public static readonly Color CardHover = Color.FromArgb(0x23, 0x27, 0x30);
    public static readonly Color CardPressed = Color.FromArgb(0x2A, 0x2F, 0x3A);
    public static readonly Color Line = Color.FromArgb(0x2B, 0x30, 0x38);
    public static readonly Color LineHover = Color.FromArgb(0x3B, 0x42, 0x50);
    public static readonly Color Text = Color.FromArgb(0xEC, 0xEF, 0xF4);
    public static readonly Color Muted = Color.FromArgb(0x8D, 0x95, 0xA3);
    public static readonly Color Accent = Color.FromArgb(0x9B, 0xE1, 0x5D);
    /// <summary>Overall size of the main window (1 = original). Everything in it scales together.</summary>
    public const float MainScale = 0.8f;

    public static readonly Color Warn = Color.FromArgb(0xFF, 0xB0, 0x4A);
    public static readonly Color AccentDim = Color.FromArgb(0x22, 0x2D, 0x1C);

    // Mode colours, matching G-Helper
    public static readonly Color Eco = Color.FromArgb(6, 180, 138);
    public static readonly Color Standard = Color.FromArgb(58, 174, 239);
    public static readonly Color Turbo = Color.FromArgb(255, 32, 32);
    public static readonly Color Optimized = Color.FromArgb(155, 225, 93);

    /// <summary>Fonts in pixels, so they scale exactly with the layout.</summary>
    public static Font Px(float px, bool semibold = false) =>
        new(semibold ? "Segoe UI Semibold" : "Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel);

    public static readonly Font Body = new("Segoe UI", 9f, FontStyle.Regular);
    public static readonly Font Small = new("Segoe UI", 8.25f, FontStyle.Regular);
    public static readonly Font Heading = new("Segoe UI Semibold", 9.75f, FontStyle.Regular);
    public static readonly Font Title = new("Segoe UI Semibold", 10.5f, FontStyle.Regular);

    private static Icon _icon, _trayIcon;

    /// <summary>Window icon (all sizes from the embedded app.ico).</summary>
    public static Icon AppIcon => _icon ??= LoadIcon(Size.Empty);

    /// <summary>Tray icon at the system's small-icon size, so it stays crisp at any scaling.</summary>
    public static Icon TrayIcon => _trayIcon ??= LoadIcon(SystemInformation.SmallIconSize);

    private static Icon LoadIcon(Size size)
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
        if (stream == null) return SystemIcons.Application;
        return size.IsEmpty ? new Icon(stream) : new Icon(stream, size);
    }

    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void DarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}
