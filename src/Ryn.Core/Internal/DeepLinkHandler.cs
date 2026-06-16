using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Ryn.Core.Internal;

/// <summary>
/// Owns the deep-link runtime: OS scheme registration, startup-argument parsing, and the activation paths
/// that deliver an inbound <c>myapp://…</c> URL to the running application via
/// <see cref="RynApplication.TryDeliverDeepLink(Uri)"/>.
/// <para>
/// Activation differs per OS. On <b>macOS</b> the OS keeps a single app instance alive and re-delivers
/// subsequent URLs as an <c>kAEGetURL</c> Apple Event; we install a handler on
/// <c>NSAppleEventManager</c> that parses the URL and forwards it. On <b>Windows/Linux</b> the OS launches a
/// fresh process per URL, so we enforce single-instance ourselves (a named mutex on Windows, an advisory
/// lock file on Linux) and forward the URL from the second launch to the first over a local IPC channel (a
/// named pipe on Windows, a Unix-domain socket on Linux), then exit the second process.
/// </para>
/// <para>
/// Everything here is gated by the caller behind <c>DeepLinkSchemes.Count &gt; 0</c>: an app that declares
/// no schemes keeps the original multi-instance launch behavior and pays none of this cost.
/// </para>
/// </summary>
internal static partial class DeepLinkHandler
{
    // Guards the one-time runtime setup (macOS Apple-Event handler install, or the Win/Linux single-instance
    // acquisition + listener start) so repeated RegisterScheme calls — one per declared scheme — set it up once.
    private static readonly object s_runtimeLock = new();
    private static bool s_runtimeInitialized;

    // Win/Linux: held for the process lifetime by the primary instance. Releasing them (process exit or a
    // crash) lets the next launch become the new primary.
    private static Mutex? s_singleInstanceMutex;        // Windows
    private static FileStream? s_singleInstanceLock;     // Linux (advisory flock via exclusive FileStream)

    // Stable per-app identity for the mutex / pipe / socket names, derived from the declared schemes so two
    // different Ryn apps on the same machine never collide on each other's single-instance channel.
    private static string? s_channelKey;

    /// <summary>
    /// Sets up the OS activation runtime for the declared <paramref name="schemes"/> (once), then returns the
    /// startup deep-link URL parsed from the command line, or <see langword="null"/> if there is none.
    /// </summary>
    /// <remarks>
    /// This is the caller's single post-registration hook — it runs exactly when deep linking is enabled and
    /// receives the full declared scheme list — so the activation runtime is wired up here. On Windows/Linux
    /// that includes single-instance coordination: if this launch is a <em>second</em> instance, its startup
    /// URL is forwarded to the already-running primary and the current process is terminated, so the URL parsed
    /// and returned to the caller only ever activates the primary instance.
    /// </remarks>
    internal static Uri? CheckStartupArgs(IList<string> schemes)
    {
        InitializeRuntime(schemes);
        return ParseStartupUrl(schemes);
    }

    /// <summary>
    /// Scans the process command line for the first argument that is an absolute URL whose scheme matches one
    /// of <paramref name="schemes"/>, and returns it (or <see langword="null"/>). Pure: no side effects.
    /// </summary>
    private static Uri? ParseStartupUrl(IList<string> schemes)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length; i++)
        {
            if (!Uri.TryCreate(args[i], UriKind.Absolute, out var uri)) continue;
            foreach (var scheme in schemes)
            {
                if (uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))
                    return uri;
            }
        }
        return null;
    }

    /// <summary>
    /// Registers <paramref name="scheme"/> with the OS so the desktop shell routes <c>scheme://…</c> URLs to
    /// this executable. The runtime activation path (macOS Apple-Event handler, or Windows/Linux single-instance
    /// coordination) is wired up separately by <see cref="CheckStartupArgs"/>, which receives the full declared
    /// scheme list.
    /// </summary>
    internal static void RegisterScheme(string scheme, string appName)
    {
        if (OperatingSystem.IsWindows())
            RegisterWindows(scheme, appName);
        else if (OperatingSystem.IsLinux())
            RegisterLinux(scheme, appName);
        // macOS scheme registration is declarative (CFBundleURLTypes in Info.plist, emitted by the bundler),
        // so there is nothing to register at runtime — only the Apple-Event handler, installed via CheckStartupArgs.
    }

    /// <summary>
    /// One-time setup of the OS activation path for the running instance. Idempotent and thread-safe; the first
    /// call wins and later calls are no-ops.
    /// </summary>
    /// <remarks>
    /// On Windows/Linux, if another instance already holds the single-instance token this call forwards the
    /// startup deep-link URL (if any) to it and then <see cref="Environment.Exit(int)"/>s the current process —
    /// the supported way to enforce single-instance without changing the caller's control flow. The caller's
    /// subsequent URL parse / <c>RaiseDeepLink</c> only ever runs in the primary instance.
    /// </remarks>
    private static void InitializeRuntime(IList<string> schemes)
    {
        lock (s_runtimeLock)
        {
            if (s_runtimeInitialized) return;
            s_runtimeInitialized = true;
            s_channelKey = DeriveChannelKey(schemes);

            if (OperatingSystem.IsMacOS())
            {
                MacAppleEvents.Install();
                return;
            }

            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                TrySingleInstance(schemes);
            }
        }
    }

    // --- Single-instance coordination (Windows/Linux) ---

    private static void TrySingleInstance(IList<string> schemes)
    {
        var key = s_channelKey!;
        bool isPrimary;

        if (OperatingSystem.IsWindows())
            isPrimary = TryBecomePrimaryWindows(key);
        else if (OperatingSystem.IsLinux())
            isPrimary = TryBecomePrimaryLinux(key);
        else
            return;

        if (isPrimary)
        {
            // We own the app for this session: stand up the listener that delivers URLs forwarded by future
            // second launches into the live window.
            StartForwardListener(key);
            return;
        }

        // A primary is already running. Forward our startup URL to it (if we were launched with one) and exit
        // so the user keeps a single window. ParseStartupUrl (not CheckStartupArgs) avoids re-entering the
        // single-instance setup we are already inside.
        var url = ParseStartupUrl(schemes);
        if (url is not null)
            TryForwardUrl(key, url);

        // Hand control back to the already-running instance. A flush-then-exit: the forward above is synchronous
        // and completes (or times out) before we get here.
        Environment.Exit(0);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryBecomePrimaryWindows(string key)
    {
        try
        {
            // A named mutex is the canonical Windows single-instance primitive. createdNew == we are first.
            var mutex = new Mutex(initiallyOwned: true, $"Local\\Ryn.SingleInstance.{key}", out var createdNew);
            if (createdNew)
            {
                s_singleInstanceMutex = mutex;
                return true;
            }
            mutex.Dispose();
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Can't coordinate (locked-down environment): degrade to the original multi-instance behavior
            // rather than failing the launch. The app still works; it just won't forward.
            return true;
        }
    }

    [SupportedOSPlatform("linux")]
    private static bool TryBecomePrimaryLinux(string key)
    {
        try
        {
            var lockPath = Path.Combine(RuntimeDir(), $"ryn-{key}.lock");
            // Exclusive open == advisory single-instance lock; held open for the process lifetime. A stale lock
            // from a crashed process is reclaimable because the previous FileStream (and its lock) died with it.
            var stream = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            s_singleInstanceLock = stream;
            return true;
        }
        catch (IOException)
        {
            // FileShare.None contention: another live instance holds it.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Can't create the lock file; degrade to multi-instance rather than blocking the launch.
            return true;
        }
    }

    // --- Forward channel: primary listens, secondary sends ---

    private static void StartForwardListener(string key)
    {
        // Background, non-blocking, low-priority listener. It must not hold up the UI event loop, and it
        // outlives nothing — it is torn down implicitly when the process exits.
        var thread = new Thread(() =>
        {
            if (OperatingSystem.IsWindows())
                ListenWindows(key);
            else if (OperatingSystem.IsLinux())
                ListenLinux(key);
        })
        {
            IsBackground = true,
            Name = "Ryn.DeepLinkForward",
        };
        thread.Start();
    }

    [SupportedOSPlatform("windows")]
    private static void ListenWindows(string key)
    {
        var pipeName = $"Ryn.DeepLink.{key}";
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    pipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnection();
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = reader.ReadLine();
                DeliverForwarded(line);
            }
            catch (IOException) { /* connection torn down mid-read; loop and accept the next client. */ }
            catch (ObjectDisposedException) { return; }
        }
    }

    [SupportedOSPlatform("linux")]
    private static void ListenLinux(string key)
    {
        var socketPath = SocketPath(key);
        try
        {
            // A fresh bind requires the path be free; a stale file from a crashed primary would block bind.
            if (File.Exists(socketPath)) File.Delete(socketPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(backlog: 4);
        }
        catch (SocketException) { return; }
        catch (IOException) { return; }

        while (true)
        {
            try
            {
                using var client = listener.Accept();
                var buffer = new byte[4096];
                var read = client.Receive(buffer);
                if (read > 0)
                    DeliverForwarded(Encoding.UTF8.GetString(buffer, 0, read).TrimEnd('\n', '\r'));
            }
            catch (SocketException) { /* client died mid-transfer; accept the next one. */ }
            catch (ObjectDisposedException) { return; }
        }
    }

    private static void DeliverForwarded(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (Uri.TryCreate(line, UriKind.Absolute, out var uri))
            RynApplication.TryDeliverDeepLink(uri);
    }

    private static void TryForwardUrl(string key, Uri url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                ForwardWindows(key, url);
            else if (OperatingSystem.IsLinux())
                ForwardLinux(key, url);
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (TimeoutException) { }
        catch (UnauthorizedAccessException) { }
    }

    [SupportedOSPlatform("windows")]
    private static void ForwardWindows(string key, Uri url)
    {
        using var client = new NamedPipeClientStream(".", $"Ryn.DeepLink.{key}", PipeDirection.Out);
        client.Connect(2000);
        using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        writer.WriteLine(url.AbsoluteUri);
    }

    [SupportedOSPlatform("linux")]
    private static void ForwardLinux(string key, Uri url)
    {
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        client.Connect(new UnixDomainSocketEndPoint(SocketPath(key)));
        var payload = Encoding.UTF8.GetBytes(url.AbsoluteUri + "\n");
        client.Send(payload);
    }

    // --- Naming / paths ---

    private static string DeriveChannelKey(IList<string> schemes)
    {
        // Bind the channel identity to the executable path AND the declared schemes so distinct apps (and
        // distinct installs of the same app) never share a single-instance token. Hash to a short, filesystem-
        // and pipe-name-safe hex string. SHA-256 here is for naming uniqueness, not security.
        var seed = (Environment.ProcessPath ?? AppContext.BaseDirectory) + "|" +
                   string.Join(",", schemes.OrderBy(s => s, StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        // Upper-case hex (CA1308 prefers ToUpperInvariant). Both launches derive the same string, so the
        // pipe/socket/lock names match; Windows pipe names are case-insensitive regardless.
        return Convert.ToHexString(hash, 0, 8);
    }

    [SupportedOSPlatform("linux")]
    private static string SocketPath(string key)
        => Path.Combine(RuntimeDir(), $"ryn-{key}.sock");

    [SupportedOSPlatform("linux")]
    private static string RuntimeDir()
    {
        // Prefer XDG_RUNTIME_DIR (per-user, tmpfs, auto-cleaned on logout) for the lock + socket; fall back to
        // the system temp dir when it is unset (e.g. a bare service account).
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdg) && Directory.Exists(xdg))
            return xdg;
        return Path.GetTempPath();
    }

    private static void RegisterWindows(string scheme, string appName)
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return;

        try
        {
            var psi = new ProcessStartInfo("reg")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add($@"HKCU\Software\Classes\{scheme}");
            psi.ArgumentList.Add("/ve");
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add($"URL:{appName}");
            psi.ArgumentList.Add("/f");
            Process.Start(psi)?.WaitForExit(5000);

            var psi2 = new ProcessStartInfo("reg")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi2.ArgumentList.Add("add");
            psi2.ArgumentList.Add($@"HKCU\Software\Classes\{scheme}");
            psi2.ArgumentList.Add("/v");
            psi2.ArgumentList.Add("URL Protocol");
            psi2.ArgumentList.Add("/d");
            psi2.ArgumentList.Add("");
            psi2.ArgumentList.Add("/f");
            Process.Start(psi2)?.WaitForExit(5000);

            var psi3 = new ProcessStartInfo("reg")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi3.ArgumentList.Add("add");
            psi3.ArgumentList.Add($@"HKCU\Software\Classes\{scheme}\shell\open\command");
            psi3.ArgumentList.Add("/ve");
            psi3.ArgumentList.Add("/d");
            psi3.ArgumentList.Add($"\"{exePath}\" \"%1\"");
            psi3.ArgumentList.Add("/f");
            Process.Start(psi3)?.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void RegisterLinux(string scheme, string appName)
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null) return;

        try
        {
            var desktopDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "applications");
            Directory.CreateDirectory(desktopDir);

            var desktop = $"""
                [Desktop Entry]
                Name={appName}
                Exec={exePath} %u
                Type=Application
                MimeType=x-scheme-handler/{scheme};
                NoDisplay=true
                """;
            var desktopPath = Path.Combine(desktopDir, $"{appName}-{scheme}.desktop");
            File.WriteAllText(desktopPath, desktop);

            var psi = new ProcessStartInfo("xdg-mime")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("default");
            psi.ArgumentList.Add($"{appName}-{scheme}.desktop");
            psi.ArgumentList.Add($"x-scheme-handler/{scheme}");
            Process.Start(psi)?.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (IOException) { }
    }

    /// <summary>
    /// macOS Apple-Event activation. The OS keeps one app instance alive and delivers each subsequent
    /// <c>scheme://…</c> open as a <c>kInternetEventClass</c>/<c>kAEGetURL</c> Apple Event. We register an
    /// NSObject handler method on <c>NSAppleEventManager</c> that pulls the URL string out of the event
    /// descriptor and routes it to the live app. The reverse-P/Invoke callback body is wrapped in
    /// <see cref="NativeGuard"/> so a managed throw never unwinds across the AppKit boundary.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static partial class MacAppleEvents
    {
        // Apple Event four-char codes. kInternetEventClass = 'GURL', kAEGetURL = 'GURL',
        // keyDirectObject = '----'. These are OSType (FourCharCode) big-endian packed ASCII.
        private const uint KInternetEventClass = 0x4755524C; // 'GURL'
        private const uint KAEGetUrl = 0x4755524C;           // 'GURL'
        private const uint KeyDirectObject = 0x2D2D2D2D;      // '----'

        private static nint s_handlerClass;
        private static nint s_handlerObject;
        private static bool s_installed;

        internal static unsafe void Install()
        {
            if (s_installed) return;
            s_installed = true;

            // Define an NSObject subclass with a single handler selector and register the managed callback as
            // its implementation. The Objective-C type encoding "v@:@@" = void return; self, _cmd, and two
            // object args (the event descriptor and the reply descriptor).
            var superclass = objc_getClass("NSObject");
            s_handlerClass = objc_allocateClassPair(superclass, "RynAppleEventHandler", 0);
            class_addMethod(
                s_handlerClass,
                sel_registerName("handleGetURLEvent:withReplyEvent:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&OnGetUrl,
                "v@:@@");
            objc_registerClassPair(s_handlerClass);

            var alloc = objc_msgSend_ret_nint((void*)s_handlerClass, sel_registerName("alloc"));
            s_handlerObject = objc_msgSend_ret_nint((void*)alloc, sel_registerName("init"));

            // [[NSAppleEventManager sharedAppleEventManager]
            //     setEventHandler:self andSelector:@selector(handleGetURLEvent:withReplyEvent:)
            //     forEventClass:kInternetEventClass andEventID:kAEGetURL]
            var manager = objc_msgSend_ret_nint(
                (void*)objc_getClass("NSAppleEventManager"),
                sel_registerName("sharedAppleEventManager"));

            objc_msgSend_setHandler(
                (void*)manager,
                sel_registerName("setEventHandler:andSelector:forEventClass:andEventID:"),
                (void*)s_handlerObject,
                sel_registerName("handleGetURLEvent:withReplyEvent:"),
                KInternetEventClass,
                KAEGetUrl);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static void OnGetUrl(nint self, nint sel, nint @event, nint replyEvent)
            => NativeGuard.Invoke(nameof(OnGetUrl), () => HandleGetUrl(@event));

        private static unsafe void HandleGetUrl(nint @event)
        {
            if (@event == 0) return;

            // [[event paramDescriptorForKeyword:keyDirectObject] stringValue]
            var descriptor = objc_msgSend_descForKeyword(
                (void*)@event, sel_registerName("paramDescriptorForKeyword:"), KeyDirectObject);
            if (descriptor == 0) return;

            var nsString = objc_msgSend_ret_nint((void*)descriptor, sel_registerName("stringValue"));
            if (nsString == 0) return;

            var utf8 = objc_msgSend_ret_ptr((void*)nsString, sel_registerName("UTF8String"));
            if (utf8 == 0) return;

            var url = Marshal.PtrToStringUTF8(utf8);
            if (string.IsNullOrEmpty(url)) return;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                RynApplication.TryDeliverDeepLink(uri);
        }

        // --- ObjC runtime P/Invoke ---

        [LibraryImport("libobjc.dylib")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [LibraryImport("libobjc.dylib")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [LibraryImport("libobjc.dylib")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial nint objc_allocateClassPair(
            nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);

        [LibraryImport("libobjc.dylib")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial void objc_registerClassPair(nint cls);

        [LibraryImport("libobjc.dylib")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool class_addMethod(
            nint cls, nint sel, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

        [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static unsafe partial nint objc_msgSend_ret_nint(void* receiver, nint selector);

        [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static unsafe partial nint objc_msgSend_ret_ptr(void* receiver, nint selector);

        // -[NSAppleEventDescriptor paramDescriptorForKeyword:(FourCharCode)] — one uint arg.
        [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static unsafe partial nint objc_msgSend_descForKeyword(
            void* receiver, nint selector, uint keyword);

        // -[NSAppleEventManager setEventHandler:andSelector:forEventClass:andEventID:] — id, SEL, two uints.
        [LibraryImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static unsafe partial void objc_msgSend_setHandler(
            void* receiver, nint selector, void* handler, nint andSelector, uint eventClass, uint eventId);
    }
}
