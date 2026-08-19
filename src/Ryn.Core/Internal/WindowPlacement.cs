using Ryn.Interop;

namespace Ryn.Core.Internal;

/// <summary>
/// Create-time window placement helpers. Used to map a requested origin onto a visible monitor
/// before the first paint — including the Windows <c>-32000</c> minimized/off-screen sentinel
/// and a state file saved on a now-disconnected display.
/// </summary>
internal static unsafe class WindowPlacement
{
    internal readonly record struct ScreenBounds(int X, int Y, int Width, int Height)
    {
        internal bool IsValid => Width > 0 && Height > 0;

        internal bool ContainsOrigin(int x, int y) =>
            x >= X && x < X + Width && y >= Y && y < Y + Height;

        internal bool Intersects(int x, int y, int width, int height) =>
            x < X + Width && x + width > X && y < Y + Height && y + height > Y;

        internal (int X, int Y) Clamp(int x, int y, int width, int height)
        {
            var maxX = X + Math.Max(0, Width - width);
            var maxY = Y + Math.Max(0, Height - height);
            return (Math.Clamp(x, X, maxX), Math.Clamp(y, Y, maxY));
        }
    }

    /// <summary>
    /// Keeps <paramref name="x"/>/<paramref name="y"/> on the monitor that contains the origin
    /// (or intersects the window). Origins that miss every screen — including <c>-32000</c> —
    /// fall back to the first valid monitor.
    /// </summary>
    internal static (int X, int Y) ClampToVisibleMonitor(
        int x, int y, int width, int height, ReadOnlySpan<ScreenBounds> screens)
    {
        ScreenBounds? containing = null;
        ScreenBounds? intersecting = null;
        ScreenBounds? firstValid = null;

        foreach (var screen in screens)
        {
            if (!screen.IsValid) continue;
            firstValid ??= screen;
            if (screen.ContainsOrigin(x, y))
            {
                containing = screen;
                break;
            }

            intersecting ??= screen.Intersects(x, y, width, height) ? screen : null;
        }

        var target = containing ?? intersecting ?? firstValid;
        return target is { } chosen ? chosen.Clamp(x, y, width, height) : (x, y);
    }

    /// <summary>
    /// Reads connected displays from saucer. Returns an empty array when the application handle
    /// is missing or the platform reports no screens. Caller must be on the UI thread.
    /// </summary>
    internal static ScreenBounds[] ReadScreens(saucer_application* app)
    {
        if (app == null) return [];

        nuint count;
        System.Runtime.CompilerServices.Unsafe.SkipInit(out count);
        Saucer.saucer_application_screens(app, null, &count);
        if (count == 0 || count > 32) return [];

        var pointers = stackalloc saucer_screen*[(int)count];
        var requested = count;
        Saucer.saucer_application_screens(app, pointers, &count);
        if (count > requested) count = requested;

        var screens = new ScreenBounds[count];
        for (nuint i = 0; i < count; i++)
        {
            var screen = pointers[i];
            if (screen == null) continue;
            int sx, sy, sw, sh;
            Saucer.saucer_screen_position(screen, &sx, &sy);
            Saucer.saucer_screen_size(screen, &sw, &sh);
            Saucer.saucer_screen_free(screen);
            screens[i] = new ScreenBounds(sx, sy, sw, sh);
        }

        return screens;
    }
}
