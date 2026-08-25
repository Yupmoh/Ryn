using Ryn.Core;

namespace SharedBufferDemo;

/// <summary>
/// Demonstrates the WebView2 SharedBuffer push path: the host writes rows of time-series data into a
/// shared-memory buffer and posts it to the page, where script reads it as an <c>ArrayBuffer</c> via
/// <c>chrome.webview</c>'s <c>sharedbufferreceived</c> event — no JSON, no network stack.
/// </summary>
/// <remarks>
/// <para>
/// Two buffers are alternated so the host never rewrites a buffer the page may still be reading: after
/// posting buffer A the producer fills buffer B, posts it, then reuses A. Each frame is 200 rows of
/// <c>long timestamp + 3 floats</c> (20 bytes/row), the same binary layout as a typical chart feed.
/// </para>
/// <para>
/// The webview is created lazily after <c>RunAsync</c> starts, so <see cref="StartAsync"/> retries
/// <see cref="IRynWebView.CreateSharedBufferAsync"/> until the deferred <see cref="IRynWebView"/> resolves
/// to a live webview (the buffer creation itself runs on the UI thread).
/// </para>
/// </remarks>
public sealed class SharedBufferPublisher : IDisposable
{
    private readonly IRynWebView _webView;
    private readonly CancellationTokenSource _cts = new();

    private static readonly string[] Cols = ["timestamp", "voltage", "current", "temp"];
    private const int RowsPerFrame = 200;
    private const int SensorCount = 3;
    private const int TimestampBytes = 8;
    private const int RowBytes = TimestampBytes + SensorCount * sizeof(float);

    public SharedBufferPublisher(IRynWebView webView)
    {
        _webView = webView;
    }

    public async Task StartAsync()
    {
        try
        {
            var buffers = await CreateBuffersWithRetryAsync();
            try
            {
                await PumpAsync(buffers);
            }
            finally
            {
                foreach (var buffer in buffers)
                    buffer.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // App shutting down.
        }
    }

    public void Stop() => _cts.Cancel();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task<RynSharedBuffer[]> CreateBuffersWithRetryAsync()
    {
        var capacity = (ulong)(RowsPerFrame * RowBytes);
        while (true)
        {
            try
            {
                var a = await _webView.CreateSharedBufferAsync(capacity, _cts.Token);
                var b = await _webView.CreateSharedBufferAsync(capacity, _cts.Token);
                Console.WriteLine($"[SharedBufferDemo] buffers created: {a.Size} bytes x2");
                return [a, b];
            }
            catch (InvalidOperationException) when (!_cts.IsCancellationRequested)
            {
                // The deferred IRynWebView is not attached to a live webview yet (app still starting).
                await Task.Delay(16, _cts.Token);
            }
        }
    }

    private async Task PumpAsync(RynSharedBuffer[] buffers)
    {
        var bufferIndex = 0;
        long seq = 0;
        const double periodMs = 1000.0 / 60.0; // 60Hz fixed-step cadence (~16.67ms per frame)
        double nextTick = Environment.TickCount64 + periodMs;
        long lastLoggedSeq = 0;
        var lastLoggedTick = Environment.TickCount64;

        while (!_cts.IsCancellationRequested)
        {
            var buffer = buffers[bufferIndex++ % buffers.Length];
            WriteFrame(buffer, seq);

            var colsJson = string.Join(",", Cols.Select(c => $"\"{c}\""));
            var additionalData =
                $$"""{"cols":[{{colsJson}}],"rows":{{RowsPerFrame}},"seq":{{seq}}}""";
            await _webView.PostSharedBufferToScriptAsync(
                buffer, RynSharedBufferAccess.ReadOnly, additionalData, _cts.Token);

            seq++;

            // Align to the next 60Hz boundary instead of sleeping a fixed interval, so loop overhead
            // does not drag the average below 60 frames/second. The fractional boundary alternates
            // 16/17ms sleeps so the average is exactly 60Hz.
            nextTick += periodMs;
            var delay = (long)Math.Ceiling(nextTick - Environment.TickCount64);
            if (delay > 0)
                await Task.Delay((int)delay, _cts.Token);

            if (seq - lastLoggedSeq >= 60)
            {
                var elapsed = Environment.TickCount64 - lastLoggedTick;
                Console.WriteLine($"[SharedBufferDemo] {seq - lastLoggedSeq} frames in {elapsed} ms");
                lastLoggedSeq = seq;
                lastLoggedTick = Environment.TickCount64;
            }
        }
    }

    private static unsafe void WriteFrame(RynSharedBuffer buffer, long seq)
    {
        var baseTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var p = (byte*)buffer.Buffer;

        for (var i = 0; i < RowsPerFrame; i++)
        {
            // Deterministic "noise" (no Random: this is a demo, not a security boundary).
            var jitter = (float)Math.Sin(seq * 0.31 + i * 0.13);
            var row = p + i * RowBytes;
            *(long*)row = baseTime + i;

            var values = (float*)(row + TimestampBytes);
            values[0] = 220f + (float)(Math.Sin(baseTime * 0.001) * 5.0) + jitter * 0.25f;
            values[1] = 4.5f + jitter * 0.1f;
            values[2] = 36.5f + (float)(Math.Sin(baseTime * 0.0005) * 0.8) + jitter * 0.05f;
        }
    }
}
