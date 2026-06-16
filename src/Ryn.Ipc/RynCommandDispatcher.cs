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
                ThrowIfShadowed(command, i);

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

    /// <summary>
    /// Fails loudly when a command id is claimed by more than one registered router.
    /// <para>
    /// The source generator's RYN006 diagnostic only catches duplicate command names within a
    /// single compilation. When two routers come from different assemblies (e.g. an app and a
    /// plugin that both expose <c>"fs.readTextFile"</c>), the duplicate is invisible to the
    /// generator and the first registered router would otherwise silently shadow the rest. Rather
    /// than let one win arbitrarily, this surfaces the ambiguity at the moment such a command is
    /// dispatched, naming the conflicting command and both routers so the conflict is fixable.
    /// </para>
    /// </summary>
    /// <param name="command">The command id that matched <paramref name="firstMatchIndex"/>.</param>
    /// <param name="firstMatchIndex">Index of the first router that claimed the command.</param>
    private void ThrowIfShadowed(string command, int firstMatchIndex)
    {
        for (var j = firstMatchIndex + 1; j < _routers.Length; j++)
        {
            if (_routers[j].CanRoute(command))
            {
                var first = _routers[firstMatchIndex].GetType().FullName;
                var second = _routers[j].GetType().FullName;
                throw new InvalidOperationException(
                    $"Command '{command}' is registered by more than one router: " +
                    $"'{first}' and '{second}'. Duplicate command ids across routers are ambiguous; " +
                    "rename one of the conflicting [RynCommand] handlers so each command id is unique.");
            }
        }
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
