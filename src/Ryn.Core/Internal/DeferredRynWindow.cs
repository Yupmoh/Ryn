namespace Ryn.Core.Internal;

/// <summary>
/// An <see cref="IRynWindow"/> that can be injected into any service at any time — including services
/// constructed before the native window exists. Members forward to the real window once it is available;
/// event subscriptions made early are attached when the window becomes ready. Using a member before the
/// window exists throws a clear error (you cannot, e.g., show a window that has not been created yet).
/// </summary>
/// <remarks>
/// A pre-ready subscribe queues an attach via <see cref="RynWindowAccessor.OnReady"/>, which hands back a
/// token. We keep that token next to the handler so a pre-ready <em>unsubscribe</em> can cancel the matching
/// still-queued attach via <see cref="RynWindowAccessor.CancelOnReady"/> instead of being a silent no-op
/// (PAP-15). Without this, subscribe-then-unsubscribe before <c>RunAsync</c> leaked a permanent subscription:
/// the queued attach still fired at the accessor's window-publish while the unsubscribe found no live window.
/// Once the window is ready, the token is stale (0) and removals forward straight through to the live window.
/// </remarks>
internal sealed class DeferredRynWindow(RynWindowAccessor accessor) : IRynWindow
{
    // Pending pre-ready subscriptions, keyed by (event, handler) so an unsubscribe can find the exact queued
    // attach and its OnReady token. Stored as a list (not a dictionary) to preserve a true multiset: the same
    // handler may be subscribed more than once and each pending attach must be cancellable independently.
    // Guarded by _gate because the deferred window can be touched from any thread (e.g. an IPC command on a
    // thread-pool thread subscribing to a window event) before the window is published.
    private readonly List<PendingSub> _pending = [];
    private readonly object _gate = new();

    private sealed record PendingSub(WindowEvent Event, Delegate Handler, long Token);

    private enum WindowEvent
    {
        Closing,
        Closed,
        Resized,
        Focused,
        Blurred,
        Moved,
        StateChanged,
        ThemeChanged,
    }

    private RynWindow Live => accessor.Window
        ?? throw new InvalidOperationException(
            "The window is not available yet. IRynWindow can be injected anywhere, but its members are only usable after RunAsync has started.");

    public int Id => Live.Id;
    public string Title { get => Live.Title; set => Live.Title = value; }
    public int Width { get => Live.Width; set => Live.Width = value; }
    public int Height { get => Live.Height; set => Live.Height = value; }
    public bool Resizable { get => Live.Resizable; set => Live.Resizable = value; }
    public AppTheme Theme => Live.Theme;

    public ValueTask ShowAsync(CancellationToken cancellationToken = default) => Live.ShowAsync(cancellationToken);
    public ValueTask HideAsync(CancellationToken cancellationToken = default) => Live.HideAsync(cancellationToken);
    public ValueTask CloseAsync(CancellationToken cancellationToken = default) => Live.CloseAsync(cancellationToken);
    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default) => Live.WaitForCloseAsync(cancellationToken);
    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => Live.NavigateAsync(url, cancellationToken);
    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default) => Live.EvaluateJavaScriptAsync(script, cancellationToken);
    public void Close() => Live.Close();
    public void Minimize() => Live.Minimize();
    public void ToggleMaximize() => Live.ToggleMaximize();
    public void StartDrag() => Live.StartDrag();
    public void StartResize(WindowEdge edge) => Live.StartResize(edge);

    // --- Pre-ready subscription buffering ---------------------------------------------------------------

    /// <summary>
    /// Subscribes <paramref name="handler"/> to <paramref name="which"/>. If the window is already live the
    /// accessor runs <paramref name="attach"/> synchronously (token 0). Otherwise the attach is queued and we
    /// remember its token so a later pre-ready unsubscribe can cancel it.
    /// </summary>
    private void AddHandler(WindowEvent which, Delegate? handler, Action<RynWindow> attach)
    {
        if (handler is null) return;

        // OnReady runs attach inline when the window already exists; queue it (returning a token) otherwise.
        var token = accessor.OnReady(attach);
        if (token == 0) return; // Attached straight through to the live window — nothing to track.

        lock (_gate)
            _pending.Add(new PendingSub(which, handler, token));
    }

    /// <summary>
    /// Unsubscribes <paramref name="handler"/>. Before the window is ready this cancels the matching queued
    /// attach via the accessor token (PAP-15); after it is ready the removal forwards to the live window.
    /// </summary>
    private void RemoveHandler(WindowEvent which, Delegate? handler, Action<RynWindow> detach)
    {
        if (handler is null) return;

        long token = 0;
        lock (_gate)
        {
            // Find the most recent matching still-pending attach (LIFO mirrors how -= typically unwinds +=).
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].Event == which && _pending[i].Handler.Equals(handler))
                {
                    token = _pending[i].Token;
                    _pending.RemoveAt(i);
                    break;
                }
            }
        }

        // If we found a queued attach, try to cancel it before the window publishes. CancelOnReady returns false
        // if it already fired (window came up between our find and cancel) — in that race the handler is now live,
        // so fall through and detach it from the real window.
        if (token != 0 && accessor.CancelOnReady(token))
            return;

        if (accessor.Window is { } live)
            detach(live);
    }

    // --- Events -----------------------------------------------------------------------------------------

    public event EventHandler<WindowClosingEventArgs>? Closing
    {
        add => AddHandler(WindowEvent.Closing, value, w => w.Closing += value);
        remove => RemoveHandler(WindowEvent.Closing, value, w => w.Closing -= value);
    }

    public event EventHandler? Closed
    {
        add => AddHandler(WindowEvent.Closed, value, w => w.Closed += value);
        remove => RemoveHandler(WindowEvent.Closed, value, w => w.Closed -= value);
    }

    public event EventHandler<WindowResizedEventArgs>? Resized
    {
        add => AddHandler(WindowEvent.Resized, value, w => w.Resized += value);
        remove => RemoveHandler(WindowEvent.Resized, value, w => w.Resized -= value);
    }

    public event EventHandler? Focused
    {
        add => AddHandler(WindowEvent.Focused, value, w => w.Focused += value);
        remove => RemoveHandler(WindowEvent.Focused, value, w => w.Focused -= value);
    }

    public event EventHandler? Blurred
    {
        add => AddHandler(WindowEvent.Blurred, value, w => w.Blurred += value);
        remove => RemoveHandler(WindowEvent.Blurred, value, w => w.Blurred -= value);
    }

    public event EventHandler<WindowMovedEventArgs>? Moved
    {
        add => AddHandler(WindowEvent.Moved, value, w => w.Moved += value);
        remove => RemoveHandler(WindowEvent.Moved, value, w => w.Moved -= value);
    }

    public event EventHandler<WindowStateChangedEventArgs>? StateChanged
    {
        add => AddHandler(WindowEvent.StateChanged, value, w => w.StateChanged += value);
        remove => RemoveHandler(WindowEvent.StateChanged, value, w => w.StateChanged -= value);
    }

    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged
    {
        add => AddHandler(WindowEvent.ThemeChanged, value, w => w.ThemeChanged += value);
        remove => RemoveHandler(WindowEvent.ThemeChanged, value, w => w.ThemeChanged -= value);
    }
}
