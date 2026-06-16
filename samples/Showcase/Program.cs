using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Clipboard;
using Ryn.Plugins.FileSystem;
using Ryn.Plugins.Notification;
using Ryn.Plugins.Shell;
using Showcase;

var html = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8"/>
<title>Ryn Showcase</title>
<style>
  :root {
    --bg: #0a0a12; --surface: #12121f; --surface2: #1a1a2e;
    --border: #2a2a4a; --text: #e0e0e8; --muted: #666680;
    --accent: #7c3aed; --accent2: #a78bfa; --accent3: #c4b5fd;
    --green: #22c55e; --red: #ef4444; --yellow: #eab308;
  }
  * { margin:0; padding:0; box-sizing:border-box; }
  body {
    font-family: -apple-system, BlinkMacSystemFont, 'SF Pro Display', 'Segoe UI', system-ui, sans-serif;
    background: var(--bg); color: var(--text);
    overflow-x: hidden;
  }
  .header {
    background: linear-gradient(135deg, #1a1a2e 0%, #16162a 100%);
    border-bottom: 1px solid var(--border);
    padding: 32px 40px 24px;
  }
  .header h1 {
    font-size: 28px; font-weight: 700;
    background: linear-gradient(135deg, var(--accent2), var(--accent3));
    -webkit-background-clip: text; -webkit-text-fill-color: transparent;
    margin-bottom: 4px;
  }
  .header p { color: var(--muted); font-size: 13px; }
  .header .badge {
    display: inline-block; background: var(--accent); color: white;
    padding: 2px 10px; border-radius: 12px; font-size: 11px; font-weight: 600;
    margin-left: 8px; vertical-align: middle;
  }
  .tabs {
    display: flex; background: var(--surface); border-bottom: 1px solid var(--border);
    padding: 0 32px; gap: 0;
  }
  .tab {
    padding: 12px 20px; cursor: pointer; color: var(--muted); font-size: 13px;
    font-weight: 500; border-bottom: 2px solid transparent; transition: all 0.2s;
  }
  .tab:hover { color: var(--text); }
  .tab.active { color: var(--accent2); border-bottom-color: var(--accent); }
  .content { padding: 24px 40px; max-height: calc(100vh - 140px); overflow-y: auto; }
  .panel { display: none; }
  .panel.active { display: block; }

  .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
  .card {
    background: var(--surface); border: 1px solid var(--border); border-radius: 12px;
    padding: 20px; transition: border-color 0.2s;
  }
  .card:hover { border-color: #3a3a5a; }
  .card.full { grid-column: 1 / -1; }
  .card h3 {
    font-size: 14px; color: var(--accent2); margin-bottom: 12px;
    display: flex; align-items: center; gap: 8px;
  }
  .card h3 .icon { font-size: 16px; }

  input, textarea, select {
    width: 100%; padding: 10px 14px; border-radius: 8px; border: 1px solid var(--border);
    background: var(--bg); color: var(--text); font-size: 13px; font-family: inherit;
    outline: none; transition: border-color 0.2s;
  }
  input:focus, textarea:focus { border-color: var(--accent); }
  textarea { resize: vertical; min-height: 80px; font-family: 'SF Mono', 'Fira Code', monospace; }

  .btn {
    padding: 8px 18px; border-radius: 8px; border: none; cursor: pointer;
    font-size: 13px; font-weight: 600; transition: all 0.15s;
  }
  .btn-primary { background: var(--accent); color: white; }
  .btn-primary:hover { background: #6d28d9; transform: translateY(-1px); }
  .btn-outline {
    background: transparent; color: var(--accent2); border: 1px solid var(--border);
  }
  .btn-outline:hover { background: var(--surface2); }
  .btn-sm { padding: 6px 12px; font-size: 12px; }

  .row { display: flex; gap: 8px; align-items: center; }
  .mt { margin-top: 12px; }
  .mb { margin-bottom: 12px; }

  .output {
    background: var(--bg); border: 1px solid var(--border); border-radius: 8px;
    padding: 14px; font-family: 'SF Mono', 'Fira Code', monospace; font-size: 12px;
    line-height: 1.6; min-height: 44px; white-space: pre-wrap; word-break: break-all;
    margin-top: 12px; color: var(--accent3); max-height: 200px; overflow-y: auto;
  }
  .output.error { color: var(--red); border-color: #4a1a1a; }
  .output.success { color: var(--green); }

  .stat { text-align: center; padding: 16px; }
  .stat .value { font-size: 28px; font-weight: 700; color: var(--accent2); }
  .stat .label { font-size: 11px; color: var(--muted); margin-top: 4px; text-transform: uppercase; letter-spacing: 1px; }

  .kv-table { width: 100%; font-size: 12px; }
  .kv-table td { padding: 6px 0; border-bottom: 1px solid #1a1a2e; vertical-align: top; }
  .kv-table td:first-child { color: var(--muted); width: 140px; font-weight: 500; }
  .kv-table td:last-child { color: var(--accent3); font-family: 'SF Mono', monospace; word-break: break-all; }

  .file-list { list-style: none; }
  .file-list li {
    display: flex; align-items: center; gap: 8px; padding: 8px 12px;
    border-radius: 6px; font-size: 13px; cursor: default;
  }
  .file-list li:hover { background: var(--surface2); }
  .file-list .icon { width: 20px; text-align: center; }
  .file-list .name { flex: 1; }
  .file-list .size { color: var(--muted); font-size: 11px; font-family: monospace; }

  .toast {
    position: fixed; bottom: 24px; right: 24px; background: var(--green);
    color: white; padding: 12px 20px; border-radius: 10px; font-size: 13px;
    font-weight: 600; transform: translateY(100px); opacity: 0;
    transition: all 0.3s ease;
  }
  .toast.show { transform: translateY(0); opacity: 1; }

  @keyframes pulse { 0%,100% { opacity:1; } 50% { opacity:0.5; } }
  .loading { animation: pulse 1.5s ease-in-out infinite; color: var(--muted); }
</style>
</head>
<body>

<div class="header">
  <h1>Ryn <span class="badge">Showcase</span></h1>
  <p>Rich Yet Native — a cross-platform desktop framework for .NET</p>
</div>

<div class="tabs">
  <div class="tab active" onclick="switchTab('ipc')">IPC Commands</div>
  <div class="tab" onclick="switchTab('system')">System Info</div>
  <div class="tab" onclick="switchTab('files')">File System</div>
  <div class="tab" onclick="switchTab('tools')">Tools</div>
</div>

<div class="content">

  <!-- IPC Tab -->
  <div class="panel active" id="panel-ipc">
    <div class="grid">
      <div class="card">
        <h3><span class="icon">👋</span> Greet</h3>
        <div class="row">
          <input id="greetName" placeholder="Enter a name" value="World" />
          <button class="btn btn-primary" onclick="doGreet()">Call C#</button>
        </div>
        <div class="output" id="greetOutput">Response will appear here</div>
      </div>

      <div class="card">
        <h3><span class="icon">🧮</span> Calculator</h3>
        <div class="row">
          <input id="calcExpr" placeholder="e.g. 42 + 8" value="355 / 113" />
          <button class="btn btn-primary" onclick="doCalc()">Evaluate</button>
        </div>
        <div class="output" id="calcOutput">Enter an expression</div>
      </div>

      <div class="card full">
        <h3><span class="icon">🔢</span> Fibonacci Sequence</h3>
        <div class="row">
          <input id="fibN" type="number" value="15" style="width:100px" />
          <button class="btn btn-primary" onclick="doFib()">Generate</button>
        </div>
        <div class="output" id="fibOutput">Click Generate to compute</div>
      </div>
    </div>
  </div>

  <!-- System Tab -->
  <div class="panel" id="panel-system">
    <div class="grid">
      <div class="card full" id="sysinfoCard">
        <h3><span class="icon">💻</span> System Information</h3>
        <div class="loading">Loading system info...</div>
      </div>
      <div class="card">
        <h3><span class="icon">⏰</span> Live Clock</h3>
        <div class="stat">
          <div class="value" id="clock">--:--:--</div>
          <div class="label">C# DateTime.Now</div>
        </div>
      </div>
      <div class="card">
        <h3><span class="icon">📊</span> IPC Stats</h3>
        <div class="stat">
          <div class="value" id="callCount">0</div>
          <div class="label">IPC Calls Made</div>
        </div>
      </div>
    </div>
  </div>

  <!-- Files Tab -->
  <div class="panel" id="panel-files">
    <div class="grid">
      <div class="card">
        <h3><span class="icon">📁</span> Browse Directory</h3>
        <div class="row mb">
          <input id="dirPath" placeholder="Directory path" value="." />
          <button class="btn btn-primary" onclick="doBrowse()">Browse</button>
        </div>
        <ul class="file-list" id="fileList">
          <li style="color:var(--muted)">Enter a path and click Browse</li>
        </ul>
      </div>
      <div class="card">
        <h3><span class="icon">📝</span> Notepad</h3>
        <div class="row mb">
          <input id="filePath" placeholder="File path" value="ryn-note.txt" />
        </div>
        <textarea id="fileContent" placeholder="Type something...">Hello from Ryn! 🚀

This text can be saved to disk and read back using the FileSystem plugin.</textarea>
        <div class="row mt">
          <button class="btn btn-primary" onclick="doSaveFile()">Save</button>
          <button class="btn btn-outline" onclick="doLoadFile()">Load</button>
        </div>
        <div class="output" id="fileOutput" style="min-height:24px"></div>
      </div>
    </div>
  </div>

  <!-- Tools Tab -->
  <div class="panel" id="panel-tools">
    <div class="grid">
      <div class="card">
        <h3><span class="icon">📋</span> Clipboard</h3>
        <div class="row mb">
          <input id="clipText" placeholder="Text to copy" value="Copied from Ryn!" />
          <button class="btn btn-primary btn-sm" onclick="doClipCopy()">Copy</button>
        </div>
        <button class="btn btn-outline btn-sm" onclick="doClipPaste()">Read Clipboard</button>
        <div class="output" id="clipOutput" style="min-height:24px"></div>
      </div>
      <div class="card">
        <h3><span class="icon">🔔</span> Notification</h3>
        <div class="row mb">
          <input id="notifTitle" placeholder="Title" value="Ryn Notification" />
        </div>
        <div class="row mb">
          <input id="notifBody" placeholder="Body" value="This was sent from JavaScript via C#!" />
        </div>
        <button class="btn btn-primary" onclick="doNotify()">Send Notification</button>
      </div>
      <div class="card full">
        <h3><span class="icon">🐚</span> Shell</h3>
        <div class="row mb">
          <input id="shellCmd" placeholder="Command" value="echo" style="width:120px" />
          <input id="shellArgs" placeholder='Args (JSON array)' value='["Hello from Ryn shell!"]' />
          <button class="btn btn-primary" onclick="doShell()">Execute</button>
        </div>
        <div class="output" id="shellOutput">Command output will appear here</div>
      </div>
    </div>
  </div>

</div>

<div class="toast" id="toast"></div>

<script>
  var callCount = 0;

  async function invoke(cmd, args) {
    if (!window.__ryn || !window.__ryn.invoke) {
      throw new Error('Ryn bridge not available');
    }
    callCount++;
    document.getElementById('callCount').textContent = callCount;
    return await window.__ryn.invoke(cmd, args || {});
  }

  function switchTab(name) {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    document.querySelectorAll('.panel').forEach(p => p.classList.remove('active'));
    event.target.classList.add('active');
    document.getElementById('panel-' + name).classList.add('active');
    if (name === 'system') loadSysInfo();
  }

  function toast(msg) {
    var el = document.getElementById('toast');
    el.textContent = msg;
    el.classList.add('show');
    setTimeout(function() { el.classList.remove('show'); }, 2000);
  }

  function formatSize(bytes) {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1048576) return (bytes/1024).toFixed(1) + ' KB';
    return (bytes/1048576).toFixed(1) + ' MB';
  }

  // Escape any file- or environment-derived string before it lands in innerHTML, so a
  // filename or machine name like '<img src=x onerror=alert(1)>' renders as text, not markup.
  function escapeHtml(value) {
    return String(value)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  // IPC Commands
  async function doGreet() {
    try {
      var name = document.getElementById('greetName').value;
      var result = await invoke('app.greet', { name: name });
      document.getElementById('greetOutput').textContent = result;
      document.getElementById('greetOutput').className = 'output success';
    } catch(e) {
      document.getElementById('greetOutput').textContent = 'Error: ' + e.message;
      document.getElementById('greetOutput').className = 'output error';
    }
  }

  async function doCalc() {
    var expr = document.getElementById('calcExpr').value;
    var result = await invoke('app.calculate', { expression: expr });
    var el = document.getElementById('calcOutput');
    el.textContent = expr + ' = ' + result;
    el.className = result.startsWith('Error') ? 'output error' : 'output success';
  }

  async function doFib() {
    var n = parseInt(document.getElementById('fibN').value);
    var result = await invoke('app.fibonacci', { n: n });
    var el = document.getElementById('fibOutput');
    try {
      var arr = JSON.parse(result);
      el.textContent = arr.join(', ');
      el.className = 'output success';
    } catch(e) {
      el.textContent = result;
      el.className = 'output error';
    }
  }

  // System Info
  async function loadSysInfo() {
    try {
      var info = JSON.parse(await invoke('app.sysinfo'));
      var html = '<h3><span class="icon">💻</span> System Information</h3>';
      html += '<table class="kv-table">';
      html += '<tr><td>OS</td><td>' + escapeHtml(info.os) + '</td></tr>';
      html += '<tr><td>Architecture</td><td>' + escapeHtml(info.arch) + '</td></tr>';
      html += '<tr><td>Runtime</td><td>' + escapeHtml(info.runtime) + '</td></tr>';
      html += '<tr><td>Processors</td><td>' + escapeHtml(info.processors) + ' cores</td></tr>';
      html += '<tr><td>Machine</td><td>' + escapeHtml(info.machineName) + '</td></tr>';
      html += '<tr><td>User</td><td>' + escapeHtml(info.userName) + '</td></tr>';
      html += '<tr><td>Working Dir</td><td>' + escapeHtml(info.workingDir) + '</td></tr>';
      html += '</table>';
      document.getElementById('sysinfoCard').innerHTML = html;
    } catch(e) {
      document.getElementById('sysinfoCard').innerHTML = '<div class="output error">' + escapeHtml(e.message) + '</div>';
    }
  }

  // Live clock
  setInterval(async function() {
    try {
      var time = await invoke('app.time');
      document.getElementById('clock').textContent = time.split(' ')[1] || time;
    } catch(e) {}
  }, 1000);

  // File System
  async function doBrowse() {
    try {
      var path = document.getElementById('dirPath').value;
      var result = await invoke('fs.readDir', { path: path });
      var entries = JSON.parse(result);
      var html = '';
      entries.sort(function(a,b) { return (b.isDirectory?1:0) - (a.isDirectory?1:0) || a.name.localeCompare(b.name); });
      entries.forEach(function(e) {
        html += '<li>';
        html += '<span class="icon">' + (e.isDirectory ? '📁' : '📄') + '</span>';
        html += '<span class="name">' + escapeHtml(e.name) + '</span>';
        html += '<span class="size">' + (e.isDirectory ? '' : formatSize(e.size)) + '</span>';
        html += '</li>';
      });
      document.getElementById('fileList').innerHTML = html || '<li style="color:var(--muted)">Empty directory</li>';
    } catch(e) {
      document.getElementById('fileList').innerHTML = '<li class="output error">' + escapeHtml(e.message) + '</li>';
    }
  }

  async function doSaveFile() {
    try {
      var path = document.getElementById('filePath').value;
      var content = document.getElementById('fileContent').value;
      await invoke('fs.writeTextFile', { path: path, text: content });
      document.getElementById('fileOutput').textContent = 'Saved to ' + path;
      document.getElementById('fileOutput').className = 'output success';
      toast('File saved!');
    } catch(e) {
      document.getElementById('fileOutput').textContent = e.message;
      document.getElementById('fileOutput').className = 'output error';
    }
  }

  async function doLoadFile() {
    try {
      var path = document.getElementById('filePath').value;
      var content = await invoke('fs.readTextFile', { path: path });
      document.getElementById('fileContent').value = content;
      document.getElementById('fileOutput').textContent = 'Loaded from ' + path;
      document.getElementById('fileOutput').className = 'output success';
      toast('File loaded!');
    } catch(e) {
      document.getElementById('fileOutput').textContent = e.message;
      document.getElementById('fileOutput').className = 'output error';
    }
  }

  // Clipboard
  async function doClipCopy() {
    await invoke('clipboard.writeText', { text: document.getElementById('clipText').value });
    toast('Copied to clipboard!');
  }

  async function doClipPaste() {
    var text = await invoke('clipboard.readText');
    document.getElementById('clipOutput').textContent = text || '(empty)';
    document.getElementById('clipOutput').className = 'output';
  }

  // Notification
  async function doNotify() {
    var title = document.getElementById('notifTitle').value;
    var body = document.getElementById('notifBody').value;
    await invoke('notification.send', { title: title, body: body });
    toast('Notification sent!');
  }

  // Shell
  async function doShell() {
    try {
      var cmd = document.getElementById('shellCmd').value;
      var args = document.getElementById('shellArgs').value;
      var result = await invoke('shell.execute', { command: cmd, argsJson: args });
      var parsed = JSON.parse(result);
      var el = document.getElementById('shellOutput');
      el.textContent = parsed.stdout || parsed.stderr || '(no output)';
      el.className = parsed.exitCode === 0 ? 'output success' : 'output error';
    } catch(e) {
      document.getElementById('shellOutput').textContent = e.message;
      document.getElementById('shellOutput').className = 'output error';
    }
  }

  // Load system info on startup
  loadSysInfo();
</script>
</body>
</html>
""";

var app = RynApplication.CreateBuilder()
    .ConfigureOptions(opts =>
    {
        opts.Title = "Ryn Showcase";
        opts.Width = 960;
        opts.Height = 750;
        opts.Html = html;
        opts.DevTools = true;
    })
    .ConfigureServices(services =>
    {
        services.AddRynCommands();
        services.AddShowcaseCommands();
        services.AddRynFileSystem(fs => fs.AllowedPaths.Add(AppContext.BaseDirectory));
        services.AddRynClipboard();
        services.AddRynShell(shell => shell.AllowedCommands.AddRange(["echo", "date", "whoami", "uname", "ls"]));
        services.AddRynNotification();
    })
    .Build();

await app.RunAsync();
