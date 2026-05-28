using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Ryn.Core.Internal;

#pragma warning disable CA5380 // Cert store install is intentional for self-signed HTTPS
#pragma warning disable CA1031 // Catch-all in cleanup is intentional

internal sealed class LocalWebServer : IAsyncDisposable
{
    private WebApplication? _app;
    private X509Certificate2? _cert;
    private RynWebView? _webView;
    private readonly string _contentDirectory;
    private readonly bool _useHttps;

    public string Url { get; private set; } = "";

    internal LocalWebServer(string contentDirectory, bool useHttps)
    {
        _contentDirectory = Path.GetFullPath(contentDirectory);
        _useHttps = useHttps;
    }

    internal void SetWebView(RynWebView webView) => _webView = webView;

    internal async Task StartAsync()
    {
        var port = FindAvailablePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        if (_useHttps)
        {
            _cert = GenerateSelfSignedCert();
            if (OperatingSystem.IsWindows())
                InstallCertToUserStore(_cert);

            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Loopback, port, o => o.UseHttps(_cert));
            });
        }
        else
        {
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Listen(IPAddress.Loopback, port);
            });
        }

        _app = builder.Build();

        _app.MapPost("/ipc/cmd/{id}/{command}", HandleIpcCommand);
        _app.MapPost("/ipc/eval/{id}/{ok}", HandleIpcEval);

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
                var html = await File.ReadAllTextAsync(indexPath).ConfigureAwait(false);
                var bust = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    .ToString(CultureInfo.InvariantCulture);
                html = html.Replace(".css\"", $".css?v={bust}\"", StringComparison.Ordinal)
                           .Replace(".js\"", $".js?v={bust}\"", StringComparison.Ordinal);

                context.Response.ContentType = "text/html";
                context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                await context.Response.WriteAsync(html).ConfigureAwait(false);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        });

        await _app.StartAsync().ConfigureAwait(false);

        var scheme = _useHttps ? "https" : "http";
        Url = $"{scheme}://localhost:{port}";
    }

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task HandleIpcCommand(HttpContext ctx)
    {
        if (_webView is null)
        {
            ctx.Response.StatusCode = 503;
            return;
        }

        var idStr = ctx.Request.RouteValues["id"] as string ?? "0";
        var commandStr = ctx.Request.RouteValues["command"] as string ?? "";

        if (!long.TryParse(idStr, out var id))
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var command = Uri.UnescapeDataString(commandStr);
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);

        ctx.Response.StatusCode = 200;
        _webView.DispatchCommandFromServer(id, command, body);
    }

    private async Task HandleIpcEval(HttpContext ctx)
    {
        if (_webView is null)
        {
            ctx.Response.StatusCode = 503;
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

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }

        if (_useHttps && OperatingSystem.IsWindows())
            RemoveCertFromUserStore();

        _cert?.Dispose();
        _cert = null;
    }
}
