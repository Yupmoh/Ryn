namespace Ryn.Core;

/// <summary>
/// Exposes streaming health counters from an <see cref="Internal.EventBatcher"/>.
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
