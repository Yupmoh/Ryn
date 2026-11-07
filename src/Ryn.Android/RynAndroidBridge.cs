using System.Globalization;
using System.Text;
using Android.Webkit;
using Java.Interop;
using Ryn.Ipc;

namespace Ryn.Android;

public class RynAndroidBridge : Java.Lang.Object
{
    private readonly RynCommandDispatcher _dispatcher;
    private readonly Action<long, bool, string> _resolveCallback;

    public RynAndroidBridge(RynCommandDispatcher dispatcher, Action<long, bool, string> resolveCallback)
    {
        _dispatcher = dispatcher;
        _resolveCallback = resolveCallback;
    }

    [JavascriptInterface]
    [Export("postMessage")]
    public void PostMessage(string idStr, string command, string argsJson)
    {
        if (!long.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            id = 0;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var argsBytes = Encoding.UTF8.GetBytes(argsJson ?? "{}");
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
