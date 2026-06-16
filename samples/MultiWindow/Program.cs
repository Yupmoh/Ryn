using MultiWindow;
using Ryn.Core;
using Ryn.Ipc;

// The page used by the JS-opened child window, embedded into the main page below as a JS string literal via
// JsonEncodedText.Encode, which escapes '<' and '>' to \uXXXX so the nested </script> can't prematurely close
// the main page's own script element.
var jsChildLiteral = System.Text.Json.JsonEncodedText.Encode(Demo.ChildPage("JS child", "#34d399")).ToString();

// The main "hub" window: identifies itself, lists the open windows, and on load opens the other two windows
// and tiles them. The work is driven over IPC (window.open + the demo.* commands) so it runs on the event
// loop's dispatch path and stays reliable even when nothing else is happening on screen.
var mainPage = $$"""
    <!DOCTYPE html>
    <html>
    <head>
      <meta charset="utf-8" />
      <title>Ryn Multi-Window</title>
      <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
          font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif;
          background: #0f0f17; color: #e6e6f0; height: 100vh;
          display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 22px;
        }
        h1 { font-size: 2em; color: #7c3aed; }
        .badge {
          font-family: ui-monospace, monospace; font-size: 1.8em; font-weight: 700;
          background: #1a1a2e; border: 1px solid #2a2a4a; border-radius: 14px; padding: 12px 26px;
        }
        .card {
          background: #1a1a2e; border: 1px solid #2a2a4a; border-radius: 14px;
          padding: 20px; width: 440px; display: flex; flex-direction: column; gap: 12px;
        }
        .row { display: flex; gap: 10px; }
        .label { font-size: 12px; color: #8a8aa0; }
        .list { font-family: ui-monospace, monospace; color: #a78bfa; font-size: 1.1em; }
        button {
          flex: 1; padding: 11px 16px; border-radius: 9px; border: none; cursor: pointer; font-weight: 600;
          background: #7c3aed; color: #fff; font-size: 14px;
        }
        button:hover { background: #6d28d9; }
        button.ghost { background: #252540; }
      </style>
    </head>
    <body>
      <h1>Ryn Multi-Window</h1>
      <div class="label">window.current() &rarr;</div>
      <div class="badge" id="whoami">identifying...</div>
      <div class="card">
        <div class="row">
          <button onclick="openChild()">Open JS child</button>
          <button class="ghost" onclick="refresh()">Refresh list</button>
        </div>
        <div class="label">Open windows (window.list): <span class="list" id="list">...</span></div>
      </div>
      <script>
        const childHtml = "{{jsChildLiteral}}";
        async function whoami() {
          const id = await window.__ryn.invoke('window.current', {});
          document.getElementById('whoami').textContent = 'I am window #' + id + ' (main)';
        }
        async function refresh() {
          const ids = await window.__ryn.invoke('window.list', {});
          document.getElementById('list').textContent = JSON.stringify(ids);
        }
        async function openChild() {
          await window.__ryn.invoke('window.open', { title: 'JS child', width: 420, height: 320, html: childHtml });
          refresh();
        }
        async function setup() {
          await openChild();                                  // window #2, opened from JavaScript
          await window.__ryn.invoke('demo.openFromCSharp', {}); // window #3, opened from C#
          await window.__ryn.invoke('demo.tile', {});           // arrange all three
          refresh();
        }
        whoami(); refresh();
        // Open the other windows shortly after load so the multi-window layout appears on its own.
        setTimeout(setup, 500);
      </script>
    </body>
    </html>
    """;

var app = RynApplication.CreateBuilder()
    .ConfigureOptions(opts =>
    {
        opts.Title = "Ryn Multi-Window (Main)";
        opts.Width = 560;
        opts.Height = 620;
        opts.Html = mainPage;
    })
    .ConfigureServices(services =>
    {
        services.AddRynCommands();
        services.AddDemoCommands();
    })
    .Build();

app.Run();
