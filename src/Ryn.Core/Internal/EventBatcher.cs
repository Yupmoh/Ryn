using System.Text;
using System.Threading.Channels;

namespace Ryn.Core.Internal;

internal sealed class EventBatcher : IDisposable
{
    private readonly IRynWebView _webView;
    private readonly string _eventName;
    private readonly Timer _flushTimer;
    private readonly Channel<string> _channel;
    private readonly Lock _flushLock = new();
    private readonly List<string> _flushBuffer = new(MaxBatchSize);
    private readonly StringBuilder _sb = new();
    private bool _disposed;
    private long _addedCount;
    private long _flushedCount;

    private const int FlushIntervalMs = 16;
    private const int MaxBatchSize = 100;
    internal const int DefaultCapacity = 10_000;

    internal EventBatcher(IRynWebView webView, string eventName, int capacity = DefaultCapacity)
    {
        _webView = webView;
        _eventName = eventName;
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _flushTimer = new Timer(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);
    }

    internal long AddedCount => Interlocked.Read(ref _addedCount);
    internal long FlushedCount => Interlocked.Read(ref _flushedCount);
    internal long Backlog => AddedCount - FlushedCount;

    internal void Add(string jsonData)
    {
        if (_disposed) return;
        _channel.Writer.TryWrite(jsonData);
        Interlocked.Increment(ref _addedCount);
    }

    internal void FlushNow()
    {
        lock (_flushLock)
        {
            FlushAllLocked();
        }
    }

    private void Flush()
    {
        lock (_flushLock)
        {
            if (_disposed) return;
            FlushBatchLocked();
        }
    }

    private void FlushBatchLocked()
    {
        _flushBuffer.Clear();
        while (_flushBuffer.Count < MaxBatchSize && _channel.Reader.TryRead(out var item))
            _flushBuffer.Add(item);

        if (_flushBuffer.Count == 0) return;
        EmitBatch(_flushBuffer);
    }

    private void FlushAllLocked()
    {
        while (true)
        {
            _flushBuffer.Clear();
            while (_flushBuffer.Count < MaxBatchSize && _channel.Reader.TryRead(out var item))
                _flushBuffer.Add(item);

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
        lock (_flushLock)
        {
            if (_disposed) return;
            _disposed = true;
            _flushTimer.Dispose();
            _channel.Writer.Complete();
            FlushAllLocked();
        }
    }
}
