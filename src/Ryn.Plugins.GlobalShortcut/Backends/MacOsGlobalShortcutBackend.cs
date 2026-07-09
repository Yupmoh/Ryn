using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Core;
using Ryn.Core.Internal;

namespace Ryn.Plugins.GlobalShortcut.Backends;

/// <summary>
/// macOS system-wide hotkeys via Carbon's <c>RegisterEventHotKey</c> — long deprecated on paper, but still
/// the mechanism every mainstream app (and framework) uses: it needs no accessibility or input-monitoring
/// permission and fires regardless of which app is frontmost. All Carbon calls are marshalled onto the main
/// thread; a single app-target event handler dispatches hotkey ids back to managed code.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacOsGlobalShortcutBackend : IGlobalShortcutBackend
{
    // FourCharCodes: 'keyb', 'RYNH', '----', 'hkid'.
    private const uint EventClassKeyboard = 0x6B657962;
    private const uint EventHotKeyPressed = 5;
    private const uint HotKeySignature = 0x52594E48;
    private const uint ParamDirectObject = 0x2D2D2D2D;
    private const uint TypeEventHotKeyId = 0x686B6964;

    // Carbon modifier masks (Events.h).
    private const uint CmdKey = 0x0100;
    private const uint ShiftKey = 0x0200;
    private const uint OptionKey = 0x0800;
    private const uint ControlKey = 0x1000;

    private readonly IMainThreadDispatcher _mainThread;
    private readonly object _lock = new();
    private readonly Dictionary<string, (nint HotKeyRef, uint Id)> _registered = [];
    private readonly Dictionary<uint, string> _idToCanonical = [];
    private uint _nextId = 1;

    private nint _eventHandlerRef;
    private GCHandle _selfHandle;
    private bool _disposed;

    public event Action<string>? Activated;

    public MacOsGlobalShortcutBackend(IMainThreadDispatcher mainThread)
    {
        ArgumentNullException.ThrowIfNull(mainThread);
        _mainThread = mainThread;
    }

    public bool Register(ParsedAccelerator accelerator, string canonical)
    {
        if (_disposed) return false;
        if (!TryMapKeyCode(accelerator.Key, out var keyCode)) return false;

        uint modifiers = 0;
        if (accelerator.Command) modifiers |= CmdKey;
        if (accelerator.Control) modifiers |= ControlKey;
        if (accelerator.Alt) modifiers |= OptionKey;
        if (accelerator.Shift) modifiers |= ShiftKey;

        var registered = false;
        // Block until the UI thread has actually registered — the caller needs the real result. Safe while
        // the loop is running (the IPC path never runs on the UI thread); a no-op that returns false when
        // the loop is already gone.
        _mainThread.InvokeAsync(() => registered = RegisterOnUi(keyCode, modifiers, canonical))
            .GetAwaiter().GetResult();
        return registered;
    }

    private unsafe bool RegisterOnUi(uint keyCode, uint modifiers, string canonical)
    {
        if (_disposed) return false;
        if (!EnsureEventHandler()) return false;

        lock (_lock)
        {
            if (_registered.ContainsKey(canonical)) return true; // already ours — idempotent

            var id = _nextId++;
            var hotKeyId = new EventHotKeyId { Signature = HotKeySignature, Id = id };
            var status = RegisterEventHotKey(
                keyCode, modifiers, hotKeyId, GetApplicationEventTarget(), 0, out var hotKeyRef);
            if (status != 0 || hotKeyRef == 0) return false; // typically eventHotKeyExistsErr: owned elsewhere

            _registered[canonical] = (hotKeyRef, id);
            _idToCanonical[id] = canonical;
            return true;
        }
    }

    public bool Unregister(string canonical)
    {
        if (_disposed) return false;

        var removed = false;
        _mainThread.InvokeAsync(() =>
        {
            lock (_lock)
            {
                if (!_registered.Remove(canonical, out var entry)) return;
                _ = UnregisterEventHotKey(entry.HotKeyRef);
                _idToCanonical.Remove(entry.Id);
                removed = true;
            }
        }).GetAwaiter().GetResult();
        return removed;
    }

    private unsafe bool EnsureEventHandler()
    {
        if (_eventHandlerRef != 0) return true;

        if (!_selfHandle.IsAllocated)
            _selfHandle = GCHandle.Alloc(this);

        var eventType = new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed };
        var status = InstallEventHandler(
            GetApplicationEventTarget(),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnHotKeyEvent,
            1,
            &eventType,
            GCHandle.ToIntPtr(_selfHandle),
            out _eventHandlerRef);
        return status == 0 && _eventHandlerRef != 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe int OnHotKeyEvent(nint callRef, nint eventRef, nint userData)
        => NativeGuard.Invoke("MacOsGlobalShortcutBackend.OnHotKeyEvent", 0, () =>
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is not MacOsGlobalShortcutBackend backend) return 0;

            EventHotKeyId hotKeyId;
            var status = GetEventParameter(
                eventRef, ParamDirectObject, TypeEventHotKeyId, 0,
                (nuint)sizeof(EventHotKeyId), 0, &hotKeyId);
            if (status != 0 || hotKeyId.Signature != HotKeySignature) return 0;

            string? canonical;
            lock (backend._lock)
            {
                backend._idToCanonical.TryGetValue(hotKeyId.Id, out canonical);
            }
            if (canonical is not null)
                backend.Activated?.Invoke(canonical);
            return 0; // noErr — the hotkey is consumed
        });

    /// <summary>Maps a normalized accelerator key to a macOS virtual keycode (kVK_ANSI_* / kVK_*).</summary>
    internal static bool TryMapKeyCode(string key, out uint keyCode)
    {
        keyCode = key switch
        {
            "a" => 0x00, "s" => 0x01, "d" => 0x02, "f" => 0x03, "h" => 0x04, "g" => 0x05,
            "z" => 0x06, "x" => 0x07, "c" => 0x08, "v" => 0x09, "b" => 0x0B, "q" => 0x0C,
            "w" => 0x0D, "e" => 0x0E, "r" => 0x0F, "y" => 0x10, "t" => 0x11,
            "1" => 0x12, "2" => 0x13, "3" => 0x14, "4" => 0x15, "6" => 0x16, "5" => 0x17,
            "=" => 0x18, "9" => 0x19, "7" => 0x1A, "-" => 0x1B, "8" => 0x1C, "0" => 0x1D,
            "]" => 0x1E, "o" => 0x1F, "u" => 0x20, "[" => 0x21, "i" => 0x22, "p" => 0x23,
            "l" => 0x25, "j" => 0x26, "'" => 0x27, "k" => 0x28, ";" => 0x29, "\\" => 0x2A,
            "," => 0x2B, "/" => 0x2C, "n" => 0x2D, "m" => 0x2E, "." => 0x2F, "`" => 0x32,
            "+" => 0x45, // keypad plus; the ANSI plus is Shift+'='
            "enter" => 0x24, "tab" => 0x30, "space" => 0x31, "backspace" => 0x33, "escape" => 0x35,
            "delete" => 0x75, "home" => 0x73, "end" => 0x77, "pageup" => 0x74, "pagedown" => 0x79,
            "left" => 0x7B, "right" => 0x7C, "down" => 0x7D, "up" => 0x7E,
            "f1" => 0x7A, "f2" => 0x78, "f3" => 0x63, "f4" => 0x76, "f5" => 0x60, "f6" => 0x61,
            "f7" => 0x62, "f8" => 0x64, "f9" => 0x65, "f10" => 0x6D, "f11" => 0x67, "f12" => 0x6F,
            "f13" => 0x69, "f14" => 0x6B, "f15" => 0x71, "f16" => 0x6A, "f17" => 0x40, "f18" => 0x4F,
            "f19" => 0x50, "f20" => 0x5A,
            _ => uint.MaxValue,
        };
        return keyCode != uint.MaxValue;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Carbon teardown on the main thread, blocking so the GCHandle is freed only once the handler can
        // no longer fire. If the loop is already gone the dispatcher drops the work.
        _mainThread.InvokeAsync(() =>
        {
            lock (_lock)
            {
                foreach (var (hotKeyRef, _) in _registered.Values)
                    _ = UnregisterEventHotKey(hotKeyRef);
                _registered.Clear();
                _idToCanonical.Clear();
            }
            if (_eventHandlerRef != 0)
            {
                _ = RemoveEventHandler(_eventHandlerRef);
                _eventHandlerRef = 0;
            }
        }).GetAwaiter().GetResult();

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    private const string CarbonLib = "/System/Library/Frameworks/Carbon.framework/Carbon";

    [LibraryImport(CarbonLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int RegisterEventHotKey(
        uint keyCode, uint modifiers, EventHotKeyId hotKeyId, nint target, uint options, out nint hotKeyRef);

    [LibraryImport(CarbonLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int UnregisterEventHotKey(nint hotKeyRef);

    [LibraryImport(CarbonLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetApplicationEventTarget();

    [LibraryImport(CarbonLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int InstallEventHandler(
        nint target, nint handler, nuint numTypes, EventTypeSpec* typeList, nint userData, out nint handlerRef);

    [LibraryImport(CarbonLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int RemoveEventHandler(nint handlerRef);

    [LibraryImport(CarbonLib)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int GetEventParameter(
        nint eventRef, uint name, uint desiredType, nint outActualType, nuint bufferSize, nint outActualSize, void* data);
}
