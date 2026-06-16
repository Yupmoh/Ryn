namespace Ryn.Core;

/// <summary>
/// Exposes streaming health counters for an event batcher: how many items were enqueued, emitted to the
/// webview, and dropped because the queue was full. Implemented by the internal event batcher and surfaced
/// through the shell plugin's stream-stats commands.
/// </summary>
public interface IEventMetrics
{
    /// <summary>Total items enqueued.</summary>
    public long AddedCount { get; }

    /// <summary>Total items emitted to the webview.</summary>
    public long FlushedCount { get; }

    /// <summary>Total items dropped due to capacity limit.</summary>
    public long DroppedCount { get; }
}
