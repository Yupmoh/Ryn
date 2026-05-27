using System.Text;

namespace Ryn.Core.Internal;

internal sealed class EventBatcher : IDisposable
{
    private readonly IRynWebView _webView;
    private readonly string _eventName;
    private readonly Timer _flushTimer;
    private readonly int _capacity;
    private readonly Lock _lock = new();
    private readonly Queue<string> _queue;
    private readonly List<string> _flushBuffer;
    private readonly StringBuilder _sb = new();
    private bool _disposed;
    private long _addedCount;
    private long _flushedCount;
    private long _droppedCount;

    private const int FlushIntervalMs = 16;
    private const int MaxBatchSize = 100;
    internal const int DefaultCapacity = 10_000;

    internal EventBatcher(IRynWebView webView, string eventName, int capacity = DefaultCapacity)
    {
        _webView = webView;
        _eventName = eventName;
        _capacity = capacity;
        _queue = new Queue<string>(Math.Min(capacity, MaxBatchSize * 2));
        _flushBuffer = new List<string>(MaxBatchSize);
        _flushTimer = new Timer(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);
    }

    internal long AddedCount => Interlocked.Read(ref _addedCount);
    internal long FlushedCount => Interlocked.Read(ref _flushedCount);
    internal long DroppedCount => Interlocked.Read(ref _droppedCount);

    internal void Add(string jsonData)
    {
        lock (_lock)
        {
            if (_disposed) return;

            if (_queue.Count >= _capacity)
            {
                Interlocked.Increment(ref _droppedCount);
                return;
            }

            _queue.Enqueue(jsonData);
            Interlocked.Increment(ref _addedCount);

            if (_queue.Count >= MaxBatchSize)
                FlushBatchLocked();
        }
    }

    internal void FlushNow()
    {
        lock (_lock)
        {
            FlushAllLocked();
        }
    }

    private void Flush()
    {
        lock (_lock)
        {
            if (_disposed) return;
            FlushBatchLocked();
        }
    }

    private void FlushBatchLocked()
    {
        _flushBuffer.Clear();
        while (_flushBuffer.Count < MaxBatchSize && _queue.Count > 0)
            _flushBuffer.Add(_queue.Dequeue());

        if (_flushBuffer.Count == 0) return;
        EmitBatch(_flushBuffer);
    }

    private void FlushAllLocked()
    {
        while (_queue.Count > 0)
        {
            _flushBuffer.Clear();
            while (_flushBuffer.Count < MaxBatchSize && _queue.Count > 0)
                _flushBuffer.Add(_queue.Dequeue());

            if (_flushBuffer.Count == 0) break;
            EmitBatch(_flushBuffer);
        }
    }

    private void EmitBatch(List<string> items)
    {
        _sb.Clear();
        _sb.Append('[');
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) _sb.Append(',');
            _sb.Append(items[i]);
        }
        _sb.Append(']');

        Interlocked.Add(ref _flushedCount, items.Count);
        _webView.EmitEvent(_eventName, _sb.ToString());
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _flushTimer.Dispose();
            FlushAllLocked();
        }
    }
}
