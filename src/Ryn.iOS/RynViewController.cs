using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;
using UIKit;
using WebKit;

namespace Ryn.iOS;

public abstract class RynViewController : UIViewController
{
    private WKWebView? _webView;
    private RynIosWebView? _rynWebView;
    private RynCommandDispatcher? _dispatcher;
    private IServiceProvider? _serviceProvider;

    public IRynWebView? WebView => _rynWebView;
    public IServiceProvider? Services => _serviceProvider;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        // 1. Configure DI and services
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // 2. Setup Dispatcher & Capabilities
        var capabilities = LoadCapabilities();
        var routers = _serviceProvider.GetServices<ICommandRouter>();
        _dispatcher = new RynCommandDispatcher(routers, _serviceProvider, capabilities);

        // 3. Configure WKWebView
        var config = new WKWebViewConfiguration();

        // Register custom scheme handler for ryn://
        config.SetUrlSchemeHandler(new RynUrlSchemeHandler(), "ryn");

        // Register script message handler
        var messageHandler = new RynScriptMessageHandler(_dispatcher, ResolveIpc);
        config.UserContentController.AddScriptMessageHandler(messageHandler, "ryn");

        // Inject JS bridge
        var bridgeScript = new WKUserScript(
            new NSString(BuildBridgeScript()),
            WKUserScriptInjectionTime.AtDocumentStart,
            isForMainFrameOnly: false);
        config.UserContentController.AddUserScript(bridgeScript);

        ConfigureWebViewConfiguration(config);

        // 4. Create WKWebView
        _webView = new WKWebView(View!.Bounds, config)
        {
            AutoresizingMask = UIViewAutoresizing.All
        };

        _rynWebView = new RynIosWebView(_webView);

        View.AddSubview(_webView);

        // 5. Load content
        OnReady();
    }

    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    protected virtual void ConfigureWebViewConfiguration(WKWebViewConfiguration configuration)
    {
    }

    protected virtual void OnReady()
    {
        var url = new NSUrl("ryn://localhost/index.html");
        _webView?.LoadRequest(new NSUrlRequest(url));
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

    protected virtual RynCapabilities LoadCapabilities()
    {
        var isDev = false;
#if DEBUG
        isDev = true;
#endif
        var bundlePath = NSBundle.MainBundle.BundlePath;
        var rynJsonPath = Path.Combine(bundlePath, "ryn.json");

        if (File.Exists(rynJsonPath))
        {
            try
            {
                var json = File.ReadAllText(rynJsonPath);
                return RynCapabilitiesLoader.Parse(json, isDev);
            }
            catch (Exception)
            {
            }
        }

        return isDev ? RynCapabilities.AllowAll() : RynCapabilities.DenyAll();
    }

    private void ResolveIpc(long id, bool ok, string resultJson)
    {
        InvokeOnMainThread(() =>
        {
            if (_webView is null) return;
            var escaped = EscapeForJs(resultJson);
            var script = $"window.__ryn._resolve({id}, {(ok ? "true" : "false")}, '{escaped}');";
            _ = _webView.EvaluateJavaScriptAsync(script);
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
              if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.ryn) {
                window.webkit.messageHandlers.ryn.postMessage(JSON.stringify({ id: id, command: command, args: body }));
              } else {
                reject(new Error('Ryn iOS native bridge not found'));
              }
            });
          };

          ryn._resolve = function(id, ok, data) {
            var p = pending[id];
            if (!p) return;
            delete pending[id];
            if (ok) {
              try {
                p.resolve(JSON.parse(data));
              } catch (e) {
                p.resolve(data);
              }
            } else {
              p.reject(new Error(data || 'Command failed'));
            }
          };

          ryn.listen = function(event, callback) {
            if (!listeners[event]) listeners[event] = [];
            listeners[event].push(callback);
            return function() {
              var idx = listeners[event].indexOf(callback);
              if (idx >= 0) listeners[event].splice(idx, 1);
            };
          };

          ryn._emit = function(event, data) {
            var list = listeners[event];
            if (list) {
              list.forEach(function(cb) {
                try { cb(data); } catch (e) { console.error(e); }
              });
            }
          };
        })();
        """;
}
