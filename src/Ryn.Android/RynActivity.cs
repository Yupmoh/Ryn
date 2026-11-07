using System.Text.Json;
using Android.App;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Webkit;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;

namespace Ryn.Android;

public abstract class RynActivity : Activity
{
    private const string Tag = "Ryn";
    private WebView? _webView;
    private RynAndroidWebView? _rynWebView;
    private RynCommandDispatcher? _dispatcher;
    private IServiceProvider? _serviceProvider;

    public IRynWebView? WebView => _rynWebView;
    public IServiceProvider? Services => _serviceProvider;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Log.Info(Tag, "RynActivity.OnCreate started");

        // 1. Configure DI and services
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // 2. Setup Dispatcher & Capabilities
        var capabilities = LoadCapabilities();
        var routers = _serviceProvider.GetServices<ICommandRouter>();
        _dispatcher = new RynCommandDispatcher(routers, _serviceProvider, capabilities);

        // 3. Create & configure Android WebView
        _webView = new WebView(this);
        ConfigureWebViewSettings(_webView.Settings);
        _webView.SetWebChromeClient(new WebChromeClient());

        _rynWebView = new RynAndroidWebView(_webView);

        // 4. Attach JavascriptInterface bridge
        var bridge = new RynAndroidBridge(_dispatcher, ResolveIpc);
        _webView.AddJavascriptInterface(bridge, "__ryn_native");

        // 5. Attach WebViewClient
        _webView.SetWebViewClient(new RynWebViewClient(Assets!, BuildBridgeScript));

        // 6. Set as main view
        SetContentView(_webView);

        // 7. Load content
        OnReady();
        Log.Info(Tag, "RynActivity.OnCreate completed");
    }

    protected virtual void ConfigureServices(IServiceCollection services)
    {
        // Apps override this to register commands, repositories, and plugins
    }

    protected virtual void OnReady()
    {
        // Load index.html from wwwroot
        try
        {
            using var stream = Assets?.Open("wwwroot/index.html");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                var html = reader.ReadToEnd();
                Log.Info(Tag, $"Loading index.html ({html.Length} chars)");
                _webView?.LoadDataWithBaseURL("https://appassets.androidplatform.net/", html, "text/html", "UTF-8", null);
                return;
            }
        }
        catch (Java.IO.IOException ex)
        {
            Log.Warn(Tag, $"Could not load wwwroot/index.html: {ex.Message}");
        }

        // Fallback: try root index.html
        try
        {
            using var stream = Assets?.Open("index.html");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                var html = reader.ReadToEnd();
                _webView?.LoadDataWithBaseURL("https://appassets.androidplatform.net/", html, "text/html", "UTF-8", null);
                return;
            }
        }
        catch (Java.IO.IOException)
        {
            Log.Error(Tag, "index.html not found in assets or assets/wwwroot");
        }
    }

    protected virtual void ConfigureWebViewSettings(WebSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.DatabaseEnabled = true;
        settings.AllowFileAccess = true;
        settings.AllowContentAccess = true;
    }

    protected virtual RynCapabilities LoadCapabilities()
    {
        var isDev = false;
#if DEBUG
        isDev = true;
#endif
        try
        {
            using var stream = Assets?.Open("ryn.json");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                Log.Info(Tag, "Loaded ryn.json capabilities");
                return RynCapabilitiesLoader.Parse(json, isDev);
            }
        }
        catch (Java.IO.IOException)
        {
            Log.Warn(Tag, "ryn.json not found in assets, falling back");
        }

        return isDev ? RynCapabilities.AllowAll() : RynCapabilities.DenyAll();
    }

    private void ResolveIpc(long id, bool ok, string resultJson)
    {
        RunOnUiThread(() =>
        {
            if (_webView is null) return;
            var escaped = EscapeForJs(resultJson);
            var script = $"window.__ryn._resolve({id}, {(ok ? "true" : "false")}, '{escaped}');";
            _webView.EvaluateJavascript(script, null);
        });
    }

    private static string EscapeForJs(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);
    }

    private static string BuildBridgeScript() =>
        """
        (function(){
          var ryn = window.__ryn = window.__ryn || {};
          var nextId = 1;
          var pending = {};
          var listeners = {};

          ryn.invoke = function(command, args) {
            return new Promise(function(resolve, reject) {
              var id = nextId++;
              pending[id] = { resolve: resolve, reject: reject };
              var body = args ? JSON.stringify(args) : '{}';
              if (window.__ryn_native && window.__ryn_native.postMessage) {
                window.__ryn_native.postMessage(id.toString(), command, body);
              } else {
                reject(new Error('Ryn Android native bridge not found'));
              }
            });
          };

          ryn._resolve = function(id, ok, data) {
            var p = pending[id];
            if (!p) return;
            delete pending[id];
            if (ok) {
              try { p.resolve(JSON.parse(data)); } catch(e) { p.resolve(data); }
            } else {
              p.reject(new Error(data));
            }
          };

          ryn.on = function(event, callback) {
            if (!listeners[event]) listeners[event] = [];
            listeners[event].push(callback);
          };

          ryn.off = function(event, callback) {
            var list = listeners[event];
            if (!list) return;
            var idx = list.indexOf(callback);
            if (idx >= 0) list.splice(idx, 1);
          };

          ryn._emit = function(event, data) {
            var list = listeners[event];
            if (!list) return;
            for (var i = 0; i < list.length; i++) {
              try { list[i](data); } catch(e) { console.error('Ryn event error:', e); }
            }
          };
          console.log('Ryn Android bridge initialized');
        })();
        """;

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.Back && _webView is not null && _webView.CanGoBack())
        {
            _webView.GoBack();
            return true;
        }

        return base.OnKeyDown(keyCode, e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _webView?.Dispose();
            _webView = null;
        }
        base.Dispose(disposing);
    }

    protected override void OnDestroy()
    {
        _webView?.Destroy();
        _webView = null;
        base.OnDestroy();
    }
}
