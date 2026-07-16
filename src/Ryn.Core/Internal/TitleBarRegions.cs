namespace Ryn.Core.Internal;

/// <summary>
/// Pure hit-testing for custom title-bar drag regions, shared by the macOS overlay drag view. Rectangles
/// are flat <c>[x, y, w, h, …]</c> runs in viewport-top-left CSS pixels, as published by the
/// <c>data-webview-*</c> script.
/// </summary>
internal static class TitleBarRegions
{
    /// <summary>
    /// True when the point should start a window drag: inside a drag rect and not inside any ignore rect
    /// (an interactive control such as a button or a <c>data-webview-ignore</c> element).
    /// </summary>
    public static bool IsDraggable(double[] drag, double[] ignore, double x, double y) =>
        !Contains(ignore, x, y) && Contains(drag, x, y);

    /// <summary>Scales complete <c>[x, y, w, h]</c> runs from page CSS pixels into native points.</summary>
    public static double[] Scale(IReadOnlyList<double> rects, double factor)
    {
        var scaled = new double[rects.Count - rects.Count % 4];
        for (var i = 0; i < scaled.Length; i++)
            scaled[i] = rects[i] * factor;
        return scaled;
    }

    /// <summary>True when (x, y) falls inside any <c>[x, y, w, h]</c> rect in the flat run.</summary>
    public static bool Contains(double[] rects, double x, double y)
    {
        if (rects is null) return false;
        for (var i = 0; i + 3 < rects.Length; i += 4)
        {
            if (x >= rects[i] && x < rects[i] + rects[i + 2] &&
                y >= rects[i + 1] && y < rects[i + 1] + rects[i + 3])
            {
                return true;
            }
        }
        return false;
    }
}
