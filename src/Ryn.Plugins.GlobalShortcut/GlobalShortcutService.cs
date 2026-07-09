using Ryn.Core;
using Ryn.Plugins.GlobalShortcut.Backends;

namespace Ryn.Plugins.GlobalShortcut;

public sealed class GlobalShortcutService : IDisposable
{
    private readonly IGlobalShortcutBackend _backend;
    private readonly object _lock = new();
    // canonical form -> the accelerator string the app originally registered, echoed back on activation.
    private readonly Dictionary<string, string> _registrations = [];
    private bool _disposed;

    internal Action<string, string>? EmitEvent { get; set; }

    internal GlobalShortcutService(IMainThreadDispatcher mainThread)
        : this(CreateBackend(mainThread))
    {
    }

    // Test seam: lets tests drive the service against a fake backend without touching Carbon/Win32.
    internal GlobalShortcutService(IGlobalShortcutBackend backend)
    {
        _backend = backend;
        _backend.Activated += OnActivated;
    }

    private static IGlobalShortcutBackend CreateBackend(IMainThreadDispatcher mainThread)
    {
        if (OperatingSystem.IsMacOS())
            return new MacOsGlobalShortcutBackend(mainThread);
        if (OperatingSystem.IsWindows())
            return new WindowsGlobalShortcutBackend();
        return new StubGlobalShortcutBackend();
    }

    /// <summary>
    /// Registers a system-wide hotkey. Returns <c>false</c> for unparsable accelerators, unmappable keys, or
    /// when the OS rejects the combination (typically because another application owns it).
    /// </summary>
    public bool Register(string accelerator)
    {
        if (_disposed) return false;
        if (!AcceleratorParser.TryParse(accelerator, preferCommand: OperatingSystem.IsMacOS(), out var parsed))
            return false;

        var canonical = parsed.ToCanonicalString();
        lock (_lock)
        {
            if (_registrations.ContainsKey(canonical)) return true; // idempotent re-register
        }

        if (!_backend.Register(parsed, canonical)) return false;

        lock (_lock)
        {
            _registrations[canonical] = accelerator;
        }
        return true;
    }

    /// <summary>Removes a previously registered hotkey. Returns <c>false</c> if it wasn't registered.</summary>
    public bool Unregister(string accelerator)
    {
        if (_disposed) return false;
        if (!AcceleratorParser.TryParse(accelerator, preferCommand: OperatingSystem.IsMacOS(), out var parsed))
            return false;

        var canonical = parsed.ToCanonicalString();
        lock (_lock)
        {
            if (!_registrations.Remove(canonical)) return false;
        }
        return _backend.Unregister(canonical);
    }

    /// <summary>Whether this application currently holds the given hotkey.</summary>
    public bool IsRegistered(string accelerator)
    {
        if (_disposed) return false;
        if (!AcceleratorParser.TryParse(accelerator, preferCommand: OperatingSystem.IsMacOS(), out var parsed))
            return false;
        lock (_lock)
        {
            return _registrations.ContainsKey(parsed.ToCanonicalString());
        }
    }

    /// <summary>Removes every hotkey this application registered.</summary>
    public void UnregisterAll()
    {
        if (_disposed) return;
        List<string> canonicals;
        lock (_lock)
        {
            canonicals = [.. _registrations.Keys];
            _registrations.Clear();
        }
        foreach (var canonical in canonicals)
            _backend.Unregister(canonical);
    }

    private void OnActivated(string canonical)
    {
        string? original;
        lock (_lock)
        {
            _registrations.TryGetValue(canonical, out original);
        }
        if (original is null) return;

        // Encode as a proper JSON string (the accelerator came from the app but may still contain characters
        // that would break naive concatenation downstream).
        EmitEvent?.Invoke(
            "globalShortcut.activated",
            $"\"{System.Text.Json.JsonEncodedText.Encode(original)}\"");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend.Activated -= OnActivated;
        _backend.Dispose();
    }
}
