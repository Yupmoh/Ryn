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
                    NotifyDenied(_observer, command);
                    throw;
                }

                NotifyStarted(_observer, command);
                var sw = Stopwatch.StartNew();
                try
                {
                    var result = await _routers[i].RouteAsync(command, args, _services, cancellationToken)
                        .ConfigureAwait(false);
                    NotifyCompleted(_observer, command, sw.ElapsedMilliseconds);
                    return result;
                }
                catch (Exception ex)
                {
                    NotifyFailed(_observer, command, sw.ElapsedMilliseconds, ex);
                    throw;
                }
            }
        }

        throw new RynCommandNotFoundException(command);
    }

    private static void NotifyStarted(IIpcObserver? observer, string command)
    {
        if (observer is null) return;
        try { observer.OnCommandStarted(command); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private static void NotifyCompleted(IIpcObserver? observer, string command, long elapsedMs)
    {
        if (observer is null) return;
        try { observer.OnCommandCompleted(command, elapsedMs); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private static void NotifyFailed(IIpcObserver? observer, string command, long elapsedMs, Exception exception)
    {
        if (observer is null) return;
        try { observer.OnCommandFailed(command, elapsedMs, exception); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private static void NotifyDenied(IIpcObserver? observer, string command)
    {
        if (observer is null) return;
        try { observer.OnCommandDenied(command); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
