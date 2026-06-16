using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ryn.Core.Internal;

namespace Ryn.Core;

/// <summary>The main entry point for a Ryn application, managing the window lifecycle and plugin initialization.</summary>
public sealed partial class RynApplication : IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RynApplication> _logger;
    private readonly List<IRynPlugin> _plugins = [];
    private NativeAppHost? _host;
    private volatile bool _running;
    private bool _disposed;

    // The currently-running instance, so the native deep-link path (DeepLinkHandler's macOS Apple-Event
    // handler / single-instance forwarder) can route an inbound URL to the live app. Guarded by a lock so a
    // late callback that fires during teardown sees a consistent value rather than a torn reference.
    private static readonly object s_runningLock = new();
    private static RynApplication? s_running;

    /// <summary>Fires when the app is opened via a registered deep link URL.</summary>
    public event EventHandler<DeepLinkEventArgs>? DeepLinkReceived;

    internal RynApplication(IServiceProvider services)
    {
        _services = services;
        _logger = services.GetService<ILogger<RynApplication>>() ?? NullLogger<RynApplication>.Instance;
    }

    /// <summary>The dependency injection service provider for this application.</summary>
    public IServiceProvider Services => _services;

    /// <summary>The main application window. Only available after <see cref="RunAsync"/> has been called.</summary>
    public IRynWindow Window => _host?.MainWindow ?? throw new InvalidOperationException("Application is not running");

    /// <summary>The main application webview. Only available after <see cref="RunAsync"/> has been called.</summary>
    public IRynWebView WebView => _host?.MainWindow?.WebView ?? throw new InvalidOperationException("Application is not running");

    /// <summary>Creates a new application builder with default options.</summary>
    public static RynApplicationBuilder CreateBuilder() => new(programmaticOptions: null);

    /// <summary>Creates a new application builder with the specified options.</summary>
    public static RynApplicationBuilder CreateBuilder(RynOptions options) => new(options);

    /// <summary>
    /// Initializes plugins, creates the window, and runs the native event loop until the window is closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This call <b>blocks the calling thread</b> for the lifetime of the application: it does not return
    /// until the window closes or <paramref name="cancellationToken"/> is signalled. Although the signature
    /// is asynchronous, the returned <see cref="ValueTask"/> is already completed when control returns — the
    /// native event loop is pumped synchronously on this thread (an AppKit/GTK requirement), so there is no
    /// work left to await. Treat it like a blocking <c>Main</c>; do not expect it to yield.
    /// </para>
    /// <para>
    /// It must be called from the application's <b>main thread</b>: <c>[STAThread] Main</c> on Windows, and
    /// thread 0 (the thread that ran <c>main</c>) on macOS and Linux. Calling it from a worker thread throws
    /// <see cref="InvalidOperationException"/> rather than letting the native toolkit crash later.
    /// </para>
    /// </remarks>
    public ValueTask RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (OperatingSystem.IsWindows() && Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException(
                "Ryn requires [STAThread] on the entry point for Windows. " +
                "Use '[STAThread] static void Main()' instead of top-level statements or async Main.");
        }

        // macOS AppKit and Linux GTK both require their event loop to run on the process main thread (the
        // thread that entered main / thread 0). pthread_main_np() returns non-zero there on both platforms.
        // Calling RunAsync off that thread otherwise faults deep inside the native toolkit with no actionable
        // message; convert that undefined behavior into a clear throw, mirroring the Windows STA guard above.
        if ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) && NativeThread.pthread_main_np() == 0)
        {
            throw new InvalidOperationException(
                "RunAsync must be called from the application's main thread (thread 0). " +
                "On macOS and Linux the native UI event loop can only run on the main thread. " +
                "Call it directly from Main rather than from a Task, worker thread, or thread-pool callback.");
        }

        Log.Starting(_logger);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Capture the handler so it can be unsubscribed in finally: otherwise it leaks, stacks on a second
        // RunAsync, and — because `using` disposes cts on return — a late Ctrl+C would call Cancel() on a
        // disposed CTS (ObjectDisposedException).
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        };
        Console.CancelKeyPress += cancelHandler;

        UnhandledExceptionEventHandler? domainHandler = null;
        EventHandler<UnobservedTaskExceptionEventArgs>? taskHandler = null;

        try
        {
            foreach (var plugin in _plugins)
            {
                try
                {
#pragma warning disable CA1849 // Intentional sync-over-async: no event loop exists yet, so no deadlock risk
                    plugin.InitializeAsync(cts.Token).AsTask().GetAwaiter().GetResult();
#pragma warning restore CA1849
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Log at Error (not Debug.WriteLine, which is a no-op in Release) so a half-initialized
                    // plugin — e.g. a shell allowlist that failed to resolve — is never silent.
                    Log.PluginInitFailed(_logger, plugin.Name, ex);
                }
            }

            var options = _services.GetRequiredService<RynOptions>();

            // Opt-in process-global exception net: log and surface AppDomain / unobserved-task exceptions
            // via the UnhandledException event so apps can install a crash logger.
            if (options.CaptureUnhandledExceptions)
            {
                domainHandler = (_, e) => { if (e.ExceptionObject is Exception ex) RaiseUnhandled(ex); };
                taskHandler = (_, e) => { RaiseUnhandled(e.Exception); e.SetObserved(); };
                AppDomain.CurrentDomain.UnhandledException += domainHandler;
                TaskScheduler.UnobservedTaskException += taskHandler;
            }

            // Route exceptions caught at any native-callback boundary (the [UnmanagedCallersOnly] bodies in
            // RynWindow/RynWebView, wrapped via NativeGuard) into this app's unhandled-exception surface so the
            // UnhandledException event and logger see them. Always wired — unlike the opt-in AppDomain net above,
            // a managed throw escaping a native callback is never something to swallow silently. Cleared in
            // finally so a second RunAsync (or another instance) does not inherit a stale sink.
            NativeGuard.UnhandledSink = RaiseUnhandled;

            if (options.DeepLinkSchemes.Count > 0)
            {
                foreach (var scheme in options.DeepLinkSchemes)
                    DeepLinkHandler.RegisterScheme(scheme, options.Title);

                var deepLink = DeepLinkHandler.CheckStartupArgs(options.DeepLinkSchemes);
                if (deepLink is not null)
                    RaiseDeepLink(deepLink);
            }

            var host = new NativeAppHost(options);
            _host = host;

            // Wire IPC command dispatcher if registered. The host applies it to the main window's webview
            // during OnReady, before the window is initialized.
            host.CommandHandler = _services.GetService<CommandDispatchHandler>();

            var accessor = _services.GetRequiredService<RynWindowAccessor>();
            var nativeAccessor = _services.GetRequiredService<NativeApplicationAccessor>();
            host.NativeReady = handle => nativeAccessor.ApplicationHandle = handle;
            // Publish the live main window once it is fully initialized (inside OnReady, on the UI thread) so
            // deferred IRynWindow/IRynWebView injections and queued main-thread work resolve against it.
            host.MainWindowCreated = window => accessor.Window = window;

            Log.Running(_logger);

            // Publish the live instance so the native deep-link path can deliver inbound URLs to it. Marked
            // running before the loop starts and cleared in finally so RaiseDeepLink never targets a window
            // that has begun (or finished) tearing down.
            lock (s_runningLock) { s_running = this; }
            _running = true;

            try
            {
                host.Run(cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
            {
                Log.EventLoopFailed(_logger, ex);
                RaiseUnhandled(ex);
                throw;
            }

            Log.ShuttingDown(_logger);
        }
        finally
        {
            _running = false;
            lock (s_runningLock) { if (ReferenceEquals(s_running, this)) s_running = null; }
            if (NativeGuard.UnhandledSink == RaiseUnhandled) NativeGuard.UnhandledSink = null;
            Console.CancelKeyPress -= cancelHandler;
            if (domainHandler is not null) AppDomain.CurrentDomain.UnhandledException -= domainHandler;
            if (taskHandler is not null) TaskScheduler.UnobservedTaskException -= taskHandler;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Raises <see cref="DeepLinkReceived"/> for <paramref name="url"/>. The entry point used by the native
    /// deep-link path (the macOS Apple-Event handler and the single-instance forwarder, both owned by
    /// <see cref="DeepLinkHandler"/>) to deliver a URL to the running app. Wrapped in the native-boundary
    /// exception barrier so a throwing subscriber never unwinds across the Apple-Event callback.
    /// </summary>
    internal void RaiseDeepLink(Uri url)
        => NativeGuard.Invoke(nameof(RaiseDeepLink), () =>
            DeepLinkReceived?.Invoke(this, new DeepLinkEventArgs { Url = url }));

    /// <summary>
    /// Routes an inbound deep-link URL to the currently-running application instance, if any. Returns
    /// <see langword="true"/> when a live instance accepted it. Called by <see cref="DeepLinkHandler"/> from
    /// the native activation path; safe to call from any thread.
    /// </summary>
    internal static bool TryDeliverDeepLink(Uri url)
    {
        RynApplication? app;
        lock (s_runningLock) { app = s_running; }
        if (app is null || !app._running) return false;
        app.RaiseDeepLink(url);
        return true;
    }

    /// <summary>
    /// Raised when an exception escapes the event loop, or (when <see cref="RynOptions.CaptureUnhandledExceptions"/>
    /// is enabled) for AppDomain-unhandled and unobserved-task exceptions. Use it to install a crash logger.
    /// </summary>
    public event EventHandler<RynUnhandledExceptionEventArgs>? UnhandledException;

    private void RaiseUnhandled(Exception exception)
    {
        Log.UnhandledException(_logger, exception);
        try { UnhandledException?.Invoke(this, new RynUnhandledExceptionEventArgs(exception)); }
        catch (Exception handlerEx) when (handlerEx is not OutOfMemoryException) { }
    }

    /// <summary>Synchronous convenience wrapper for <see cref="RunAsync"/>. Blocks the calling thread.</summary>
    public void Run(CancellationToken cancellationToken = default)
    {
#pragma warning disable CA2012 // Intentional sync-over-async: convenience wrapper for [STAThread] Main
        RunAsync(cancellationToken).GetAwaiter().GetResult();
#pragma warning restore CA2012
    }

    /// <summary>
    /// Requests an orderly shutdown without calling <see cref="Environment.Exit(int)"/>. Closes the window on
    /// the UI thread so the native event loop unwinds, <see cref="RunAsync"/> returns, and the normal disposal
    /// chain runs (plugins → webview → window → app, including window-state save). This is the supported way for
    /// an in-app component — e.g. an auto-updater that must relaunch the app — to stop cleanly instead of
    /// hard-exiting from a background thread and skipping disposal (PAP-06).
    /// </summary>
    /// <remarks>
    /// Safe to call from any thread and idempotent. Returns immediately; shutdown proceeds asynchronously on the
    /// UI thread. A caller that needs to block until the app has fully stopped should await
    /// <see cref="IRynWindow.WaitForCloseAsync"/> on <see cref="Window"/>. A no-op if the app is not running.
    /// </remarks>
    public void RequestShutdown()
    {
        if (_disposed)
            return;

        // Closing every window drives the native CLOSE/CLOSED path → the last close quits the loop → RunAsync
        // returns. If the host does not exist yet (RequestShutdown raced ahead of RunAsync creating it) there is
        // nothing running to stop, so this is a no-op — RunAsync has not begun the loop and will exit on its own.
        _host?.RequestShutdown();
    }

    internal void AddPlugin(IRynPlugin plugin) => _plugins.Add(plugin);

    /// <summary>Disposes the plugins, window, and service provider.</summary>
    /// <remarks>
    /// Teardown order is <b>plugins → webview → window → app</b> (Cluster E). Plugins are torn down first so
    /// they stop issuing IPC/native calls before the surfaces they target go away; the window's own
    /// <see cref="RynWindow.Dispose"/> then frees the webview before the native window/application handles, so
    /// no native handle is freed while something still references it.
    /// <para>
    /// This is the <em>abnormal/early</em> teardown path (e.g. disposing without ever running, or after a
    /// throw). The normal-shutdown path frees native handles inside the run loop itself
    /// (<c>RynWindow.DisposeNative</c> after the loop exits), so disposing here while the loop is still live
    /// would race that teardown and risk freeing handles mid-flight. Guard against it: while the loop is still
    /// running we skip the native window teardown entirely and let the loop free its own handles as it unwinds
    /// (the supported way to stop a running app is to close the window or cancel <see cref="RunAsync"/>'s
    /// token). Managed plugin/service disposal still proceeds either way.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 1. Plugins first: stop them before the window/webview they may call into is gone.
        foreach (var plugin in _plugins)
        {
            try
            {
                if (plugin is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (plugin is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A plugin that throws on teardown must not abort the rest of the ordered teardown,
                // otherwise the window and native handles leak.
                Log.PluginDisposeFailed(_logger, plugin.Name, ex);
            }
        }

        // 2. Application host (which internally disposes each window's webview → native window, then frees the
        //    native application, in that order). Only when the loop is NOT live: while RunAsync's event loop is
        //    still pumping it owns those native handles and frees them itself as it unwinds (NativeAppHost's
        //    DisposeNative after the loop exits), so freeing them here would race that teardown and risk freeing
        //    a live handle mid-flight. In that case leave the host alone — closing the windows or cancelling
        //    RunAsync's token is the supported way to stop a running app — and only manage plugin/service disposal.
        var host = _host;
        if (!_running)
        {
            _host = null;
            host?.Dispose();
        }

        // 3. App/services last.
        if (_services is IAsyncDisposable serviceDisposable)
        {
            await serviceDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_services is IDisposable serviceSyncDisposable)
        {
            serviceSyncDisposable.Dispose();
        }
    }

    /// <summary>
    /// Thin P/Invoke shim for the C runtime's main-thread predicate. <c>pthread_main_np</c> returns a non-zero
    /// value on the process main thread and 0 elsewhere; it is exported from <c>libSystem</c> on macOS and from
    /// <c>libc</c> on Linux (glibc). AOT-safe: a plain extern call with blittable return, no reflection.
    /// </summary>
    private static partial class NativeThread
    {
        internal static int pthread_main_np()
        {
            if (OperatingSystem.IsMacOS()) return MacMainNp();
            if (OperatingSystem.IsLinux()) return LinuxMainNp();
            return 0;
        }

        [LibraryImport("libSystem", EntryPoint = "pthread_main_np")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [System.Runtime.Versioning.SupportedOSPlatform("macos")]
        private static partial int MacMainNp();

        [LibraryImport("libc", EntryPoint = "pthread_main_np")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        private static partial int LinuxMainNp();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Ryn application starting")]
        public static partial void Starting(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Ryn application running")]
        public static partial void Running(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Ryn application shutting down")]
        public static partial void ShuttingDown(ILogger logger);

        [LoggerMessage(Level = LogLevel.Error, Message = "Plugin '{pluginName}' failed to initialize")]
        public static partial void PluginInitFailed(ILogger logger, string pluginName, Exception exception);

        [LoggerMessage(Level = LogLevel.Error, Message = "Plugin '{pluginName}' failed to dispose")]
        public static partial void PluginDisposeFailed(ILogger logger, string pluginName, Exception exception);

        [LoggerMessage(Level = LogLevel.Critical, Message = "Ryn event loop terminated with an unhandled exception")]
        public static partial void EventLoopFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled exception")]
        public static partial void UnhandledException(ILogger logger, Exception exception);
    }
}
