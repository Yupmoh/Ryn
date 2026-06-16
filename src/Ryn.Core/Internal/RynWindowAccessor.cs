namespace Ryn.Core.Internal;

/// <summary>
/// Holds the live <see cref="RynWindow"/> once it exists and lets services injected before that point queue
/// work (and event subscriptions) to run when it becomes ready. The window is created and published on the
/// app's main/UI thread, but the deferred <see cref="IRynWindow"/> handed out by DI can be touched from any
/// thread (e.g. an IPC command running on a thread-pool thread that subscribes to a window event), so every
/// access to the shared mutable state is serialized under <see cref="_gate"/>. User callbacks are always
/// invoked outside the lock to avoid re-entrancy deadlocks if a handler subscribes again from within itself.
/// </summary>
internal sealed class RynWindowAccessor
{
    private readonly object _gate = new();

    // Queued ready-callbacks. Each entry is keyed by the token returned from OnReady so a pre-ready
    // unsubscribe can cancel its matching queued add instead of being a silent no-op (DeferredRynWindow's
    // event 'remove' accessors call CancelOnReady before the window exists; DeferredRynWebView uses its own
    // pending-handler multiset because its webview is created after this queue drains). Insertion order is
    // preserved by appending; cancellation marks the slot null, and null slots are skipped when the window is
    // published (the whole list is then cleared, so no separate compaction is needed).
    private readonly List<Entry?> _onReady = [];
    private long _nextToken;
    private RynWindow? _window;

    internal RynWindow? Window
    {
        get
        {
            lock (_gate)
                return _window;
        }
        set
        {
            Entry?[] pending;
            lock (_gate)
            {
                _window = value;
                if (value is null || _onReady.Count == 0)
                    return;

                pending = _onReady.ToArray();
                _onReady.Clear();
            }

            // Invoke outside the lock: a handler may legitimately re-enter the accessor (subscribe to another
            // event, read Window), which would deadlock a non-recursive lock if held here.
            foreach (var entry in pending)
                entry?.Action(value!);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> now if the window already exists, otherwise once it is set. Returns a
    /// token that <see cref="CancelOnReady"/> can use to cancel a still-queued action before the window is
    /// published; the token is <c>0</c> when the action ran synchronously and there is nothing to cancel.
    /// </summary>
    internal long OnReady(Action<RynWindow> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        RynWindow? window;
        long token;
        lock (_gate)
        {
            window = _window;
            if (window is null)
            {
                token = ++_nextToken;
                _onReady.Add(new Entry(token, action));
                return token;
            }
        }

        // Window already live — run synchronously, outside the lock. No queued slot, so nothing to cancel.
        action(window);
        return 0;
    }

    /// <summary>
    /// Cancels a queued ready-callback identified by <paramref name="token"/> if it has not yet run, making a
    /// pre-ready unsubscribe effective. Returns <see langword="true"/> if a pending callback was removed;
    /// <see langword="false"/> if the token was already fired, already cancelled, or never queued (token 0).
    /// </summary>
    internal bool CancelOnReady(long token)
    {
        if (token == 0)
            return false;

        lock (_gate)
        {
            for (var i = 0; i < _onReady.Count; i++)
            {
                if (_onReady[i] is { } entry && entry.Token == token)
                {
                    _onReady[i] = null;
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record Entry(long Token, Action<RynWindow> Action);
}
