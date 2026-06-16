using HelloWindow;
using Ryn.Core;
using Ryn.Ipc;

var html = """
    <!DOCTYPE html>
    <html>
    <head>
        <meta charset="utf-8" />
        <title>Ryn Demo</title>
        <style>
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body {
                font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif;
                background: #0f0f0f; color: #e0e0e0;
                display: flex; flex-direction: column; align-items: center;
                justify-content: center; height: 100vh; gap: 24px;
            }
            h1 { font-size: 2em; color: #7c3aed; }
            .card {
                background: #1a1a2e; border: 1px solid #2a2a4a; border-radius: 12px;
                padding: 24px; width: 400px;
            }
            .card h2 { font-size: 1.1em; color: #a78bfa; margin-bottom: 12px; }
            input, button {
                padding: 8px 16px; border-radius: 8px; border: 1px solid #3a3a5a;
                background: #252540; color: #e0e0e0; font-size: 14px;
            }
            button {
                background: #7c3aed; border: none; cursor: pointer; font-weight: 600;
            }
            button:hover { background: #6d28d9; }
            .row { display: flex; gap: 8px; margin-bottom: 12px; }
            .result {
                background: #0d0d1a; border-radius: 8px; padding: 12px;
                font-family: monospace; min-height: 40px; margin-top: 8px;
                border: 1px solid #2a2a4a;
            }
            .label { font-size: 12px; color: #888; margin-bottom: 4px; }
        </style>
    </head>
    <body>
        <h1>Ryn IPC Demo</h1>

        <div class="card">
            <h2>Greet Command</h2>
            <div class="row">
                <input id="name" type="text" placeholder="Your name" value="World" />
                <button onclick="doGreet()">Greet</button>
            </div>
            <div class="label">Result:</div>
            <div class="result" id="greetResult">Click Greet to call C#</div>
        </div>

        <div class="card">
            <h2>Add Command</h2>
            <div class="row">
                <input id="a" type="number" value="17" style="width:80px" />
                <span style="color:#888;align-self:center">+</span>
                <input id="b" type="number" value="25" style="width:80px" />
                <button onclick="doAdd()">Add</button>
            </div>
            <div class="label">Result:</div>
            <div class="result" id="addResult">Click Add to call C#</div>
        </div>

        <div class="card">
            <h2>Get Time Command</h2>
            <div class="row">
                <button onclick="doTime()">Get Server Time</button>
            </div>
            <div class="label">Result:</div>
            <div class="result" id="timeResult">Click to get C# DateTime.Now</div>
        </div>

        <script>
            async function doGreet() {
                var name = document.getElementById('name').value;
                var result = await window.__ryn.invoke('app.greet', { name: name });
                document.getElementById('greetResult').textContent = result;
            }
            async function doAdd() {
                var a = parseInt(document.getElementById('a').value);
                var b = parseInt(document.getElementById('b').value);
                var result = await window.__ryn.invoke('app.add', { a: a, b: b });
                document.getElementById('addResult').textContent = a + ' + ' + b + ' = ' + result;
            }
            async function doTime() {
                var result = await window.__ryn.invoke('app.getTime', {});
                document.getElementById('timeResult').textContent = result;
            }
        </script>
    </body>
    </html>
    """;

var app = RynApplication.CreateBuilder()
    .ConfigureOptions(opts =>
    {
        opts.Title = "Ryn IPC Demo";
        opts.Width = 500;
        opts.Height = 700;
        opts.Html = html;
        opts.DevTools = true;
    })
    .ConfigureServices(services =>
    {
        services.AddRynCommands();
        services.AddDemoCommands();
    })
    .Build();

await app.RunAsync();
