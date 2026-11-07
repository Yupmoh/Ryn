using System.Text;
using System.Text.Json;
using Foundation;
using Ryn.Ipc;
using WebKit;

namespace Ryn.iOS;

public class RynScriptMessageHandler : NSObject, IWKScriptMessageHandler
{
    private readonly RynCommandDispatcher _dispatcher;
    private readonly Action<long, bool, string> _resolveCallback;

    public RynScriptMessageHandler(RynCommandDispatcher dispatcher, Action<long, bool, string> resolveCallback)
    {
        _dispatcher = dispatcher;
        _resolveCallback = resolveCallback;
    }

    public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var json = message.Body?.ToString();
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            long id = 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idProp))
                {
                    id = idProp.GetInt64();
                }

                var command = root.GetProperty("command").GetString() ?? "";
                var argsJson = root.TryGetProperty("args", out var argsProp) ? argsProp.GetString() ?? "{}" : "{}";
                var argsBytes = Encoding.UTF8.GetBytes(argsJson);

                var resultJson = await _dispatcher.DispatchAsync(command, argsBytes, CancellationToken.None)
                    .ConfigureAwait(false);

                _resolveCallback(id, true, resultJson ?? "null");
            }
            catch (Exception ex)
            {
                _resolveCallback(id, false, ex.Message);
            }
        });
    }
}
