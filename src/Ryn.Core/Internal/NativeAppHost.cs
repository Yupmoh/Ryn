using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ryn.Interop;

namespace Ryn.Core.Internal;

/// <summary>
/// Owns the one-per-process native saucer application and its blocking event loop, the UI-thread marshalling
/// primitives, the global scheme registry, and the registry of live windows. Separated out of
/// <see cref="RynWindow"/> so a single application/loop can host N windows: everything genuinely per-window
/// (the native window/webview, IPC token, scheme handlers, events) stays on <see cref="RynWindow"/>, while
/// everything that is one-per-process (the app handle, the run loop, the post/marshal helpers, the pre-ready
/// queue) lives here. saucer natively supports N windows on one application, so the split is a separation of
/// concerns rather than a rearchitecture.
/// </summary>
internal sealed unsafe class NativeAppHost : IDisposable
{
    // App-global options. For now this also carries the main window's per-window settings; the app is created
    // from its ApplicationId and the main window is built from the whole instance.
    private readonly RynOptions _mainWindowOptions;

    private saucer_application* _app;
    private void* _selfHandle;

    // GCHandles posted to saucer's UI thread via RunOnUi. RunPostedAppAction normally claims-and-frees each
    // one; tracking them here lets DisposeNative reclaim any the run loop dropped at shutdown (INT-10).
    private readonly ConcurrentDictionary<nint, byte> _postedHandles = new();

    // Main-thread work submitted (via IMainThreadDispatcher) before the native application exists. saucer's
    // post requires a live _app, so RunOnUi drops actions while _app == null. Buffer them here and drain them
    // — in submission order, on the UI thread — once the loop comes up (Cluster C / INT-02). Guarded by
    // _preReadyGate; nulled after the drain so later posts go straight to RunOnUi.
    private readonly object _preReadyGate = new();
    private List<Action>? _preReadyQueue = [];
    private volatile bool _appReady;

    private volatile bool _disposed;

    // Disposed after the saucer run loop exits so a late token cancellation can't quit a freed _app (PAP-14).
    private CancellationTokenRegistration _quitRegistration;

    // Live windows, keyed by id. The main window is created in OnReady; the loop quits when the registry
    // empties. Guarded by _windowsGate because windows can be enumerated from teardown paths while the UI
    // thread mutates the registry.
    private readonly object _windowsGate = new();
    private readonly Dictionary<int, RynWindow> _windows = [];
    private int _nextWindowId;
    private RynWindow? _mainWindow;

    // Schemes already registered with the engine. saucer_webview_register_scheme is process-global and must
    // run once, before the first webview is created; a double-register is a silent no-op but we guard anyway.
    private readonly HashSet<string> _registeredSchemes = new(StringComparer.OrdinalIgnoreCase);

    internal NativeAppHost(RynOptions mainWindowOptions) => _mainWindowOptions = mainWindowOptions;

    /// <summary>The native application handle. Null until <see cref="Run"/> has created it.</summary>
    internal saucer_application* App => _app;

    /// <summary>True while the native application exists and the host has not begun teardown.</summary>
    internal bool IsRunning => _app != null && !_disposed;

    /// <summary>The first (main) window, once created in OnReady. Null before then.</summary>
    internal RynWindow? MainWindow
    {
        get { lock (_windowsGate) return _mainWindow; }
    }

    /// <summary>A point-in-time snapshot of the live windows.</summary>
    internal IReadOnlyList<RynWindow> Windows
    {
        get { lock (_windowsGate) return [.. _windows.Values]; }
    }

    /// <summary>Fires (on the UI thread, inside OnReady) with the native app handle once it exists.</summary>
    internal Action<nint>? NativeReady { get; set; }

    /// <summary>Fires (on the UI thread, inside OnReady) once the main window is fully initialized and live.</summary>
    internal Action<RynWindow>? MainWindowCreated { get; set; }

    /// <summary>The IPC command handler applied to each window's webview. Assigned before <see cref="Run"/>.</summary>
    internal CommandDispatchHandler? CommandHandler { get; set; }

    internal void Run(CancellationToken cancellationToken)
    {
        NativeLibraryResolver.Register();
        Span<byte> idBuf = stackalloc byte[256];
        var appIdStr = Utf8String.Create(_mainWindowOptions.ApplicationId, idBuf);
        var appOpts = Saucer.saucer_application_options_new(appIdStr.Pointer);
        appIdStr.Dispose();
        int error = 0;
        _app = Saucer.saucer_application_new(appOpts, &error);
        Saucer.saucer_application_options_free(appOpts);
        if (_app == null) throw new InvalidOperationException($"Failed to create saucer application (error code: {error})");
        if (cancellationToken.CanBeCanceled)
            _quitRegistration = cancellationToken.Register(() => { if (_app != null) Saucer.saucer_application_quit(_app); });
        _selfHandle = NativeCallbackHelper.Alloc(this);
        var exitCode = Saucer.saucer_application_run(_app,
            (delegate* unmanaged[Cdecl]<saucer_application*, void*, void>)&OnReady,
            (delegate* unmanaged[Cdecl]<saucer_application*, void*, void>)&OnFinish,
            _selfHandle);
        _ = exitCode;
        // Dispose the cancellation registration before freeing _app: the registration is only meaningful while
        // the loop runs, and disposing it here closes the post-shutdown closure pin and the TOCTOU window where
        // a late cancellation could call saucer_application_quit on the freed _app (PAP-14).
        _quitRegistration.Dispose();
        _quitRegistration = default;
        DisposeNative();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnReady(saucer_application* app, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<NativeAppHost>(userdata);
        NativeGuard.Invoke("NativeAppHost.OnReady", () =>
        {
            try
            {
                self.InitializeReady();
            }
            catch
            {
                // Startup failed (window/webview creation, local-server bind, state-dir creation, …). Quit the
                // saucer loop so Run() can return and dispose cleanly, complete any open per-window close TCS so
                // a WaitForCloseAsync awaiter is released, then rethrow into NativeGuard, which routes the
                // exception to RynApplication's UnhandledException surface (ARC-01/INT-01/PAP-09).
                if (self._app != null) Saucer.saucer_application_quit(self._app);
                self.CompleteAllCloseSignals();
                throw;
            }
        });
    }

    /// <summary>
    /// Brings the application up on the UI thread: publishes the native handle, creates and initializes the
    /// main window, announces it, then drains any pre-ready main-thread work. Runs inside saucer's OnReady.
    /// </summary>
    private void InitializeReady()
    {
        NativeReady?.Invoke((nint)_app);

        var main = CreateWindowCore(_mainWindowOptions);
        if (CommandHandler is not null) main.SetCommandHandler(CommandHandler);
        main.InitializeNative();

        // Publish the fully-initialized window only after InitializeNative, so anything the announcement wakes
        // up (a deferred IRynWebView flush, a queued IMainThreadDispatcher action) finds a live webview.
        MainWindowCreated?.Invoke(main);

        // We're on the UI thread and the app exists. Drain any main-thread work a plugin backend buffered via
        // IMainThreadDispatcher before the loop was up (Cluster C / INT-02), in submission order.
        FlushPreReadyQueue();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFinish(saucer_application* app, void* userdata)
    {
        var self = NativeCallbackHelper.Resolve<NativeAppHost>(userdata);
        // The loop has ended; defensively complete any per-window close TCS still open so awaiters unblock.
        NativeGuard.Invoke("NativeAppHost.OnFinish", () => self.CompleteAllCloseSignals());
    }

    /// <summary>Constructs and registers a window (managed only; native creation is the caller's next step).</summary>
    private RynWindow CreateWindowCore(RynOptions options)
    {
        lock (_windowsGate)
        {
            var id = ++_nextWindowId;
            var window = new RynWindow(this, id, options);
            _windows[id] = window;
            _mainWindow ??= window;
            return window;
        }
    }

    /// <summary>
    /// Removes a closed window from the registry and quits the loop only when the last window has gone (the
    /// key multi-window difference from the old hardcoded quit-on-any-close). Runs on the UI thread, called
    /// from <see cref="RynWindow"/>'s native CLOSED handler.
    /// </summary>
    internal void OnWindowClosedInternal(RynWindow window)
    {
        bool empty;
        lock (_windowsGate)
        {
            _windows.Remove(window.Id);
            empty = _windows.Count == 0;
        }
        if (empty && _app != null)
            Saucer.saucer_application_quit(_app);
    }

    private void CompleteAllCloseSignals()
    {
        foreach (var window in Windows)
            window.CompleteCloseSignal();
    }

    /// <summary>
    /// Requests an orderly shutdown from any thread: closes every live window on the UI thread so each runs its
    /// native CLOSE/CLOSED path (window-state save), and the last close quits the loop. Falls back to quitting
    /// the application directly if there is no window yet, so a shutdown request is never silently dropped.
    /// </summary>
    internal void RequestShutdown() => PostToUi(() =>
    {
        var windows = Windows;
        if (windows.Count == 0)
        {
            if (_app != null) Saucer.saucer_application_quit(_app);
            return;
        }
        foreach (var window in windows)
            window.CloseNativeWindow();
    });

    /// <summary>Quits the saucer loop. Must be called on the UI thread.</summary>
    internal void Quit()
    {
        if (_app != null) Saucer.saucer_application_quit(_app);
    }

    /// <summary>
    /// Registers a custom URL scheme with the engine, exactly once across the process. saucer's
    /// register_scheme is global and must run before the first webview is created; the <see cref="HashSet{T}"/>
    /// guard makes a repeat call (e.g. the reserved "ryn" scheme, or a scheme shared across windows) a no-op.
    /// </summary>
    internal void RegisterScheme(string scheme)
    {
        if (string.IsNullOrEmpty(scheme) || !_registeredSchemes.Add(scheme))
            return;
        Span<byte> buf = stackalloc byte[64];
        var str = Utf8String.Create(scheme, buf);
        Saucer.saucer_webview_register_scheme(str.Pointer);
        str.Dispose();
    }

    /// <summary>
    /// Marshals a native operation onto saucer's UI thread. Native window/AppKit calls are not thread-safe, so
    /// mutating operations are posted to the application loop. A no-op before the app exists or after teardown.
    /// </summary>
    internal void RunOnUi(Action action)
    {
        if (_disposed || _app == null)
            return;

        var data = (nint)NativeCallbackHelper.Alloc(new PostedAppAction(this, action));
        // Track the handle so DisposeNative can reclaim it if saucer drops the posted callback at shutdown
        // (INT-10). RunPostedAppAction claims-and-removes it before freeing, so the two never double-free.
        _postedHandles[data] = 0;
        Saucer.saucer_application_post(
            _app,
            (delegate* unmanaged[Cdecl]<void*, void>)&RunPostedAppAction,
            (void*)data);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunPostedAppAction(void* userdata)
    {
        var handle = (nint)userdata;
        var payload = NativeCallbackHelper.Resolve<PostedAppAction>(userdata);
        // Claim the handle so a DisposeNative racing at shutdown can't also free it; whoever removes it frees.
        if (!payload.Owner._postedHandles.TryRemove(handle, out _))
            return;
        NativeGuard.Invoke("NativeAppHost.RunOnUi", payload.Action);
        NativeCallbackHelper.Free(userdata);
    }

    /// <summary>Pairs a UI-thread action with its owning host so the static native callback can deregister the
    /// tracked GCHandle (INT-10) before running and freeing it.</summary>
    private sealed record PostedAppAction(NativeAppHost Owner, Action Action);

    /// <summary>
    /// True when the caller is already on saucer's UI thread, so UI-thread work can run inline rather than be
    /// posted. Backed by <c>saucer_application_thread_safe</c>. False before the app exists or after teardown.
    /// </summary>
    internal bool IsOnUiThread => _app != null && !_disposed && Saucer.saucer_application_thread_safe(_app) != 0;

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread, used by <see cref="IMainThreadDispatcher"/> to fence
    /// native UI calls made from worker threads (Cluster C / INT-02). Runs inline when already on the UI thread;
    /// queues it when the native app is not up yet (drained in submission order once the loop starts); otherwise
    /// posts via saucer. Safe to call from any thread. A no-op after disposal.
    /// </summary>
    internal void PostToUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed)
            return;

        if (IsOnUiThread)
        {
            NativeGuard.Invoke("NativeAppHost.PostToUi", action);
            return;
        }

        // Native app not up yet → buffer until the loop drains the queue on the UI thread. Double-check
        // _appReady under the lock to close the race with FlushPreReadyQueue (which flips it while holding the
        // gate): if the app became ready between our volatile read and taking the lock, fall through to post.
        if (!_appReady)
        {
            lock (_preReadyGate)
            {
                if (!_appReady && _preReadyQueue is { } queue)
                {
                    queue.Add(action);
                    return;
                }
            }
        }

        RunOnUi(action);
    }

    /// <summary>
    /// Like <see cref="PostToUi"/> but returns a Task that completes when the action has run on the UI thread
    /// (or faults if it throws). Completes inline when already on the UI thread; completes without running if
    /// the host has been disposed. Used by <see cref="IMainThreadDispatcher.InvokeAsync"/>.
    /// </summary>
    internal Task InvokeOnUiAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed)
            return Task.CompletedTask;

        if (IsOnUiThread)
        {
            try { action(); return Task.CompletedTask; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return Task.FromException(ex); }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PostToUi(() =>
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>
    /// Drains any work buffered by <see cref="PostToUi"/> before the native app existed, in submission order,
    /// on the UI thread. Flips <c>_appReady</c> under the gate so a concurrent <see cref="PostToUi"/> either
    /// lands in the queue we snapshot here or takes the post path — never lost, never run twice.
    /// </summary>
    private void FlushPreReadyQueue()
    {
        List<Action>? pending;
        lock (_preReadyGate)
        {
            pending = _preReadyQueue;
            _preReadyQueue = null;
            _appReady = true;
        }

        if (pending is null)
            return;

        // We're on the UI thread here (this runs inside saucer's OnReady), so run inline through the same
        // NativeGuard barrier the posted path uses.
        foreach (var action in pending)
            NativeGuard.Invoke("NativeAppHost.PostToUi", action);
    }

    /// <summary>
    /// Frees native handles after the run loop returns: each window's webview/window, the leftover posted
    /// GCHandles saucer never drained (INT-10), then the application handle. Called from <see cref="Run"/>.
    /// </summary>
    private void DisposeNative()
    {
        foreach (var window in Windows)
            window.DisposeNative();
        lock (_windowsGate)
        {
            _windows.Clear();
            _mainWindow = null;
        }

        // Reclaim any GCHandles saucer never drained — RunPostedAppAction is what normally frees them, so
        // without this they leak (INT-10). The TryRemove claim means a callback that did run can't be
        // double-freed here.
        foreach (var handle in _postedHandles.Keys)
        {
            if (_postedHandles.TryRemove(handle, out _))
                NativeCallbackHelper.Free(handle);
        }

        if (_selfHandle != null) { NativeCallbackHelper.Free(_selfHandle); _selfHandle = null; }
        if (_app != null) { Saucer.saucer_application_free(_app); _app = null; }
    }

    /// <summary>
    /// Early/abnormal managed teardown (disposing without ever running, or after a throw). Disposes each
    /// window's managed resources and cancels its close TCS. Native handle frees happen inside the run loop
    /// (<see cref="DisposeNative"/>) when the loop runs, so this path only manages the never-ran case.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Defensive: Run() disposes the registration after the loop exits, but disposing here too (a no-op on a
        // default registration) ensures a token cancellation after Dispose can never quit a freed app (PAP-14).
        _quitRegistration.Dispose();
        foreach (var window in Windows)
            window.Dispose();
    }
}
