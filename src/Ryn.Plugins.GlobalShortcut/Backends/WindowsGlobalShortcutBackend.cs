using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Core.Internal;

namespace Ryn.Plugins.GlobalShortcut.Backends;

/// <summary>
/// Windows system-wide hotkeys via <c>RegisterHotKey</c>. Hotkeys are thread-affine — <c>WM_HOTKEY</c> is
/// posted to the registering thread's queue — so this backend runs its own message-only window on a
/// dedicated thread (the same pattern as the Tray plugin's hidden window), independent of the saucer UI
/// loop. Register/unregister requests are queued and executed on that thread; callers block on a per-request
/// event to get the real OS result.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsGlobalShortcutBackend : IGlobalShortcutBackend
{
    private const int WmApp = 0x8000;
    private const int WmProcessRequests = WmApp + 1;
    private const int WmHotKey = 0x0312;
    private const int WmDestroy = 0x0002;

    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModWin = 0x8;
    private const uint ModNoRepeat = 0x4000;

    private sealed class Request
    {
        public required bool IsRegister { get; init; }
        public required string Canonical { get; init; }
        public uint Modifiers { get; init; }
        public uint VirtualKey { get; init; }
        public bool Result { get; set; }
        public ManualResetEventSlim Done { get; } = new();
    }

    private readonly ConcurrentQueue<Request> _requests = new();
    private readonly ManualResetEventSlim _readyEvent = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _registered = [];
    private readonly Dictionary<int, string> _idToCanonical = [];
    private int _nextId = 1;

    private readonly WndProcDelegate _wndProcRef;
    private Thread? _thread;
    private nint _hwnd;
    private bool _disposed;

    public event Action<string>? Activated;

    internal WindowsGlobalShortcutBackend()
    {
        _wndProcRef = WndProc;
    }

    public bool Register(ParsedAccelerator accelerator, string canonical)
    {
        if (_disposed) return false;
        if (!TryMapVirtualKey(accelerator.Key, out var vk)) return false;

        var modifiers = ModNoRepeat;
        if (accelerator.Command) modifiers |= ModWin; // Cmd/Super = the Windows key
        if (accelerator.Control) modifiers |= ModControl;
        if (accelerator.Alt) modifiers |= ModAlt;
        if (accelerator.Shift) modifiers |= ModShift;

        return Execute(new Request
        {
            IsRegister = true,
            Canonical = canonical,
            Modifiers = modifiers,
            VirtualKey = vk,
        });
    }

    public bool Unregister(string canonical)
    {
        if (_disposed) return false;
        return Execute(new Request { IsRegister = false, Canonical = canonical });
    }

    private bool Execute(Request request)
    {
        EnsureThread();
        if (_hwnd == 0) return false;

        _requests.Enqueue(request);
        PostMessage(_hwnd, WmProcessRequests, 0, 0);
        // The hotkey thread only runs its message loop; requests complete in microseconds. The timeout is a
        // deadlock backstop (e.g. the thread died), after which false is the honest answer.
        return request.Done.Wait(TimeSpan.FromSeconds(5)) && request.Result;
    }

    private void EnsureThread()
    {
        if (_thread is not null) return;
        lock (_lock)
        {
            if (_thread is not null) return;
            _thread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "RynGlobalShortcut",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _readyEvent.Wait();
        }
    }

    private void RunMessageLoop()
    {
        var className = $"RynGlobalShortcut_{Environment.ProcessId}";
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcRef),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);

        // Message-only window (HWND_MESSAGE parent): no visual presence, just a message queue.
        _hwnd = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, -3, nint.Zero, wc.hInstance, nint.Zero);

        _readyEvent.Set();

        while (GetMessage(out var msg, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
        // On a throwing handler, return 0 ("message handled") rather than crossing the boundary; the onError
        // value is evaluated eagerly, so it must be a plain default and not a re-entrant DefWindowProc call.
        => NativeGuard.Invoke("WindowsGlobalShortcutBackend.WndProc", (nint)0, () =>
        {
            if (msg == WmProcessRequests)
            {
                while (_requests.TryDequeue(out var request))
                    ProcessRequest(hwnd, request);
                return 0;
            }

            if (msg == WmHotKey)
            {
                string? canonical;
                lock (_lock)
                {
                    _idToCanonical.TryGetValue((int)wParam, out canonical);
                }
                if (canonical is not null)
                    Activated?.Invoke(canonical);
                return 0;
            }

            return DefWindowProc(hwnd, msg, wParam, lParam);
        });

    private void ProcessRequest(nint hwnd, Request request)
    {
        try
        {
            lock (_lock)
            {
                if (request.IsRegister)
                {
                    if (_registered.ContainsKey(request.Canonical))
                    {
                        request.Result = true; // already ours — idempotent
                        return;
                    }
                    var id = _nextId++;
                    // Fails with ERROR_HOTKEY_ALREADY_REGISTERED when another app owns the combination.
                    if (!RegisterHotKey(hwnd, id, request.Modifiers, request.VirtualKey)) return;
                    _registered[request.Canonical] = id;
                    _idToCanonical[id] = request.Canonical;
                    request.Result = true;
                }
                else
                {
                    if (!_registered.Remove(request.Canonical, out var id)) return;
                    _ = UnregisterHotKey(hwnd, id);
                    _idToCanonical.Remove(id);
                    request.Result = true;
                }
            }
        }
        finally
        {
            request.Done.Set();
        }
    }

    /// <summary>Maps a normalized accelerator key to a Win32 virtual-key code.</summary>
    internal static bool TryMapVirtualKey(string key, out uint vk)
    {
        if (key.Length == 1)
        {
            var c = key[0];
            vk = c switch
            {
                >= 'a' and <= 'z' => (uint)char.ToUpperInvariant(c),
                >= '0' and <= '9' => c,
                ';' => 0xBA, '=' => 0xBB, ',' => 0xBC, '-' => 0xBD, '.' => 0xBE, '/' => 0xBF,
                '`' => 0xC0, '[' => 0xDB, '\\' => 0xDC, ']' => 0xDD, '\'' => 0xDE,
                '+' => 0xBB, // VK_OEM_PLUS
                _ => 0,
            };
            return vk != 0;
        }

        if (AcceleratorParser.IsFunctionKey(key))
        {
            vk = 0x70u + uint.Parse(key.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture) - 1; // VK_F1..VK_F24
            return true;
        }

        vk = key switch
        {
            "enter" => 0x0D, "tab" => 0x09, "space" => 0x20, "backspace" => 0x08, "escape" => 0x1B,
            "delete" => 0x2E, "home" => 0x24, "end" => 0x23, "pageup" => 0x21, "pagedown" => 0x22,
            "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28,
            _ => 0,
        };
        return vk != 0;
    }

    ~WindowsGlobalShortcutBackend() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);

        if (_hwnd != 0)
        {
            // The hotkey thread owns all registrations; WM_DESTROY tears them down implicitly (the system
            // unregisters a window's hotkeys when it is destroyed) and ends the loop via the posted quit.
            PostMessage(_hwnd, WmDestroy, nint.Zero, nint.Zero);
            _hwnd = 0;
        }

        _readyEvent.Dispose();
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int w, int h, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    private static partial void TranslateMessage(ref Msg lpMsg);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(ref Msg lpMsg);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial nint GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public Point pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
