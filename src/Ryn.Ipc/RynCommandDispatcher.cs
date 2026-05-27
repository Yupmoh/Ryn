using System.Diagnostics;

namespace Ryn.Ipc;

public sealed class RynCommandDispatcher
{
    private readonly ICommandRouter[] _routers;
    private readonly IServiceProvider _services;
    private readonly RynCapabilities _capabilities;
    private readonly IIpcObserver? _observer;

    public RynCommandDispatcher(
        IEnumerable<ICommandRouter> routers,
        IServiceProvider services,
        RynCapabilities capabilities,
        IIpcObserver? observer = null)
    {
        _routers = routers.ToArray();
        _services = services;
        _capabilities = capabilities;
        _observer = observer;
    }

    public async ValueTask<string> DispatchAsync(
        string command,
        ReadOnlyMemory<byte> args,
        CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _routers.Length; i++)
        {
            if (_routers[i].CanRoute(command))
            {
                try
                {
                    _capabilities.ThrowIfDenied(command);
                }
                catch (RynCommandDeniedException)
                {
                    NotifyObserver(() => _observer?.OnCommandDenied(command));
                    throw;
                }

                NotifyObserver(() => _observer?.OnCommandStarted(command));
                var sw = Stopwatch.StartNew();
                try
                {
                    var result = await _routers[i].RouteAsync(command, args, _services, cancellationToken)
                        .ConfigureAwait(false);
                    NotifyObserver(() => _observer?.OnCommandCompleted(command, sw.ElapsedMilliseconds));
                    return result;
                }
                catch (Exception ex)
                {
                    NotifyObserver(() => _observer?.OnCommandFailed(command, sw.ElapsedMilliseconds, ex));
                    throw;
                }
            }
        }

        throw new RynCommandNotFoundException(command);
    }

    private static void NotifyObserver(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
