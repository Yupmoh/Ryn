using System.Text;

namespace Ryn.Core.Internal;

internal sealed class EventBatcher : IDisposable
{
    private readonly IRynWebView _webView;
    private readonly string _eventName;
    private readonly Timer _flushTimer;
    private readonly List<string> _buffer = [];
    private readonly Lock _lock = new();
    private bool _disposed;

    private const int FlushIntervalMs = 16; // ~60fps
    private const int MaxBatchSize = 100;

    internal EventBatcher(IRynWebView webView, string eventName)
    {
        _webView = webView;
        _eventName = eventName;
        _flushTimer = new Timer(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);
    }

    internal void Add(string jsonData)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _buffer.Add(jsonData);

            if (_buffer.Count >= MaxBatchSize)
                FlushLocked();
        }
    }

    internal void FlushNow()
    {
        lock (_lock)
        {
            FlushLocked();
        }
    }

    private void Flush()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            FlushLocked();
        }
    }

    private void FlushLocked()
    {
        if (_buffer.Count == 0)
            return;

        var items = new List<string>(_buffer);
        _buffer.Clear();

        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(items[i]);
        }
        sb.Append(']');

        _webView.EmitEvent(_eventName, sb.ToString());
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _flushTimer.Dispose();
            FlushLocked();
        }
    }
}
