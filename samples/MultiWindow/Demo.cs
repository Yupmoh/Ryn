namespace MultiWindow;

/// <summary>Shared HTML for the demo windows, used by both the startup page and the C# IPC command.</summary>
internal static class Demo
{
    /// <summary>A self-contained child page that asks the framework which window it is (window.current) and
    /// lists every open window (window.list), proving IPC is wired per-window.</summary>
    public static string ChildPage(string heading, string accent) => $$"""
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8" />
          <title>{{heading}}</title>
          <style>
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body {
              font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif;
              background: #0f0f17; color: #e6e6f0; height: 100vh;
              display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 18px;
            }
            h1 { font-size: 1.5em; color: {{accent}}; }
            .badge {
              font-family: ui-monospace, monospace; font-size: 1.5em; font-weight: 700;
              background: #1a1a2e; border: 1px solid #2a2a4a; border-radius: 12px; padding: 10px 22px;
            }
            .label { font-size: 12px; color: #8a8aa0; }
            .list { font-family: ui-monospace, monospace; color: #a78bfa; }
            button {
              padding: 10px 18px; border-radius: 9px; border: none; cursor: pointer; font-weight: 600;
              background: {{accent}}; color: #0f0f17; font-size: 14px;
            }
          </style>
        </head>
        <body>
          <h1>{{heading}}</h1>
          <div class="label">window.current() &rarr;</div>
          <div class="badge" id="whoami">identifying...</div>
          <div class="label">window.list() &rarr; <span class="list" id="list">...</span></div>
          <button onclick="closeSelf()">Close this window</button>
          <script>
            async function whoami() {
              const id = await window.__ryn.invoke('window.current', {});
              document.getElementById('whoami').textContent = 'I am window #' + id;
            }
            async function refresh() {
              const ids = await window.__ryn.invoke('window.list', {});
              document.getElementById('list').textContent = JSON.stringify(ids);
            }
            function closeSelf() { window.__ryn.invoke('window.close', {}); }
            whoami(); refresh();
          </script>
        </body>
        </html>
        """;
}
