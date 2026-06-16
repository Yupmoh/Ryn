using Microsoft.Extensions.Logging;
using Ryn.Core;

namespace Ryn.Plugins.Updater;

#pragma warning disable CA1812 // Instantiated by DI
internal sealed partial class UpdaterPlugin : IRynPlugin, IAsyncDisposable
#pragma warning restore CA1812
{
    private readonly UpdaterOptions _options;
    private readonly UpdaterService _service;
    private readonly ILogger<UpdaterPlugin> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _startupCheck;

    public UpdaterPlugin(UpdaterOptions options, UpdaterService service, ILogger<UpdaterPlugin> logger)
    {
        _options = options;
        _service = service;
        _logger = logger;
    }

    public string Name => "Updater";

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.CheckOnStartup)
            return ValueTask.CompletedTask;

        // Genuinely fire-and-forget (PAP-05): the check must not delay the window showing. We run it on a
        // background task whose lifetime we track (cancelled + awaited in DisposeAsync) so it can't outlive the
        // app or leak an unobserved fault. The HttpClient timeout (UpdaterOptions.HttpTimeout) bounds a slow or
        // unreachable host, and every failure is logged via ILogger — never Console.
        _startupCheck = Task.Run(() => RunStartupCheckAsync(_cts.Token), CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    private async Task RunStartupCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var update = await _service.CheckForUpdateAsync(cancellationToken).ConfigureAwait(false);
            if (update is not null)
                LogUpdateAvailable(update.Version, update.ReleaseUrl);
        }
        catch (OperationCanceledException)
        {
            // App shutting down or the request timed out — nothing to report.
        }
        catch (HttpRequestException ex)
        {
            LogCheckFailed(ex);
        }
        catch (System.Security.SecurityException ex)
        {
            // A redirect to a disallowed host, a non-HTTPS asset URL, etc. Log, don't crash startup.
            LogCheckFailed(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_startupCheck is not null)
        {
            try
            {
                await _startupCheck.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or System.Security.SecurityException)
            {
                // Already handled/expected inside the check; swallow on teardown.
            }
        }
        _cts.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Update available: {version} ({releaseUrl})")]
    private partial void LogUpdateAvailable(string version, Uri releaseUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Startup update check failed.")]
    private partial void LogCheckFailed(Exception exception);
}
