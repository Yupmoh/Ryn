using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Ryn.Core;

namespace Ryn.Plugins.Audio.Backends;

[SupportedOSPlatform("macos")]
internal sealed partial class MacOsAudioBackend : IAudioBackend
{
    // NSSound is an AppKit/NSObject type, so creating, playing, stopping and mutating it must happen on the
    // main (UI) thread — the same thread that runs the native event loop. IPC commands arrive on thread-pool
    // threads, so every NSSound touch below is marshaled onto the main thread through this dispatcher
    // (INT-02). _currentSound is only ever read/written on the UI thread, so it needs no lock of its own.
    private readonly IMainThreadDispatcher _dispatcher;
    private nint _currentSound;
    private bool _disposed;

    public MacOsAudioBackend(IMainThreadDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    // NOTE (PLG-03): the file path is intentionally NOT path-scoped here. Audio playback is gated by the
    // capability system (ryn.json) like every other command — an app that does not grant `audio.play` cannot
    // reach this code at all — and the legitimate use of audio.play is to play a file the user just chose
    // (e.g. via a file dialog), which is routinely outside the application directory. Scoping the path to the
    // app directory would break that without adding meaningful protection beyond the capability gate, so the
    // path is passed through as-is. Callers that want a tighter policy should restrict `audio.play` in ryn.json.
    public void Play(string path, int volume, bool loop)
    {
        ArgumentNullException.ThrowIfNull(path);

        _dispatcher.Post(() => PlayOnUiThread(path, volume, loop));
    }

    private unsafe void PlayOnUiThread(string path, int volume, bool loop)
    {
        StopOnUiThread();

        var pool = objc_autoreleasePoolPush();
        try
        {
            var pathStr = CreateNSString(path);

            var soundAlloc = objc_msgSend_ret_nint(
                (void*)objc_getClass("NSSound"), sel_registerName("alloc"));
            var sound = objc_msgSend_ptr_bool_ret_nint(
                (void*)soundAlloc, sel_registerName("initWithContentsOfFile:byReference:"), (void*)pathStr, 1);

            if (sound != 0)
            {
                objc_msgSend_float((void*)sound, sel_registerName("setVolume:"), volume / 100.0f);
                objc_msgSend_bool((void*)sound, sel_registerName("setLoops:"), loop ? (byte)1 : (byte)0);
                _currentSound = sound;
                objc_msgSend_ret_bool((void*)sound, sel_registerName("play"));
            }
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public void PlaySystem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // PAP-18: `name` arrives straight from JS and is interpolated into a filesystem path below. Reject
        // anything that is not a bare, safe sound name so a caller cannot escape /System/Library/Sounds via
        // path separators or "..". Legitimate macOS system-sound names ("Glass", "Ping", "Funk", ...) are
        // alphanumeric and pass unchanged.
        if (!IsSafeSystemSoundName(name))
            return;

        var path = $"/System/Library/Sounds/{name}.aiff";
        if (File.Exists(path))
        {
            Play(path, 100, false);
        }
    }

    /// <summary>
    /// True when <paramref name="name"/> is a bare system-sound name safe to interpolate into a path:
    /// non-empty and composed only of letters, digits, space, underscore and hyphen. This rejects any path
    /// separator ('/' or '\'), "." / ".." traversal, NUL and every other metacharacter, so the interpolated
    /// path can never leave the system sounds directory.
    /// </summary>
    internal static bool IsSafeSystemSoundName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var c in name)
        {
            var ok = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == ' ' || c == '_' || c == '-';
            if (!ok)
                return false;
        }

        return true;
    }

    public void Stop() => _dispatcher.Post(StopOnUiThread);

    private unsafe void StopOnUiThread()
    {
        if (_currentSound != 0)
        {
            objc_msgSend_void((void*)_currentSound, sel_registerName("stop"));
            objc_msgSend_ret_nint((void*)_currentSound, sel_registerName("release"));
            _currentSound = 0;
        }
    }

    public void SetVolume(int percent) => _dispatcher.Post(() => SetVolumeOnUiThread(percent));

    private unsafe void SetVolumeOnUiThread(int percent)
    {
        if (_currentSound != 0)
        {
            var volume = percent / 100.0f;
            objc_msgSend_float((void*)_currentSound, sel_registerName("setVolume:"), volume);
        }
    }

    public bool IsPlaying()
    {
        var playing = false;
        // InvokeAsync runs inline when already on the UI thread and otherwise marshals + signals completion;
        // we read the NSSound state on the UI thread and wait for the answer. (Called from thread-pool IPC
        // threads, so the wait is fine; when already on the UI thread it completes synchronously inline.)
        _dispatcher.InvokeAsync(() => playing = IsPlayingOnUiThread()).GetAwaiter().GetResult();
        return playing;
    }

    private unsafe bool IsPlayingOnUiThread()
    {
        if (_currentSound == 0)
            return false;
        return objc_msgSend_ret_bool((void*)_currentSound, sel_registerName("isPlaying")) != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Release the live NSSound on the UI thread. Dispose runs during orderly shutdown; Post is a no-op
        // once the loop has stopped, in which case the process is going away and the sound dies with it.
        _dispatcher.Post(StopOnUiThread);
    }

    private static unsafe nint CreateNSString(string str)
    {
        var utf8 = Encoding.UTF8.GetBytes(str + "\0");
        fixed (byte* ptr = utf8)
        {
            return objc_msgSend_ptr_ret_nint(
                (void*)objc_getClass("NSString"),
                sel_registerName("stringWithUTF8String:"),
                ptr);
        }
    }

    // --- ObjC Runtime P/Invoke ---

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint sel_registerName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_getClass(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint objc_msgSend_ret_nint(void* receiver, nint selector);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint objc_msgSend_ptr_ret_nint(
        void* receiver, nint selector, void* value);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial nint objc_msgSend_ptr_bool_ret_nint(
        void* receiver, nint selector, void* value, byte boolValue);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_void(void* receiver, nint selector);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_bool(
        void* receiver, nint selector, byte value);

    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial void objc_msgSend_float(
        void* receiver, nint selector, float value);

    // PAP-17: ObjC BOOL is a single signed byte and objc_msgSend only guarantees the low byte of the return
    // register. Marshaling it as a 4-byte Win32 BOOL (UnmanagedType.Bool) can read garbage in the upper
    // bytes and report a false "true". Return the raw byte (UnmanagedType.U1) and let callers compare != 0.
    [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial byte objc_msgSend_ret_bool(void* receiver, nint selector);
}
