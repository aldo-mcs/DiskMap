using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using DiskMap.Core.FileTypes;

namespace DiskMap.App.Infrastructure;

/// <summary>
/// Stable color per file extension, used by both the treemap and the extension legend so they always agree.
/// Colors are derived from the <see cref="FileCategory"/> system — the single source of truth for both
/// analytics rollups and color assignment. Deliberately theme-neutral: file-type color is data, not chrome,
/// so it stays the same vivid color regardless of dark/light theme rather than being darkened to "fit."
/// Each color renders as a subtle diagonal gradient rather than a flat fill.
/// </summary>
public static class FileTypeColors
{
    public static readonly Color DirectoryColor = Color.FromRgb(0x6E, 0x77, 0x81);
    public static readonly Color EmptyColor = Color.FromRgb(0x2A, 0x2A, 0x2C);

    // Theme-neutral saturation/value — bright and clear enough to read on either a dark or light
    // background without looking muddy. File-type color is data, so it doesn't adapt to chrome.
    private const double Saturation = 0.62;
    private const double Value = 0.88;

    // How far the subtle gradient's two stops diverge from the base color, as a fraction of Value.
    // Small on purpose — "barely visible," a hint of depth rather than a visible highlight.
    private const double GradientSpread = 0.06;

    // Curated per-extension overrides for very common extensions that deserve distinct hues
    // *within* their category (e.g. .pdf should look distinct from .doc even though both are Documents).
    // Everything else derives its color from its category via the hash-based disperser.
    private static readonly Dictionary<string, (double HueOffset, double SatBoost)> CuratedOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        // Give PDF its own red that stands out from blue documents
        [".pdf"] = (0, 0),
        // CSS slightly more green than HTML
        [".css"] = (20, 0),
        // Python a distinct purple-blue vs generic code teal
        [".py"] = (-20, 0.1),
        // DLL distinctly more muted than exe
        [".dll"] = (0, -0.2),
    };

    private static readonly ConcurrentDictionary<string, Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    public static Color GetColor(string extension)
    {
        if (string.IsNullOrEmpty(extension) || extension == "(no extension)")
            return Hex(0x80, 0x86, 0x8E);
        return FromExtension(extension);
    }

    public static Brush GetBrush(string extension) =>
        BrushCache.GetOrAdd(extension, static ext => MakeGradientBrush(GetColor(ext)));

    private static readonly Brush DirectoryBrushInstance = MakeGradientBrush(DirectoryColor);
    public static Brush DirectoryBrush => DirectoryBrushInstance;

    /// <summary>Color for a category, used by analytics bar and legend.</summary>
    public static Brush GetCategoryBrush(FileCategory cat) =>
        BrushCache.GetOrAdd("cat:" + cat, c => MakeGradientBrush(FromHsv(FileCategoryMeta.Hue(cat), Saturation, Value)));

    private static Color FromExtension(string extension)
    {
        var baseCategory = Categorizer.OfExtension(extension);

        // Apply curated per-extension overrides where they exist
        if (CuratedOverrides.TryGetValue(extension, out var ovr))
        {
            double overrideHue = FileCategoryMeta.Hue(baseCategory) + ovr.HueOffset;
            double sat = Math.Clamp(Saturation + ovr.SatBoost, 0.3, 0.8);
            return FromHsv(overrideHue, sat, Value);
        }

        // Disperse hues within the category so .jpg and .png look different but both clearly "Image"
        uint hash = 2166136261;
        foreach (char ch in extension) { hash ^= ch; hash *= 16777619; }
        double baseHue = FileCategoryMeta.Hue(baseCategory);
        double spread = 30; // ±30° within the category band
        double hue = baseHue + ((hash % 61) - 30) * (spread / 30.0); // hash%61 gives -30..+30
        return FromHsv(hue, Saturation, Value);
    }

    /// <summary>Subtle top-left-to-bottom-right gradient: same hue/saturation, value nudged up then
    /// down by <see cref="GradientSpread"/> so tiles and swatches read as flat color from a distance
    /// but pick up a faint sense of depth up close.</summary>
    private static Brush MakeGradientBrush(Color color)
    {
        ColorToHsv(color, out double h, out double s, out double v);
        var lighter = FromHsv(h, s, Math.Clamp(v + GradientSpread, 0, 1));
        var darker = FromHsv(h, s, Math.Clamp(v - GradientSpread, 0, 1));
        var brush = new LinearGradientBrush(lighter, darker, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static Color FromHsv(double h, double s, double v)
    {
        // Normalize hue to 0..360
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static void ColorToHsv(Color color, out double h, out double s, out double v)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        h = 0;
        if (delta > 0.0001)
        {
            if (max == r) h = 60 * (((g - b) / delta) % 6);
            else if (max == g) h = 60 * (((b - r) / delta) + 2);
            else h = 60 * (((r - g) / delta) + 4);
            if (h < 0) h += 360;
        }
        s = max <= 0 ? 0 : delta / max;
        v = max;
    }

    private static Color Hex(int r, int g, int b) => Color.FromRgb((byte)r, (byte)g, (byte)b);
}
