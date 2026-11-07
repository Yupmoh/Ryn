using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Ryn.Plugins.Sidecar;

/// <summary>Delegates readiness to trusted host code.</summary>
public sealed class SidecarCallbackReadinessProbe : ISidecarReadinessProbe
{
    private readonly Func<SidecarProbeContext, CancellationToken, ValueTask> _callback;

    public SidecarCallbackReadinessProbe(Func<SidecarProbeContext, CancellationToken, ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
    }

    public ValueTask WaitUntilReadyAsync(SidecarProbeContext context, CancellationToken cancellationToken) =>
        _callback(context, cancellationToken);
}

/// <summary>Delegates authenticated service identity verification to trusted host code.</summary>
public sealed class SidecarCallbackIdentity : ISidecarIdentity
{
    private readonly Func<SidecarProbeContext, CancellationToken, ValueTask<string>> _callback;

    public SidecarCallbackIdentity(Func<SidecarProbeContext, CancellationToken, ValueTask<string>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
    }

    public ValueTask<string> VerifyAsync(SidecarProbeContext context, CancellationToken cancellationToken) =>
        _callback(context, cancellationToken);
}

/// <summary>Delegates graceful stop to a trusted host-defined protocol.</summary>
public sealed class SidecarCallbackStopProtocol : ISidecarStopProtocol
{
    private readonly Func<SidecarProbeContext, CancellationToken, ValueTask> _callback;

    public SidecarCallbackStopProtocol(Func<SidecarProbeContext, CancellationToken, ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
    }

    public ValueTask RequestStopAsync(SidecarProbeContext context, CancellationToken cancellationToken) =>
        _callback(context, cancellationToken);
}

/// <summary>Attaches only through a trusted host callback that performs service authentication.</summary>
public sealed class SidecarCallbackExternalConnector : ISidecarExternalConnector
{
    private readonly Func<SidecarProbeContext, CancellationToken, ValueTask<SidecarProcessIdentity?>> _callback;

    public SidecarCallbackExternalConnector(
        Func<SidecarProbeContext, CancellationToken, ValueTask<SidecarProcessIdentity?>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
    }

    public ValueTask<SidecarProcessIdentity?> TryAttachAsync(
        SidecarProbeContext context,
        CancellationToken cancellationToken) => _callback(context, cancellationToken);
}

/// <summary>Waits for a bounded regular-expression match in captured stdout or stderr.</summary>
public sealed class SidecarLogReadinessProbe : ISidecarReadinessProbe
{
    private readonly Regex _pattern;
    private readonly TimeSpan _pollInterval;
    private readonly SidecarLogChannel? _stream;

    public SidecarLogReadinessProbe(
        Regex pattern,
        SidecarLogChannel? stream = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        _pattern = pattern;
        _stream = stream;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(25);
        if (_pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public async ValueTask WaitUntilReadyAsync(SidecarProbeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        while (true)
        {
            var logs = context.GetLogs();
            foreach (var entry in logs)
            {
                if ((_stream is null || entry.Channel == _stream) && _pattern.IsMatch(entry.Text))
                    return;
            }

            if (context.Process is { HasExited: true })
                throw new InvalidOperationException("Sidecar exited before the readiness log predicate matched.");

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Authenticated HTTP identity/readiness probe. The response body must equal the expected identity and the
/// caller-supplied bearer token is sent on every probe; a bare open port is never accepted as identity.
/// </summary>
public sealed class SidecarAuthenticatedHttpProbe : ISidecarIdentity, ISidecarReadinessProbe, IDisposable
{
    private const int MaxIdentityBytes = 4096;
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _expectedIdentity;
    private readonly string _bearerToken;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _pollInterval;

    public SidecarAuthenticatedHttpProbe(
        Uri endpoint,
        string expectedIdentity,
        string bearerToken,
        TimeSpan? requestTimeout = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Sidecar HTTP probes require an absolute HTTP or HTTPS endpoint.", nameof(endpoint));
        }
        if (!IPAddress.IsLoopback(ResolveLoopback(endpoint.Host)))
            throw new ArgumentException("Sidecar HTTP probes must target a loopback address.", nameof(endpoint));
        if (Encoding.UTF8.GetByteCount(expectedIdentity) > MaxIdentityBytes)
            throw new ArgumentOutOfRangeException(nameof(expectedIdentity), "Expected sidecar identity exceeds the probe limit.");

        _endpoint = endpoint;
        _expectedIdentity = expectedIdentity;
        _bearerToken = bearerToken;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(2);
        if (_requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        if (_pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));

        _handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        };
        _client = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async ValueTask<string> VerifyAsync(SidecarProbeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        var requestToken = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxIdentityBytes)
            throw new InvalidDataException("Sidecar identity response exceeds the probe limit.");

        var body = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
        await using var bodyDispose = body.ConfigureAwait(false);
        var buffer = new byte[MaxIdentityBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(total), requestToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }
        if (total > MaxIdentityBytes)
            throw new InvalidDataException("Sidecar identity response exceeds the probe limit.");

        var identity = Encoding.UTF8.GetString(buffer, 0, total).Trim();
        if (!string.Equals(identity, _expectedIdentity, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Sidecar returned an unexpected authenticated identity.");
        return identity;
    }

    public async ValueTask WaitUntilReadyAsync(SidecarProbeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        while (true)
        {
            try
            {
                _ = await VerifyAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException ||
                (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (context.Process is { HasExited: true })
                    throw new InvalidOperationException("Sidecar exited before its authenticated HTTP probe became ready.", ex);
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    private static IPAddress ResolveLoopback(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (IPAddress.TryParse(host, out var literal))
            return literal;
        throw new ArgumentException("Sidecar HTTP probe host must be localhost or a loopback IP literal.", nameof(host));
    }
}
