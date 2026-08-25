using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;
using SharedBufferDemo;

var html = """
    <!DOCTYPE html>
    <html>
    <head>
        <meta charset="utf-8" />
        <title>Ryn SharedBuffer Demo</title>
        <style>
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body {
                font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif;
                background: #0f0f0f; color: #e0e0e0;
                padding: 24px;
            }
            h1 { font-size: 1.4em; color: #7c3aed; margin-bottom: 4px; }
            .sub { color: #888; font-size: 13px; margin-bottom: 20px; }
            .stats { display: flex; gap: 24px; margin-bottom: 20px; }
            .stat .label { font-size: 11px; color: #888; }
            .stat .value { font-size: 20px; font-weight: 700; color: #a78bfa; }
            table { border-collapse: collapse; width: 100%; font-family: monospace; font-size: 13px; }
            th, td { text-align: right; padding: 6px 12px; border-bottom: 1px solid #222; }
            th { color: #a78bfa; font-weight: 600; }
            td:first-child, th:first-child { text-align: left; }
            .notice {
                background: #1a1a2e; border: 1px solid #2a2a4a; border-radius: 12px;
                padding: 24px; margin-top: 24px; color: #ffb4b4;
            }
        </style>
    </head>
    <body>
        <h1>WebView2 SharedBuffer</h1>
        <div class="sub">The host writes 200 rows of time-series data into shared memory and pushes them at 60Hz; the page reads the ArrayBuffer directly via sharedbufferreceived — no JSON, no network.</div>

        <div class="stats">
            <div class="stat"><div class="label">Frame #</div><div class="value" id="seq">-</div></div>
            <div class="stat"><div class="label">Rows/frame</div><div class="value" id="rows">-</div></div>
            <div class="stat"><div class="label">FPS</div><div class="value" id="fps">-</div></div>
            <div class="stat"><div class="label">Latest ts</div><div class="value" id="ts">-</div></div>
        </div>

        <table>
            <thead id="head"></thead>
            <tbody id="body"></tbody>
        </table>

        <div class="notice" id="notice" style="display:none">
            Not running inside a WebView2 host (window.chrome.webview unavailable); SharedBuffer events cannot be delivered.
        </div>

        <script>
            console.log('[SharedBufferDemo] page loaded');
            var headEl = document.getElementById('head');
            var bodyEl = document.getElementById('body');
            var frames = 0;
            var fpsStart = Date.now();
            var cols = null;

            function renderStats(meta) {
                document.getElementById('seq').textContent = meta.seq;
                document.getElementById('rows').textContent = meta.rows;
                document.getElementById('ts').textContent = meta.ts || '-';
                frames++;
                var now = Date.now();
                if (now - fpsStart >= 1000) {
                    document.getElementById('fps').textContent = frames;
                    frames = 0;
                    fpsStart = now;
                }
            }

            function renderTable(meta, rows) {
                if (!cols || cols.join(',') !== (meta.cols || []).join(',')) {
                    cols = meta.cols;
                    headEl.innerHTML = '<tr>' + cols.map(function (c) { return '<th>' + c + '</th>'; }).join('') + '</tr>';
                }
                var visible = rows.slice(-10).reverse();
                bodyEl.innerHTML = visible.map(function (row) {
                    return '<tr>' + row.map(function (v, i) {
                        if (i === 0) return '<td>' + new Date(v).toLocaleTimeString() + '.' + String(v % 1000).padStart(3, '0') + '</td>';
                        return '<td>' + v.toFixed(3) + '</td>';
                    }).join('') + '</tr>';
                }).join('');
            }

            function onSharedBuffer(e) {
                var meta = e.additionalData || {};
                var buf = e.getBuffer();
                var rows = [];
                try {
                    var colNames = meta.cols || ['timestamp'];
                    var rowCount = meta.rows || 0;
                    var stride = 8 + (colNames.length - 1) * 4;
                    var view = new DataView(buf);
                    for (var i = 0; i < rowCount; i++) {
                        var off = i * stride;
                        var row = [Number(view.getBigInt64(off, true))];
                        for (var s = 1; s < colNames.length; s++) {
                            row.push(view.getFloat32(off + 8 + (s - 1) * 4, true));
                        }
                        rows.push(row);
                    }
                } finally {
                    window.chrome.webview.releaseBuffer(buf);
                }
                meta.ts = rows.length ? rows[rows.length - 1][0] : 0;
                renderStats(meta);
                renderTable(meta, rows);
            }

            if (window.chrome && window.chrome.webview) {
                console.log('[SharedBufferDemo] chrome.webview available, subscribing');
                window.chrome.webview.addEventListener('sharedbufferreceived', onSharedBuffer);
            } else {
                console.log('[SharedBufferDemo] chrome.webview NOT available');
                document.getElementById('notice').style.display = 'block';
            }
        </script>
    </body>
    </html>
    """;

var app = RynApplication.CreateBuilder()
    .ConfigureOptions(opts =>
    {
        opts.Title = "Ryn SharedBuffer Demo";
        opts.Width = 760;
        opts.Height = 640;
        opts.Html = html;
        opts.DevTools = true;
    })
    .ConfigureServices(services =>
    {
        services.AddRynCommands();
        services.AddSingleton<SharedBufferPublisher>();
    })
    .Build();

var publisher = app.Services.GetRequiredService<SharedBufferPublisher>();
var publisherTask = RunPublisherAsync(publisher);

await app.RunAsync();
publisher.Stop();
await publisherTask;
publisher.Dispose();

static async Task RunPublisherAsync(SharedBufferPublisher publisher)
{
    try
    {
        await publisher.StartAsync();
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        await Console.Error.WriteLineAsync($"[SharedBufferDemo] publisher failed: {ex.Message}");
    }
}
