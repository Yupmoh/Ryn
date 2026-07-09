using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Core.Internal;
using Ryn.Plugins.Badge.Backends;

namespace Ryn.Plugins.Badge;

public sealed class BadgeService : IDisposable
{
    private readonly IBadgeBackend _backend;
    private bool _disposed;

    internal BadgeService(IMainThreadDispatcher mainThread, IServiceProvider services)
        : this(CreateBackend(mainThread, services))
    {
    }

    // Test seam: lets tests drive the service against a fake backend without touching AppKit/COM.
    internal BadgeService(IBadgeBackend backend) => _backend = backend;

    private static IBadgeBackend CreateBackend(IMainThreadDispatcher mainThread, IServiceProvider services)
    {
        if (OperatingSystem.IsMacOS())
            return new MacOsBadgeBackend(mainThread);
        if (OperatingSystem.IsWindows())
            return new WindowsBadgeBackend(mainThread, () =>
                services.GetService<RynWindowAccessor>()?.Window?.GetNativeWindowHandle() ?? 0);
        return new StubBadgeBackend();
    }

    /// <summary>Shows <paramref name="label"/> on the app icon; <c>null</c> or empty clears the badge.</summary>
    public void Set(string? label) => _backend.SetLabel(string.IsNullOrEmpty(label) ? null : label);

    /// <summary>Shows a numeric badge; 0 (or less) clears, values over 99 display as "99+".</summary>
    public void SetCount(int count) => _backend.SetLabel(FormatCount(count));

    public void Clear() => _backend.SetLabel(null);

    /// <summary>Formats a count the way platform badges conventionally do: null for ≤0, "99+" above 99.</summary>
    internal static string? FormatCount(int count) => count switch
    {
        <= 0 => null,
        > 99 => "99+",
        _ => count.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend.Dispose();
    }
}
