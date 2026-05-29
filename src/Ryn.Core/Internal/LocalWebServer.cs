using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Ryn.Core.Internal;

#pragma warning disable CA5380 // Cert store install is intentional for self-signed HTTPS
#pragma warning disable CA1031 // Catch-all in cleanup is intentional

internal sealed class LocalWebServer : IAsyncDisposable
{
    private const long MaxRequestBodyBytes = 32L * 1024 * 1024; // cap IPC/eval request bodies

    private WebApplication? _app;
    private X509Certificate2? _cert;
    private string? _certThumbprint;
    private RynWebView? _webView;
    private readonly string? _contentDirectory;
    private readonly bool _useHttps;
    private readonly string? _allowedCorsOrigin;

    public string Url { get; private set; } = "";

    /// <param name="contentDirectory">Static content root, or null for an IPC-only server (e.g. backing a Vite dev server).</param>
    /// <param name="allowedCorsOrigin">When set (e.g. the dev server origin), cross-origin IPC from that origin is permitted via CORS.</param>
    internal LocalWebServer(string? contentDirectory, bool useHttps, string? allowedCorsOrigin = null)
    {
        _contentDirectory = contentDirectory is null ? null : Path.GetFullPath(contentDirectory);
        _useHttps = useHttps;
        _allowedCorsOrigin = allowedCorsOrigin?.TrimEnd('/');
    }

    internal void SetWebView(RynWebView webView) => _webView = webView;

    internal async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        if (_useHttps)
        {
            // Opt-in HTTPS for the local server. We install a per-process self-signed cert so the webview
            // trusts the loopback origin, but cleanup is keyed to THIS cert's fingerprint (never by CN, which
            // could remove an unrelated "localhost" cert) and also runs on process exit so a crash can't leave
            // a trusted root behind. The default transport remains HTTP-on-loopback + per-launch token.
            _cert = GenerateSelfSignedCert();
            _certThumbprint = _cert.Thumbprint;
            if (OperatingSystem.IsWindows())
                InstallCertToUserStore(_cert);
            if (OperatingSystem.IsMacOS())
                InstallCertToMacKeychain(_cert);

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
                k.Listen(IPAddress.Loopback, 0, o => o.UseHttps(_cert));
            });
        }
        else
        {
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
                k.Listen(IPAddress.Loopback, 0);
            });
        }

        _app = builder.Build();

        // Cross-origin dev support (e.g. Vite): answer CORS preflight and tag responses so a loopback dev
        // origin can call IPC. Only the configured dev origin is allowed.
        if (_allowedCorsOrigin is not null)
            _app.MapMethods("/ipc/{**rest}", ["OPTIONS"], HandleCorsPreflight);

        _app.MapPost("/ipc/cmd/{id}/{command}", HandleIpcCommand);
        _app.MapPost("/ipc/eval/{id}/{ok}", HandleIpcEval);

        if (_contentDirectory is null)
        {
            // IPC-only mode — no static content to serve.
            await _app.StartAsync().ConfigureAwait(false);
            ResolveUrl();
            return;
        }

        var fileProvider = new PhysicalFileProvider(_contentDirectory);
        _app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                ctx.Context.Response.Headers["Pragma"] = "no-cache";
                ctx.Context.Response.Headers["Expires"] = "0";
            }
        });
        _app.MapFallback(async context =>
        {
            var indexPath = Path.Combine(_contentDirectory, "index.html");
            if (File.Exists(indexPath))
            {
                // Serve index.html verbatim. The previous blind ".css\"/.js\"" string replacement could
                // corrupt content (e.g. inside strings/scripts); we rely on no-cache headers instead.
                var html = await File.ReadAllTextAsync(indexPath).ConfigureAwait(false);

                context.Response.ContentType = "text/html";
                context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                await context.Response.WriteAsync(html).ConfigureAwait(false);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        });

        await _app.StartAsync().ConfigureAwait(false);
        ResolveUrl();
    }

    private void ResolveUrl()
    {
        var scheme = _useHttps ? "https" : "http";
        var addresses = _app!.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        var address = addresses?.Addresses.FirstOrDefault();
        if (address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri))
            Url = $"{scheme}://localhost:{uri.Port}";
        else
            Url = address ?? $"{scheme}://localhost";
    }

    private Task HandleCorsPreflight(HttpContext ctx)
    {
        ApplyCorsHeaders(ctx);
        ctx.Response.StatusCode = 204;
        return Task.CompletedTask;
    }

    private void ApplyCorsHeaders(HttpContext ctx)
    {
        if (_allowedCorsOrigin is null) return;
        var origin = ctx.Request.Headers.Origin.ToString();
        if (string.Equals(origin.TrimEnd('/'), _allowedCorsOrigin, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
            ctx.Response.Headers["Vary"] = "Origin";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Ryn-Token";
        }
    }

    private async Task HandleIpcCommand(HttpContext ctx)
    {
        if (_webView is null)
        {
            ctx.Response.StatusCode = 503;
            return;
        }

        if (!IsRequestAuthorized(ctx))
        {
            ctx.Response.StatusCode = 403;
            return;
        }

        var commandStr = ctx.Request.RouteValues["command"] as string ?? "";
        var command = Uri.UnescapeDataString(commandStr);
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);

        // Dispatch and return the result inline on the response body (no second eval hop).
        var (ok, data) = await _webView.DispatchCommandFromServerAsync(command, body).ConfigureAwait(false);

        ctx.Response.StatusCode = ok ? 200 : 500;
        ctx.Response.ContentType = ok ? "application/json" : "text/plain";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ApplyCorsHeaders(ctx);
        await ctx.Response.WriteAsync(data).ConfigureAwait(false);
    }

    private async Task HandleIpcEval(HttpContext ctx)
    {
        if (_webView is null)
        {
            ctx.Response.StatusCode = 503;
            return;
        }

        if (!IsRequestAuthorized(ctx))
        {
            ctx.Response.StatusCode = 403;
            return;
        }

        var idStr = ctx.Request.RouteValues["id"] as string ?? "0";
        var okStr = ctx.Request.RouteValues["ok"] as string ?? "0";

        if (!long.TryParse(idStr, out var evalId) || !int.TryParse(okStr, out var ok))
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);

        _webView.HandleEvalFromServer(evalId, ok, body);
        ctx.Response.StatusCode = 200;
    }

    /// <summary>
    /// Defends the loopback IPC endpoints against other local processes and cross-origin/DNS-rebinding
    /// pages: requires the per-launch bridge token, a loopback Host, and (when present) a same-origin Origin.
    /// </summary>
    private bool IsRequestAuthorized(HttpContext ctx)
    {
        // Per-launch token minted in the webview and embedded in the bridge.
        var token = _webView?.IpcToken;
        if (string.IsNullOrEmpty(token)) return false;
        var presented = ctx.Request.Headers["X-Ryn-Token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(presented),
                System.Text.Encoding.UTF8.GetBytes(token)))
            return false;

        // Host must be loopback (mitigates DNS rebinding).
        var host = ctx.Request.Host.Host;
        if (!IsLoopbackHost(host)) return false;

        // If an Origin is present it must also be loopback.
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin)
            && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !IsLoopbackHost(originUri.Host)))
            return false;

        return true;
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.Ordinal)
        || host.Equals("::1", StringComparison.Ordinal)
        || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static X509Certificate2 GenerateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(sanBuilder.Build());

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));

        var pfxBytes = cert.Export(X509ContentType.Pfx, "");
        return X509CertificateLoader.LoadPkcs12(pfxBytes, "", X509KeyStorageFlags.EphemeralKeySet);
    }

    private static void InstallCertToUserStore(X509Certificate2 cert)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cert);
    }

    private string? _macCertPath;

    private void InstallCertToMacKeychain(X509Certificate2 cert)
    {
        try
        {
            _macCertPath = Path.Combine(Path.GetTempPath(), $"ryn_cert_{Environment.ProcessId}.pem");
            var pem = cert.ExportCertificatePem();
            File.WriteAllText(_macCertPath, pem);

            var psi = new System.Diagnostics.ProcessStartInfo("security")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("add-trusted-cert");
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add("trustAsRoot");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("ssl");
            psi.ArgumentList.Add(_macCertPath);

            var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void RemoveCertFromMacKeychain()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("security")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            // Delete by SHA-1 fingerprint of THIS cert, not by common name — deleting by "localhost"
            // could remove an unrelated certificate the user installed.
            psi.ArgumentList.Add("delete-certificate");
            psi.ArgumentList.Add("-Z");
            psi.ArgumentList.Add(_certThumbprint ?? "");

            if (string.IsNullOrEmpty(_certThumbprint))
                return;

            var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        if (_macCertPath is not null)
        {
            try { File.Delete(_macCertPath); }
            catch (IOException) { }
        }
    }

    private void RemoveCertFromUserStore()
    {
        if (_cert is null) return;
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(_cert);
        }
        catch (Exception)
        {
            // Best effort cleanup — cert may already be removed
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => CleanupCert();

    private void CleanupCert()
    {
        if (!_useHttps) return;
        if (OperatingSystem.IsWindows())
            RemoveCertFromUserStore();
        if (OperatingSystem.IsMacOS())
            RemoveCertFromMacKeychain();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }

        if (_useHttps)
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            CleanupCert();
        }

        _cert?.Dispose();
        _cert = null;
    }
}
