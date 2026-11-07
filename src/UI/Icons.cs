using System.Reflection;

namespace CHelper.UI;

/// <summary>Embedded icons (black on transparent, from G-Helper / Icons8), recolored on demand and cached.</summary>
public static class Icons
{
    private static readonly Dictionary<string, Bitmap> Source = new();
    private static readonly Dictionary<(string, int), Bitmap> Tinted = new();

    public static Bitmap Get(string name, Color color)
    {
        var key = (name, color.ToArgb());
        if (Tinted.TryGetValue(key, out var cached)) return cached;

        var src = Load(name);
        if (src == null) return null;

        var bmp = new Bitmap(src.Width, src.Height);
        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                int a = src.GetPixel(x, y).A;
                bmp.SetPixel(x, y, Color.FromArgb(a, color.R, color.G, color.B));
            }
        }
        Tinted[key] = bmp;
        return bmp;
    }

    private static Bitmap Load(string name)
    {
        if (Source.TryGetValue(name, out var bmp)) return bmp;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name + ".png");
        bmp = stream == null ? null : new Bitmap(stream);
        Source[name] = bmp;
        return bmp;
    }
}
