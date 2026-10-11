using System;
using System.Windows.Media;

namespace Dropshelf.Themes;

/// <summary>Small HSV helpers used to derive the accent family (hover, glow, mascot shades) from one colour.</summary>
internal static class ColorUtil
{
    public static void ToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 1e-9)
        {
            h = 0;
        }
        else if (max == r)
        {
            h = 60 * (((g - b) / d) % 6);
        }
        else if (max == g)
        {
            h = 60 * (((b - r) / d) + 2);
        }
        else
        {
            h = 60 * (((r - g) / d) + 4);
        }

        if (h < 0)
        {
            h += 360;
        }
    }

    public static Color FromHsv(double h, double s, double v, byte alpha = 255)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromArgb(alpha, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>Hover shade: same hue, a little paler and fully bright (blue #3B82F6 gives about #5B9BFF).</summary>
    public static Color Hover(Color c)
    {
        ToHsv(c, out double h, out double s, out _);
        return FromHsv(h, s * 0.85, 1);
    }

    /// <summary>Glow shade used for the bloom and lit rim: hue nudged toward violet, bright (about #5B7BFF for blue).</summary>
    public static Color Glow(Color c)
    {
        ToHsv(c, out double h, out double s, out _);
        return FromHsv(h + 10, s * 0.85, 1);
    }

    /// <summary>Deep shade at the belly of the mascot and in its glow (about #3B5BFF for blue).</summary>
    public static Color Deep(Color c)
    {
        ToHsv(c, out double h, out double s, out _);
        return FromHsv(h + 12, s, 1);
    }

    /// <summary>Pale tip of the mascot (about #7DB2FF for blue).</summary>
    public static Color Tip(Color c)
    {
        ToHsv(c, out double h, out double s, out _);
        return FromHsv(h, s * 0.67, 1);
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t),
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static SolidColorBrush Brush(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
